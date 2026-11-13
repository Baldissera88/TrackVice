# Testing TrackVice on a real device

TrackVice 0.0.1 has only run with its demos. This checklist compares what it shows with the logs themselves during real deployments. Note anything it misses or gets wrong, together with the log lines around it, so the parsers can be fixed.

Everything TrackVice does is read-only.

## Before you start

1. Copy `TrackVice.exe` to a folder only administrators can change, or install `TrackVice.msi`. For OOBE and task sequences, put the portable exe on a USB stick or a share.
2. Have LogVice at hand to compare with the raw logs.
3. Start TrackVice as administrator.

## 1. An idle device

- [ ] The window opens without an error.
- [ ] With nothing deploying, it says so, and the Session tab lists the logs and keys it read.
- [ ] Watch is on, and the status bar says when it last read the device.
- [ ] Open > History to read: 7 days shows older SCCM application installs, if the device had any.

## 2. SCCM application install

Deploy a required application, or install an available one from Software Center.

- [ ] The application shows up under Applications, with the deployment type name, then the application name once WMI is read.
- [ ] Its state goes waiting → downloading or installing → done, at about the times in AppEnforce.log.
- [ ] The command line and the installer exit code match AppEnforce.log.
- [ ] Its content shows under Content downloads, matching CAS.log.
- [ ] Deploy an app with a wrong detection rule. It fails with **Not detected after install** and the installer's exit code.
- [ ] Deploy an app that exits 1603. It fails, and the finding explains 1603.
- [ ] Open in LogVice opens AppEnforce.log.

## 3. SCCM task sequence

Run an OSD task sequence. Start TrackVice with F8 once full Windows runs, or in Windows PE if the boot image has WinPE-NetFx.

- [ ] The task sequence name shows in the header.
- [ ] Steps run in the order of smsts.log, with their exit codes.
- [ ] Steps in Windows PE and in full Windows land in their own phases.
- [ ] Skipped steps show as skipped.
- [ ] Nothing is counted twice when smsts.log moves between folders.
- [ ] A failing step makes **Task sequence failed** and points at the step.
- [ ] Software updates installed by the task sequence show under Software updates, with restart needed where it applies.

## 4. Autopilot

Reset a device and run a user-driven Autopilot deployment with an ESP. Press Shift+F10 at the ESP and start TrackVice from USB.

- [ ] The Autopilot profile, tenant, join type and ESP time limit are right, compared with the Intune portal.
- [ ] Device preparation, device setup and account setup show the apps and policies the ESP waits for.
- [ ] Win32 apps have their names, not only IDs.
- [ ] App states match the ESP screen.
- [ ] Downloads show progress where the IME logs it.
- [ ] Let one app fail. It shows as failed with its exit code from AppWorkload.log or IntuneManagementExtension.log.
- [ ] Let one app run past the ESP time limit. **ESP time limit** names it.
- [ ] Compact view stays on top over the ESP screen and keeps updating.

Export the ESP tracking key for comparison:

```powershell
reg export HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking esp.reg
```

## 5. Intune apps after the ESP

- [ ] A Win32 app assigned after enrollment shows under Intune apps.
- [ ] A retried install counts its attempts.

## 6. Saving and sharing

- [ ] Save the session, open it on another PC: same items, states and findings.
- [ ] Collect the zip, unpack it, and open the folder: the items come back from the logs alone.
- [ ] The HTML report opens in a browser in light and dark mode.
- [ ] `TrackVice.exe /report report.html` writes a report and returns 2 when something failed.

## 7. Other devices: Autopilot in Intune

- [ ] Devices > Autopilot in Intune > Sign in gives a code, and signing in at microsoft.com/devicelogin works. If consent is needed, note the message.
- [ ] The list matches Devices > Monitor > Windows Autopilot deployments in the Intune admin center: devices, states and start times.
- [ ] Start an Autopilot deployment. It shows as In progress, and how long after the device it changes.
- [ ] Watch one device: device setup and account setup states and durations match the report.
- [ ] Apps and policies are listed with the right status. Note whether "the ESP waits for" matches the ESP profile's blocking apps.
- [ ] A failed deployment shows the failure text from Intune.
- [ ] After more than an hour of watching it still updates (the sign-in is renewed).

## 8. Other devices: task sequences in SCCM

- [ ] Devices > Task sequences in SCCM connects with only the SMS Provider name, and finds the site code.
- [ ] The list matches Monitoring > Deployments for the task sequence. Check the Started time against the console: a shift of whole hours means the time zone handling is wrong.
- [ ] Watch a running task sequence: steps appear in order under their groups, a little after they finish on the device.
- [ ] Note the exact status message names for a completed, failed and skipped step, and for the task sequence ending. TrackVice reads the state from these names.
- [ ] A failed step shows its exit code and its output in the evidence.
- [ ] A device that starts from PXE as an unknown computer shows up, and under which name.

## What to send back

For anything wrong: a screenshot, the session (Save > Collect the session and its logs in a zip), and which log line TrackVice should have read differently.
