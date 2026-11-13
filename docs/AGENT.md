# The TrackVice agent

Intune and SCCM only know what a device has reported. The agent is TrackVice itself, running on the device without a window while it deploys. It reads the device the way the window does and uploads the whole session as it changes: every app, policy and task sequence step, with the log lines, registry values and events behind them. **Devices > Devices with the TrackVice agent** lists the uploads and follows one live.

Status: preview. The agent, the upload and the list have run against the Azure Storage emulator, a folder and the demos. They have not yet run on a real device or a real storage account.

## How it works

| | |
|---|---|
| Install | `TrackVice.exe /agent-install /upload "<location>"` copies TrackVice to `C:\Program Files\TrackVice Agent`, saves the location, registers the scheduled task **TrackVice Agent** (SYSTEM, at startup), starts it and returns within seconds |
| While it runs | Reads the device every 2 seconds. Uploads when something changed, at most every 10 seconds, and every 30 seconds otherwise so the list shows it is alive |
| Restarts | The task starts the agent again. It reads everything since Windows was installed and continues the same session |
| Idle | With nothing running and nothing new for 30 minutes it reads every 15 seconds and uploads every 5 minutes. It wakes up when something starts, such as account setup after pre-provisioning |
| End | After 48 hours (`/keep <hours>`) it uploads one last time and removes its task and the saved location. The folder with the exe and `agent.log` stays |
| Remove | `"C:\Program Files\TrackVice Agent\TrackVice.exe" /agent-remove`, then delete the folder |

The agent changes nothing else on the device.

The first minutes of a deployment happen before Intune can run anything on the device. They are still in the session, because the agent reads the logs and the ESP registry keys back to the Windows install.

## What is uploaded

One file per device, `sessions/<id>.json.gz`, the same format as a saved session. It holds the computer name, the Autopilot profile and tenant, app and policy names, install command lines, exit codes and the log lines about them. Install command lines can hold secrets if a package passes them that way. Treat the container like the logs themselves.

## 1. The storage container

In a storage account, create a container, for example `trackvice`. Then create three links:

| Link | Made from | Permissions | Used by |
|---|---|---|---|
| Upload | The container: Shared access tokens | Create, Write | The devices |
| Viewer | The container: Shared access tokens | Read, List | You, in TrackVice |
| Download | The blob `TrackVice.exe`: Generate SAS | Read | The platform script |

Use HTTPS only and set an expiry you will remember to renew. With the upload link a device can add sessions but cannot list or read those of other devices.

Upload `TrackVice.exe` to the container and note its hash:

```powershell
(Get-FileHash .\TrackVice.exe -Algorithm SHA256).Hash
```

A lifecycle management rule on the storage account that deletes blobs under `sessions/` after 30 days keeps the container small.

## 2a. Platform script (recommended)

The Intune Management Extension runs platform scripts before it installs Win32 apps, so the agent runs from the start of device setup.

1. Open `agent/Install-TrackViceAgent.ps1` and fill in `$Download`, `$Sha256` and `$Upload`.
2. Intune admin center > Devices > Scripts and remediations > Platform scripts > Add > Windows 10 and later.
3. Run this script using the logged on credentials: **No**. Enforce script signature check: **No**. Run script in 64 bit PowerShell Host: **Yes**.
4. Assign it to the device group your Autopilot devices are in.

The script downloads `TrackVice.exe`, checks its hash and runs `/agent-install`. A platform script runs once per device. After a reset it runs again.

## 2b. Win32 app

Use this when you want the Enrollment Status Page to track the agent, or when scripts are not allowed.

| | |
|---|---|
| Content | `TrackVice.exe` |
| Install command | `TrackVice.exe /agent-install /upload "<upload link>"` |
| Uninstall command | `"%ProgramFiles%\TrackVice Agent\TrackVice.exe" /agent-remove` |
| Install behavior | System |
| Detection rule | File `%ProgramFiles%\TrackVice Agent\TrackVice.exe` exists |

It starts when the Intune Management Extension reaches it, which can be after other apps.

## 3. Follow the devices

In TrackVice: **Devices > Devices with the TrackVice agent**, paste the viewer link and connect. The list refreshes every 15 seconds. Watching a device reads its session every 5 seconds. The viewer link is saved encrypted for your Windows account.

**Demo: devices with the agent** shows the view with made-up devices.

## A folder instead of a storage account

The location can also be a folder or a share: `/agent-install /upload "\\server\share\TrackVice"`. The device writes as its computer account, so this is for domain-joined devices and for testing. To try the whole chain on one PC:

```powershell
.\TrackVice.exe /agent /upload C:\Temp\TrackVice /once
```

Then connect to `C:\Temp\TrackVice` in the list.

## Good to know

- `TrackVice.exe` is not code signed. Application control such as WDAC or Smart App Control can block it.
- The devices need HTTPS access to the storage account. A storage firewall has to allow them.
- `C:\Program Files\TrackVice Agent\agent.log` says when the agent started and why an upload failed.
- The upload link is saved under `HKLM\SOFTWARE\TrackVice\Agent`, readable by administrators and SYSTEM only, and removed when the agent ends.
