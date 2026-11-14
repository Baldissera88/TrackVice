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

   - **WiX Toolset 3 on Windows.** `release.ps1` runs `candle.exe -arch x64` and `light.exe` itself, after signing the exe. `.\build.ps1 -Msi` does the same without signing. WiX 3 should handle the parts `patch_msi.sh` adds for wixl, long file names and closing a running TrackVice before an upgrade, but this path has not been tried with this `.wxs` yet. WiX 3 has been out of community support since February 2025; WiX 4 and later use a different source format (`wix convert` converts it) and are not used here.
   - **Linux or WSL with `package.sh`.** This is how the MSI has been built so far. Build it from the same commit, then give it to `release.ps1`:

     ```sh
     ./package.sh
     ```

     ```powershell
     .\release.ps1 -Thumbprint <thumbprint> -MsiPath \\wsl$\Ubuntu\home\<you>\TrackVice\TrackVice.msi
     ```

     `release.ps1` checks that its version and product code match `TrackVice.wxs`. The exe inside this MSI is the one Mono built, unsigned; only the MSI itself is signed.

   Check the MSI on a test device: install, upgrade over the previous version with TrackVice running, uninstall.

7. **Hashes.** `release.ps1` writes `out\release\<version>\SHA256SUMS.txt` and `out\release\<version>\release-notes.md`: the changelog section, a table of the files with their SHA-256, the product code and the install and uninstall commands. Read the notes before publishing.

8. **The GitHub release.** Run `release.ps1` again with `-Publish` (and `-Prerelease` for a preview). It builds and signs again, so the hashes in the notes are those of the uploaded files. It creates the tag `v<version>` on the pushed commit and the release "TrackVice &lt;version&gt;" with `TrackVice.exe`, `TrackVice.msi` and `agent/Install-TrackViceAgent.ps1`. Then download the files from the release and compare them with `Get-FileHash`.

9. **The agent.** Devices with the agent check the hash of the `TrackVice.exe` they download, so a new version needs two changes, as in [Maintenance](AGENT.md#maintenance) in docs/AGENT.md:

   - Upload the new `TrackVice.exe` over the old one in the `trackvice` storage container. If the script downloads from the GitHub release instead, change the version in `$Download`.
   - Update `$Sha256` in the platform script in Intune (Devices > Scripts and remediations > Platform scripts) with the hash of `TrackVice.exe` from the release notes. Until then the script fails on purpose.

   For the Win32 app route, upload a new .intunewin with the new exe.

10. **winget.** After the release is published, with the same `TrackVice.msi`:

    ```powershell
    .\winget\New-Manifests.ps1 -Validate
    winget settings --enable LocalManifestFiles
    winget install --manifest .\out\winget\manifests\d\DanielBaldissera\TrackVice\<version>
    .\winget\New-Manifests.ps1 -Submit
    ```

    It writes the version, installer and en-US locale manifests (schema 1.12.0) for `DanielBaldissera.TrackVice`, with the MSI's SHA-256 and the product code from `TrackVice.wxs`, and validates them with `winget validate`. Install from the manifest on a test device before submitting. `-Submit` opens a pull request to microsoft/winget-pkgs with `wingetcreate submit`; give `-Token` or store a token once with `wingetcreate token --store`. On Linux, `winget/manifests.sh <version> TrackVice.msi <notes file> out/winget` writes the same files. Do not submit pre-releases.

## Before a release: device checklist

Go through [docs/TESTING.md](TESTING.md) on real devices for the parts the release changes, and at least:

- [ ] The idle device test (1) on Windows 10 and Windows 11.
- [ ] The parts the changelog mentions, with their test from TESTING.md.
- [ ] The agent (9): install with the new hash, an upload, the device in the list, removal after its time.
- [ ] The MSI: a clean install, an upgrade over the previous release with TrackVice open, an uninstall.
- [ ] The signed exe and MSI show the publisher in the file properties and the UAC prompt.
- [ ] Anything that failed is in the changelog or fixed before the release.
