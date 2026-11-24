# Releasing TrackVice

How a release is made, on the author's Windows PC. `release.ps1` does the build, signing, hashes and the GitHub release in one run. Nothing here signs or publishes anything by itself: the certificate stays on that PC, and `-Publish` is always given by hand.

## What you need

- Windows with .NET Framework 4.5 or later. `build.ps1` uses the C# compiler that ships with it.
- Git and the [GitHub CLI](https://cli.github.com/) (`gh auth login` once).
- For the MSI: [WiX Toolset 3](https://github.com/wixtoolset/wix3/releases) (3.14), or Linux/WSL with Mono and msitools for `package.sh`. See step 6.
- For signing: `signtool.exe` from the Windows SDK, and a code signing certificate or an Azure Trusted Signing account. See step 5.
- For winget: `winget` and `wingetcreate` (`winget install wingetcreate`).

## Steps

1. **Bump the version.** On a clean `main`:

   ```powershell
   .\bump.ps1 1.2.0
   ```

   or `./bump.sh 1.2.0` on Linux. It writes the version to `TrackVice.wxs` with a new product code (the upgrade code stays, so the MSI upgrades the old version), `src/AssemblyInfo.cs` and `tests/Stubs.cs`, and adds a dated section to the top of `CHANGELOG.md`. It refuses a version that is not higher than the current one.

2. **Write the changelog.** Fill in the new section in `CHANGELOG.md` in the same style as the others. It becomes the release notes and the winget release notes. `release.ps1` stops while the section has only the date.

3. **Build.** `.\build.ps1`, or `./build.sh` on Linux. `release.ps1` builds again in step 5, so this is only to check.

4. **Test.** Run `./tests/run.sh` on Linux or WSL; it must end with `0 failed`. Go through the device checklist below for anything that changed. Then commit and push:

   ```powershell
   git commit -am "TrackVice 1.2.0"
   git push
   ```

5. **Build and sign.** Look first at what would happen. `-WhatIf` runs every check and changes nothing:

   ```powershell
   .\release.ps1 -Thumbprint <thumbprint> -WhatIf
   ```

   It stops when the tree has changes, when `TrackVice.wxs`, `src/AssemblyInfo.cs` and the top of `CHANGELOG.md` give different versions, or when the tag `v<version>` already exists. Then run it without `-WhatIf`, with one of:

   | Signing | Parameters |
   |---|---|
   | Certificate in the store, or on a token | `-Thumbprint <thumbprint>` |
   | Certificate in a .pfx file | `-PfxPath <file> -PfxPassword (Read-Host -AsSecureString)` |
   | Azure Trusted Signing | `-TrustedSigningMetadata <metadata.json>` |
   | No signing | nothing |

   It signs `TrackVice.exe` first and builds the MSI around the signed exe, then signs the MSI. Every signature uses SHA-256 and an RFC 3161 time stamp (`-TimestampUrl`, by default `http://timestamp.acs.microsoft.com` for Trusted Signing and `http://timestamp.digicert.com` for a certificate; use the server of your CA). After signing, it checks each file with `signtool verify /pa` and `Get-AuthenticodeSignature`, and stops when a signature is not valid or has no time stamp.

   For Trusted Signing, install the client tools (`winget install -e --id Microsoft.Azure.ArtifactSigningClientTools`), sign in with `az login`, and write a `metadata.json`:

   ```json
   {
     "Endpoint": "https://weu.codesigning.azure.net",
     "CodeSigningAccountName": "<account>",
     "CertificateProfileName": "<certificate profile>"
   }
   ```

   The endpoint must be the region of the account. A wrong region gives 403 Forbidden. If the tools are not found, give the path of the x64 `Azure.CodeSigning.Dlib.dll` with `-TrustedSigningDlib`.

   **Which kind of signing.** All three give the same signature to Windows. What differs is how you get it:

   - An **OV or EV certificate** from a CA. The key has to be on a hardware token or in a cloud HSM, so signing is tied to that token or service. Since 2024, an EV certificate no longer gets SmartScreen reputation right away; it gets no head start over OV there.
   - **Azure Trusted Signing** (now also called Artifact Signing). Microsoft validates your identity and holds the key; the certificates last three days and are renewed for you, which is why the time stamp is required. It needs an Azure subscription, and not every country and kind of publisher can be validated yet. Check before relying on it.

   With any of them, SmartScreen still warns on a new file until enough people have run it without problems. The reputation builds over time and follows the publisher, so the warning becomes rarer with each release from the same signer. An unsigned file is judged by its own hash, so every new version starts again from nothing.

6. **The MSI.** `TrackVice.wxs` is plain WiX 3 source, so there are two ways:

   - **WiX Toolset 3 on Windows.** `release.ps1` runs `candle.exe -arch x64` and `light.exe` itself, after signing the exe. `.\build.ps1 -Msi` does the same without signing. WiX 3 should handle the parts `patch_msi.sh` adds for wixl, long file names and closing a running TrackVice before an upgrade. WiX 3 has been out of community support since February 2025; WiX 4 and later use a different source format (`wix convert` converts it) and are not used here.
   - **Linux or WSL with `package.sh`.** Build it from the same commit, then give it to `release.ps1`:

     ```sh
     ./package.sh
     ```

     It ends with `sums.sh`, which writes `SHA256SUMS.txt` next to the MSI and the exe, to check the build there. `release.ps1` writes the one that is published, in step 7.

     ```powershell
     .\release.ps1 -Thumbprint <thumbprint> -MsiPath \\wsl$\Ubuntu\home\<you>\TrackVice\TrackVice.msi
     ```

     `release.ps1` checks that its version and product code match `TrackVice.wxs`. The exe inside this MSI is the one Mono built, unsigned; only the MSI itself is signed.

   Check the MSI on a test device: install, upgrade over the previous version with TrackVice running, uninstall.

7. **Hashes.** `release.ps1` writes `out\release\<version>\SHA256SUMS.txt` and `out\release\<version>\release-notes.md`: the changelog section, a table of the files with their SHA-256, the product code and the install and uninstall commands. Read the notes before publishing.

   `SHA256SUMS.txt` has one `<hash>  <file>` line, as `sha256sum` writes it, for `TrackVice.msi`, `TrackVice.exe`, `Install-TrackViceAgent.ps1`, `Install-TrackViceAgent-Links.ps1` and `New-TrackViceCustomer.ps1`. Every release must have it: TrackVice installs an update from Settings > Updates or the status bar only when the downloaded MSI or exe has the hash this file gives. A release without it is only offered as a download.

8. **The GitHub release.** Run `release.ps1` again with `-Publish` (and `-Prerelease` for a pre-release). It builds and signs again, so the hashes in the notes are those of the uploaded files. It creates the tag `v<version>` on the pushed commit and the release "TrackVice &lt;version&gt;" with `TrackVice.exe`, `TrackVice.msi`, `SHA256SUMS.txt` and the scripts `agent/Install-TrackViceAgent.ps1`, `agent/Install-TrackViceAgent-Links.ps1` and `agent/New-TrackViceCustomer.ps1`. Check that all six files are attached. When you make or change a release by hand, attach `SHA256SUMS.txt` and all three agent scripts yourself: without `SHA256SUMS.txt`, TrackVice offers the release only as a download. Then download the files from the release and compare them with `Get-FileHash` and `SHA256SUMS.txt`. Do not mark a normal release as a pre-release: TrackVice asks GitHub for the latest release, which leaves pre-releases out.

9. **The agent.** Start the new TrackVice on your PC and click **Publish** in the status bar, or **Settings > Your storage > Publish**. New devices get it from the script at once, and installed agents within 6 hours. The script with the customer code needs no change. See [New TrackVice versions](AGENT.md#new-trackvice-versions) in docs/AGENT.md.

   For setups with links, as in [Maintenance](AGENT.md#maintenance): publish with **Publish agent update**, or upload the new `TrackVice.exe` over the old one and update `$Sha256` in `agent/Install-TrackViceAgent-Links.ps1` in Intune with the hash from the release notes. For the Win32 app route, upload a new .intunewin with the new exe.

10. **winget.** After the release is published, with the same `TrackVice.msi`:

    ```powershell
    .\winget\New-Manifests.ps1 -Validate
    winget settings --enable LocalManifestFiles
    winget install --manifest .\out\winget\manifests\d\DanielBaldissera\TrackVice\<version>
    .\winget\New-Manifests.ps1 -Submit
    ```

    It writes the version, installer and en-US locale manifests (schema 1.12.0) for `DanielBaldissera.TrackVice`, with the MSI's SHA-256 and the product code from `TrackVice.wxs`, and validates them with `winget validate`. Install from the manifest on a test device before submitting. `-Submit` opens a pull request to microsoft/winget-pkgs with `wingetcreate submit`; give `-Token` or store a token once with `wingetcreate token --store`. On Linux, `winget/manifests.sh <version> TrackVice.msi <notes file> out/winget` writes the same files. Do not submit pre-releases.

## Screenshots for the documentation

The pictures in `docs/images/` come from the demos, so they hold no real data. Retake them when the look of a window changes, and for every major version, so the version in the status bar is right (the current ones show TrackVice 2.0.0).

Each picture is one command. `--page` chooses the window (the names are in the `--page` comments in `src/`, for example `overview`, `getstarted`, `yourstorage`, `addcustomer`, `keyvault`, `vaultsetup`, `names`, `downloads-confirm`, `publish`, `stats`, `compare`, `wall`, `tsagent`), `--tab` the step or tab, `--size` the size of the window and `--theme` the theme:

```sh
cd /tmp
xvfb-run -a -s "-screen 0 1600x1000x24" mono /path/to/TrackVice.exe --demo --at 40 --size 1400x900 --theme vice --shot docs/images/trackvice-autopilot.png
xvfb-run -a -s "-screen 0 1600x1000x24" mono /path/to/TrackVice.exe --size 900x700 --theme vice --page yourstorage --tab 0 --shot docs/images/trackvice-your-storage.png
```

Look at every picture before you keep it. On Linux the window leaves a dark strip along the bottom, so crop it off. Leave out `--demo` for dialogs, so that no tooltip of the main window shows in the picture, and run the exe from a short path, because pages like Publish show it. Keep the pictures as PNG with at most 256 colors, which keeps them small.

## Before a release: device checklist

Go through [TESTING.md](TESTING.md) on real devices for the parts the release changes, and at least:

- [ ] The idle device test (1) on Windows 10 and Windows 11.
- [ ] The parts the changelog mentions, with their test from TESTING.md.
- [ ] The agent (9): install with the new hash, an upload, the device in the list, removal after its time.
- [ ] The MSI: a clean install, an upgrade over the previous release with TrackVice open, an uninstall.
- [ ] The update from the previous release, once published: Settings > Updates > Check now, then Install, with the MSI install and with a copy in a folder. TrackVice opens again in the new version and offers to publish it to Your storage.
- [ ] The signed exe and MSI show the publisher in the file properties and the UAC prompt.
- [ ] The pictures in `docs/images/` show this version, see Screenshots for the documentation.
- [ ] Anything that failed is in the changelog or fixed before the release.
