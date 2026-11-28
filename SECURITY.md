# Security

## Supported versions

Security fixes go into a new release of the newest version. Older versions do not get fixes of their own.

| Version | Fixes |
|---|---|
| The newest release | Yes |
| Older releases | No: update TrackVice, then publish the new version to your agents |

TrackVice updates itself from Settings > Updates or the status bar. Installed agents update themselves within 6 hours of a version you publish, see [New TrackVice versions](docs/AGENT.md#new-trackvice-versions). Agents in an SCCM task sequence get the version of their package.

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for a vulnerability.

- Report it privately on GitHub: [Report a vulnerability](https://github.com/Baldissera88/TrackVice/security/advisories/new) (the repository's Security tab, private vulnerability reporting).
- Or send an e-mail to dbal.itx@gmail.com.

Say what is affected, the version, and how to reproduce it. Leave out real upload links, viewer links, customer codes, tokens, tenant names and device names; made-up ones are enough. When it is fixed, the release notes say so.

## What the agent does on a device

The agent is TrackVice.exe itself, installed where you choose to install it, by an Intune platform script, a Win32 app or an SCCM task sequence step.

- It copies itself to `C:\Program Files\TrackVice Agent` and registers the scheduled task **TrackVice Agent**, which runs it as SYSTEM.
- It reads the device: the ConfigMgr and Intune Management Extension logs, the Enrollment Status Page registry keys, the Autopilot and MDM event logs and WMI. It does not start, retry or cancel anything.
- It uploads the session, and a zip of the logs, to the one place it was given: a customer's storage container, with a link that can only add files, or a share. Passwords, keys and tokens it recognises are masked in the session and in alerts. The log files in the zip are uploaded as they are.
- It replaces itself only with a TrackVice.exe that is named in an `agent/latest.json` signed with the update key it was installed with, and that has the signed size and SHA-256.
- It collects extra diagnostics only for a request signed with the same key, for that one device and once. A request holds no commands: the agent runs only the fixed Windows tools that [Collect diagnostics](docs/AGENT.md#collect-diagnostics) lists.
- It removes its task, its saved links and its registry key when its time is up: by default 48 hours after the deployment is done, and at the latest 30 days after it was installed.

## The trust model

- **Your PC is trusted.** The update key and the account key of your storage stay there, encrypted for your Windows account. Whoever runs code as you on that PC can sign updates for every device that trusts that key, so keep that PC as you would keep an administrator's PC.
- **Storage is not trusted.** Anyone with a customer code or an upload link can put files in that customer's container. The viewer and the agents treat everything read from storage, from a share or from a log as data: sessions, zips, blob names, `agent/latest.json` and requests. A file that is not signed with the update key cannot make a device run anything.
- **The links are secrets.** The customer code, the upload link, the viewer link, the alert link and the account key each give access as [What is uploaded, and security](docs/AGENT.md#what-is-uploaded-and-security) describes. Keep them like passwords. Do not paste them in issues, tickets or chats.
- **The device's administrators are trusted.** The agent's folder, task and registry key can only be changed by administrators and SYSTEM. A standard user on the device cannot change what the agent runs.
- **The network is not trusted.** TrackVice talks to GitHub, Microsoft Graph, Azure and Key Vault over HTTPS. Downloads are checked against a signed manifest (agents) or `SHA256SUMS.txt` (TrackVice itself) before they are run.
- **GitHub is trusted for TrackVice's own updates.** `SHA256SUMS.txt` comes from the same release as the files, so it shows that a download is complete and unchanged on the way, not who made the release.

## Checking a download

Each release has `SHA256SUMS.txt`, with one `<hash>  <file>` line for `TrackVice.msi`, `TrackVice.exe` and the scripts in `agent/`. Download it from the [release page](https://github.com/Baldissera88/TrackVice/releases/latest) with the file, then compare.

In PowerShell:

```powershell
$want = (Select-String -Path .\SHA256SUMS.txt -Pattern '  TrackVice\.msi$').Line.Split(' ')[0]
$have = (Get-FileHash -Path .\TrackVice.msi -Algorithm SHA256).Hash
if ($have -eq $want) { 'OK' } else { 'The hash does not match. Do not run this file.' }
```

On Linux or macOS, in the folder with the downloads:

```sh
sha256sum --check --ignore-missing SHA256SUMS.txt
```

The hashes are also in the release notes. For an agent setup with links, the hash of `TrackVice.exe` is the `$Sha256` the platform script needs. When a release is code signed, the file properties and the UAC prompt also show the publisher.
