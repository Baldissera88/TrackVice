<p align="center"><img src="docs/images/logo.png" width="96" alt="TrackVice logo"></p>

# TrackVice

TrackVice follows a deployment on one Windows device while it happens: an SCCM task sequence, SCCM applications, packages and updates, Windows Autopilot with the Enrollment Status Page, and Intune Win32 apps. It shows every phase and every step, app, update and policy in it, what each one is doing right now, and the log line behind it.

It is a companion to [LogVice](https://github.com/Baldissera88/LogVice) and [PolicyVice](https://github.com/Baldissera88/PolicyVice): one portable exe, no installs, no cloud sign-in, and the log behind any step opens in LogVice with one click.

![TrackVice following an Autopilot deployment](docs/images/trackvice-autopilot.png)

> **Version 0.0.1 is the first preview.** Everything below works with the built-in demos and has tests, but it has not run on a real device yet. The log formats of the Intune Management Extension and the ESP registry layout are not documented by Microsoft, so expect some lines to be missed until it has been tested on real deployments. [docs/TESTING.md](docs/TESTING.md) is the checklist for that.

## What it shows

| Part | Contents |
|---|---|
| Overall | How far the deployment has come, its state and how long it has run |
| Counts | Items, in progress, waiting, failed and findings. Click one to filter the list |
| Phase rail | Each phase as a card with its state, count, time and progress. Click one to jump to it |
| Progress | Every item under its phase, with a live spinner, download progress, tries, duration and its last event |
| Details | For the selected item: findings, the result with the error decoded, every attempt, the command line, IDs, its events and the raw log line |
| Timeline | Every event from every source, newest first, with a filter for errors and warnings or the selected item |
| Findings | What needs attention, with what happened, what to check and the evidence |
| Session | The device, the deployment (task sequence, Autopilot profile, tenant, join type, ESP time limit), and every log and key that was read |

## What it follows

| Deployment | Phases | Read from |
|---|---|---|
| SCCM task sequence | Windows PE, full Windows | `smsts.log` in all its places as it moves: `X:\Windows\Temp\SMSTSLog`, `_SMSTaskSequence\Logs\Smstslog`, `CCM\Logs\SMSTSLog` and `CCM\Logs` |
| SCCM applications | Applications | `AppEnforce.log`, `AppDiscovery.log`, `AppIntentEval.log`, and `CCM_Application` in `root\ccm\ClientSDK` |
| SCCM packages and programs | Packages | `execmgr.log` and `CCM_Program` |
| SCCM content | Content downloads | `CAS.log` |
| SCCM software updates | Software updates | `UpdatesDeployment.log`, `UpdatesHandler.log` and `CCM_SoftwareUpdate` |
| Autopilot and the ESP | Device preparation, device setup, account setup | `HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking`, the Autopilot profile, the ESP time limit, and the Autopilot event log |
| Intune Win32 apps | Intune apps, or the ESP phase that waits for them | `IntuneManagementExtension.log`, `AppWorkload.log`, `AgentExecutor.log` and the `Win32Apps` key |
| MDM and Entra | On the timeline | The DeviceManagement Admin, User Device Registration and AAD event logs, errors and warnings only |

Every source is mapped to one set of states: waiting, downloading, installing, detecting, restart needed, done, failed, skipped and not applicable. The original code stays on the event. Win32 app names come from the IME logs, the same way PolicyVice finds them.

## Findings

| Finding | Meaning |
|---|---|
| **Failed** | An item failed. The error is decoded: Windows Installer codes, HRESULTs, and the ConfigMgr codes that come up most, with a hint where one helps |
| **Not detected after install** | The installer finished with a success code, but the detection rule did not find the app afterwards (0x87D00324 in SCCM, 0x87D1041C in Intune) |
| **Retry loop** | An item was tried three times or more and at least two attempts failed |
| **Restart needed** | An item installed and is waiting for a restart (3010, 1641, pending soft or hard reboot) |
| **ESP time limit** | Device setup or account setup has run past the time limit in the ESP profile. Lists what it is still waiting for |
| **No progress** | While watching: an item has been downloading or installing for 15 minutes without any source saying anything about it |
| **Task sequence failed** | The task sequence ended with an error |
| **MDM errors** | The DeviceManagement Admin event log has errors from the deployment |

![Findings for a failed task sequence](docs/images/trackvice-findings-dark.png)

## Where to run it

| Where | How to start it | What it sees |
|---|---|---|
| Windows, as administrator | Start menu, or the portable exe | Everything |
| Autopilot OOBE and the ESP | Shift+F10, then run `TrackVice.exe` from a USB stick or a share | ESP, Autopilot, IME logs, event logs |
| Task sequence, full Windows part | F8 command prompt (command support must be enabled on the boot image) | smsts.log, and the ConfigMgr logs and WMI once the client is installed |
| Task sequence, Windows PE part | F8 command prompt | smsts.log only, and only if the boot image has the WinPE-NetFx component |

**Compact** opens a small window that stays on top: the phase, what is installing now with its progress, overall progress and the counts. It is made for OOBE screens and for watching over someone's shoulder.

<p align="center"><img src="docs/images/trackvice-compact.png" width="480" alt="The compact view"></p>

## Watch, save and share

**Watch** reads the device every 2 seconds: new log lines, ESP tracking changes and new event log records every time, and the Win32 app registry state and ConfigMgr client WMI every third time. It reads the last 24 hours when it starts; **Open > History to read** changes that.

**Save > Save the session** writes everything as one JSON file that opens on any PC. **Save > Collect the session and its logs in a zip** adds every log file the session has a line from, so a supporter can send the evidence instead of the device. **Open > Open a folder of logs** reads a CCM Logs folder, an IME Logs folder or an unpacked bundle; it reads every log TrackVice knows in it and its subfolders, including rolled-over copies.

**Export** writes an HTML report with the phases, findings and timeline, or every event as CSV.

![The timeline with the raw line behind an event](docs/images/trackvice-timeline.png)

## Try it without a deployment

`TrackVice.exe --demo` plays an Autopilot deployment: a user-driven Entra join where a VPN client hits error 1618 twice before it installs, an app fails with 1603, and Adobe Acrobat Reader never finishes and runs into the ESP time limit.

`TrackVice.exe --demo osd` plays an SCCM task sequence through Windows PE and full Windows, with content downloads, software updates that need a restart, and an application that installs with exit code 0 but is not detected afterwards, which fails the task sequence.

Both demos are written as real log lines and ESP registry values and read through the same code as a real device, at 10 times the speed. The screenshots in this README come from them.

![The task sequence demo in the Vice theme](docs/images/trackvice-osd-vice.png)

## Command line

```text
TrackVice.exe                          Watch this device
TrackVice.exe <session.json>           Open a saved session
TrackVice.exe <folder>                 Read a folder of logs
TrackVice.exe /report <file.html>      Save an HTML report of this device and exit
TrackVice.exe /csv <file.csv>          Save every event as CSV and exit
TrackVice.exe /session <file.json>     Save the session as JSON and exit
  /folder <path>                       Read a folder of logs instead of this device
  /hours <n>                           Hours of history to read (default 24)
TrackVice.exe --demo [osd]             Play a demo
```

The exit code is 0 when done, 1 on an error and 2 when failed items were found, so it works in a script or an Intune remediation. In PowerShell, use `Start-Process -Wait -PassThru` to get the exit code.

## Keyboard

| Key | Action |
|---|---|
| F5 | Read everything again |
| Ctrl+W | Start or pause watching |
| Ctrl+F | Search items, IDs and command lines |
| Ctrl+O | Open a saved session |
| Ctrl+S | Save the session |
| Ctrl+E | Export an HTML report |
| Ctrl+L | Open the selected item's log in LogVice |
| Ctrl+M | Compact view |
| Arrow keys | Move through the list; Left and Right fold and unfold a phase |
| F1 | About and the command line |

## Privacy and safety

TrackVice only reads. It never starts, retries or cancels a deployment, and writes nothing to the device except its own settings in `HKCU\Software\TrackVice` and the files you save. It makes no network connections.

## Limitations

- Version 0.0.1 has not been tested on a real device yet. See the note at the top.
- LogVice opens a log file but cannot yet jump to a line, so TrackVice says in the status bar which line to look for.
- The Intune Management Extension log format changes between IME versions. Lines TrackVice does not recognise are left out, and the ESP and Win32Apps registry keys still give the state.
- The Windows PE part of a task sequence needs WinPE-NetFx in the boot image for TrackVice to start there.
- User-targeted SCCM applications are only fully visible from the user's own session.
- The exe and MSI are unsigned. SmartScreen warns on downloaded copies, and AppLocker or WDAC need them signed with your own certificate.

## Build from source

TrackVice is plain C# 5 and Windows Forms on .NET Framework 4.5, with no NuGet packages, like LogVice and PolicyVice.

**On Windows**, with the compiler that ships with .NET Framework:

```powershell
.\build.ps1          # TrackVice.exe
.\build.ps1 -Msi     # also TrackVice.msi, needs WiX Toolset 3
```

**On Linux or macOS**, with Mono and msitools:

```sh
./build.sh           # TrackVice.exe
./package.sh         # TrackVice.exe and TrackVice.msi
./tests/run.sh       # the tests
```

| Path | Contents |
|---|---|
| `src/Model.cs` | Session, phases, items, attempts, events, evidence, findings; JSON save and load |
| `src/Track.cs` | Observations, the tracker that builds the session, findings and error decoding |
| `src/CmLog.cs` | CMTrace and simple ConfigMgr log parsing, and the tailer that follows a growing log |
| `src/Sccm.cs` | smsts.log, AppEnforce, AppDiscovery, AppIntentEval, execmgr, CAS and update logs |
| `src/Intune.cs` | IME logs, ESP tracking, Win32 app state and the Autopilot profile |
| `src/Ime.cs` | The Win32 app registry reader and error codes, shared with PolicyVice |
| `src/Live.cs` | Reads this device: log discovery, registry, ConfigMgr client WMI, event logs; and folders of logs |
| `src/Engine.cs` | Ties a source (device, folder, file, demo) to the session |
| `src/Report.cs` | HTML, CSV and the zip bundle |
| `src/Demo.cs` | The two demos, written as real log lines and registry values |
| `src/Ui.*.cs` | The window, the phase rail, the progress list, the compact view and the themes |
| `tests/` | Log parsing and tailing, every parser, the tracker, findings, save and load, and both demos end to end |
| `art/` | The icon, drawn in the same style as the other Vice icons |

## License

TrackVice is released under the [MIT license](LICENSE).

## Author

Developed by **Daniel L. Baldissera**.
