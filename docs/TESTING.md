# Testing TrackVice on a real device

This checklist compares what it shows with the logs themselves during real deployments. Note anything it misses or gets wrong, together with the log lines around it, so the parsers can be fixed.

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
- [ ] A step that rolls smsts.log over (a long task sequence, or one with many restarts): no step is missing and none is counted twice.

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

## 5. Apps after the ESP

- [ ] A Win32 app assigned after enrollment shows under Apps, in Device, after the ESP or User, after the ESP. An available app shows under Available in Company Portal and is not counted as waiting.
- [ ] The ESP step Apps in device setup and account setup says which app it waits for.
- [ ] On a device whose time zone changes during the deployment (an en-US image moved to another region), the Started time in the header matches when the deployment started, not hours before.
- [ ] A retried install counts its attempts.

## 6. Saving and sharing

- [ ] Save the session, open it on another PC: same items, states and findings.
- [ ] Collect the zip, unpack it, and open the folder: the items come back from the logs alone.
- [ ] The HTML report opens in a browser in light and dark mode, with the summary, findings, time line, details per phase and events.
- [ ] Print the report, or print it to PDF: the print layout is readable on paper, without dark backgrounds.
- [ ] `TrackVice.exe /report report.html /title "CONTOSO-PC01 after the upgrade"` writes a report with that title and returns 2 when something failed.

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

## 9. The agent

Start with a folder on one PC, then a storage container, then a real Autopilot deployment. [docs/AGENT.md](AGENT.md) has the setup.

- [ ] `TrackVice.exe /agent /upload C:\Temp\TrackVice /once` as administrator writes one session, and Devices > Devices with the TrackVice agent lists it from that folder.
- [ ] The same with the upload link of a storage container, and the viewer link in the list. Note any error text.
- [ ] `/agent-install /upload "<upload link>"` as administrator returns at once, the task TrackVice Agent runs as SYSTEM, and the device shows in the list within a minute.
- [ ] `C:\Program Files\TrackVice Agent\agent.log` says it started and uploaded.
- [ ] A standard user cannot read `HKLM\SOFTWARE\TrackVice\Agent`.
- [ ] After a restart the device continues as the same row in the list.
- [ ] The platform script on an Autopilot device: when the device first shows in the list compared with the ESP, and whether device preparation is in the session.
- [ ] Watching the device shows the same items as TrackVice run on it.
- [ ] `/agent-remove` removes the task and the key.

## 10. Scripts, Office, MSI apps and policies

- [ ] Every platform script assigned to the device is under Platform scripts, with the right result. A failed one shows its error text.
- [ ] Microsoft 365 Apps shows as installing while it installs and done after, with the right products.
- [ ] MSI line-of-business apps show with their product name once installed.
- [ ] Policies lists the policy areas, and the settings under one area match the profile in Intune.
- [ ] What the ESP waits for matches the ESP profile: blocking apps, certificates, Wi-Fi.
- [ ] Account setup after the user signs in: note anything missing compared with the ESP screen.

To collect the same data for a parser fix, run this on the device as administrator and send the zip:

```cmd
md C:\tv
xcopy C:\ProgramData\Microsoft\IntuneManagementExtension\Logs C:\tv\IME\ /e /y
reg export "HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking" C:\tv\esp.reg /y
reg export "HKLM\SOFTWARE\Microsoft\IntuneManagementExtension" C:\tv\ime.reg /y
reg export "HKLM\SOFTWARE\Microsoft\EnterpriseDesktopAppManagement" C:\tv\msi.reg /y
reg export "HKLM\SOFTWARE\Microsoft\OfficeCSP" C:\tv\office.reg /y
wevtutil epl Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin C:\tv\mdm-admin.evtx
powershell Compress-Archive C:\tv\* C:\tv.zip
```

TrackVice opens the unpacked folder directly.

## 11. Gantt

- [ ] The Gantt tab shows one bar per item, under its phase, at the times in the progress list.
- [ ] A running item's bar grows while you watch.
- [ ] **Policies** adds and removes the Policies phase.
- [ ] **Only what took time** hides items shorter than a second, but not running ones.
- [ ] Clicking a bar selects the item in the progress list.
- [ ] Scrolling with the mouse wheel and dragging the scroll bar keep the time axis on top.

## 12. Findings that explain

- [ ] A failed Win32 app with 1603 or 1618: the finding says what the code means and what to do.
- [ ] A platform script that fails, set to run in 32-bit PowerShell, with a 64-bit only command such as `Get-WindowsCapability`: the finding has the error text and says to run it in the 64-bit host.
- [ ] An ESP with a short time limit, such as 15 minutes, and a slow app: **near the ESP time limit** shows before the limit passes, with the minutes left.
- [ ] An app that asks for a restart while others wait in the same phase: after 5 minutes, **Waiting for a restart** names it.
- [ ] Three apps in one phase that fail with the same code, for example with the network cut: one finding about the common cause, before the items.

## 13. Autopilot events, device preparation and pre-provisioning

- [ ] Device preparation lists the items the events give: TPM attestation (self-deploying or pre-provisioning), device enrollment by Autopilot, MDM enrollment.
- [ ] The timeline has the Autopilot milestones in plain words, such as the profile download.
- [ ] Hybrid Entra join: offline domain join, the domain controller check and hybrid Entra join show in Device preparation. With the connector stopped, the **Hybrid join** finding names it.
- [ ] Account setup: the user's apps and policies show under account setup after the user signs in.
- [ ] Pre-provisioning: after the technician phase, reseal and leave the device off for more than 10 minutes. The time it was off does not count in the duration, and gives no **No progress** finding. The Session tab says when the technician phase finished and when the user phase started.
- [ ] Autopilot device preparation (v2): the Session tab says it is probably in use, and its apps and scripts show.

## 14. Remediations and Names from Intune

- [ ] Assign a remediation. It shows under Remediations, with its result and exit codes, matching the Intune portal.
- [ ] **Devices > Names from Intune** with each lookup on its own: script, app and remediation names replace the IDs, and the sign-in asks only for the permissions ticked.
- [ ] Configuration profiles of this device: every profile and its state match the device's Device configuration page in Intune.
- [ ] Turn off consent for one permission: that lookup fails with a clear message, and the others still apply.

## 15. Customers

- [ ] **Settings > Customers**: add two customers with a tenant, an upload location and an SCCM site each.
- [ ] Each device list has the customer selector, and switching connects to that customer's tenant, location or site.
- [ ] A Graph sign-in for one tenant is not used for another customer.
- [ ] Export without the storage links: the file has no links. Export with them, import on another PC: the customers come back and connect.
- [ ] `HKCU\Software\TrackVice` does not hold a storage link in plain text.

## 16. Statistics and Compare

- [ ] **Devices > Statistics for devices with the agent** with a few uploaded enrollments: the counts match the list, and the times match the sessions.
- [ ] Each period (24 h, 7 d, 30 d, 90 d) changes the counts as expected.
- [ ] A failed app shows its fail rate out of the devices where it finished.
- [ ] **Export CSV** opens in Excel with the same rows.
- [ ] Double-clicking an enrollment watches it.
- [ ] **Devices > Compare with another enrollment > A device with the agent**: two enrollments of the same profile, one with a failed app. The failed app and clearly slower items are listed, and the last events of both sides show.
- [ ] **A saved session file** works the same, and **Export** writes a CSV.
- [ ] `TrackVice.exe /fleet-report fleet.html /upload "<viewer link>" /hours 168` writes a report and returns 2 when an enrollment had failed items.

## 17. The agent: alerts, masking and logs

- [ ] Make the Teams workflow as in [docs/AGENT.md](AGENT.md#alerts-in-teams). `/agent /alert "<link>" /alert-test` posts a test card.
- [ ] A link that does not start with `https://` is refused with a clear message.
- [ ] With `$Alert` in the script, a failed app gives one card in the channel, with the device, the deployment and what to do. A restart of the device does not send it again.
- [ ] When the deployment is finished and nothing runs for 30 minutes, a finished card arrives.
- [ ] Give an app an install command with `/password=Secret123` or a script that prints `token=...`: the uploaded session shows `***`. With `/no-mask` it shows the value.
- [ ] After a failure, `logs/<id>.zip` appears in the container. **LogVice > Logs from the device** downloads and opens it, and **Open in LogVice** opens the device's copy of a log.

## 18. The agent in an SCCM task sequence

Set up the steps as in [docs/AGENT.md](AGENT.md#sccm-task-sequences), on a boot image with WinPE-NetFx.

- [ ] The device shows in the list during Windows PE, under its `MININT-` name.
- [ ] After Setup Windows and ConfigMgr it continues as the same row, now under its new name, and searching for the `MININT-` name finds it.
- [ ] After a Restart Computer step it continues as the same row.
- [ ] Steps from Windows PE and from Windows are all in the session, each once.
- [ ] The agent stops by itself when the task sequence ends, or at once with `/agent-stop`, and the last upload has the end.
- [ ] With a wrong upload link, the step fails, and the task sequence goes on because of Continue on error.
- [ ] `%WINDIR%\Temp\TrackVice\agent.log` says when it started, uploaded and stopped.

## 19. Wall view

- [ ] **Devices > Wall view** opens full screen with a tile for every device that uploaded today. F11 switches full screen, Esc closes it.
- [ ] A device that deploys shows its progress and what runs now, and moves on as it uploads.
- [ ] Turn off the network of a running device: after 5 minutes its tile says it has not uploaded.
- [ ] The top line counts running, finished today and failed today, and the average matches the devices.
- [ ] Moving the mouse shows the filters. Running, Failed and Finished today show the right devices, and the search finds a device by name and by profile.
- [ ] A finished device leaves the wall after the time chosen under Finished devices stay. A failed one stays until midnight.
- [ ] Clicking a tile closes the wall and watches that device.
- [ ] `TrackVice.exe --wall` starts in the wall view with the customer in use, and `--wall /upload "<viewer link>"` with that location.

## 20. Agent updates

Set up as in [docs/AGENT.md](AGENT.md#agent-updates), with a test device or a virtual machine.

- [ ] **Settings > Agent updates > Publish agent update > Create key** shows a fingerprint, and **Copy** copies the public key.
- [ ] Install the agent with `$UpdateKey` and `$UpdateLink`. `agent.log` says "Self-update is on".
- [ ] Publish a newer TrackVice.exe. The container has `agent/TrackVice-<version>.exe` and `agent/latest.json`.
- [ ] Restart the device, or wait up to 6 hours: `agent.log` says the version is downloaded and the helper is started, and the agent comes back with the new version as the same row in the list.
- [ ] `C:\Program Files\TrackVice Agent` has `TrackVice.exe.old`, and no `TrackVice-apply.exe` or `update` folder after the next start.
- [ ] Publish the same version again, and an older one: the agent says so and installs nothing.
- [ ] Publish from another Windows user with a new key: `agent.log` says the file is not signed with the update key of this device, and the agent keeps running.
- [ ] Change one byte of the published exe in the container: the agent says the SHA-256 does not match and installs nothing.
- [ ] `TrackVice.exe /publish-agent /to "<link>"` publishes from the command line and prints the public key.
- [ ] `/agent-install` with a key that is cut short fails with a clear message.

## 21. Check devices before enrollment

- [ ] **Devices > Check devices before enrollment**: sign in, paste three serial numbers, one of them not registered. The unregistered one is a problem, the others show their group tag, profile and ESP as the Intune admin center has them.
- [ ] **From Autopilot** lists the registered devices, and the filter finds one by serial number, model or group tag.
- [ ] Paste a hardware hash CSV: the serial numbers are taken from its serial column.
- [ ] A device whose profile has a name template: the name it gives matches what the device gets.
- [ ] With the groups switch on, an ESP assigned to a device group is chosen over the default. Without it, the check says targeted ESPs could not be resolved, when there are any.
- [ ] Connect to the agent list first: the prediction lists the blocking apps with their typical time, and warns when they come near the ESP time limit.
- [ ] **Export CSV** and **Export HTML** save the same results.

## 22. Restarts and downloads

- [ ] A deployment with a restart: the Restarts phase has one item per restart, with who asked for it, when Windows stopped and when it started again.
- [ ] The Gantt tab has a line at the time of each restart.
- [ ] Turn off a virtual machine without shutting it down: the next start shows an unexpected shutdown.
- [ ] A Win32 app shows its download size, time and speed in the details, matching AppWorkload.log.
- [ ] Throttle the network of a virtual machine and install a large app: **Slow download** names it when it took over 5 minutes at under 5 Mbit/s.
- [ ] Statistics shows the download time and speed of Win32 apps.

## 23. Reference and ready to ship

- [ ] Watch a good enrollment and choose **Devices > Use this enrollment as the reference**. The badge says it matches.
- [ ] Watch another enrollment of the same customer with one app less and one failed: the badge counts the differences, and **Differs from the reference** lists them.
- [ ] While an enrollment still runs, items it has not reached are not missing.
- [ ] **Compare with another enrollment > The reference** shows both side by side.
- [ ] Statistics has a Reference column for each enrollment.
- [ ] **Export > Ready to ship report** prints on one page with the verdict, what was installed, the reference check and the lines to sign.
- [ ] `TrackVice.exe /ship-report ship.html /from session.json` returns 0 for a ready device and 2 for one that is not.
- [ ] **Clear the reference** removes the badge and the finding.

## 24. PackVice, NotifyVice and Power BI

- [ ] On a device with a Win32 app installed from an .msi, the details show the Installer section, and **Installer in PackVice** opens the .msi from the IME cache in PackVice.
- [ ] The same for an SCCM application from ccmcache. When the cache was cleaned, the details say why the .msi is not there.
- [ ] Without PackVice installed, TrackVice asks for the exe and keeps the choice in Settings.
- [ ] Watch a device with the agent that finished without failures: **Devices > Tell the user with NotifyVice** starts NotifyVice, and the clipboard has the message.
- [ ] Statistics > **Export for Power BI** writes `Enrollments.csv`, `Items.csv`, `Findings.csv` and `README.txt`. Load the folder in Power BI as the README says, and relate the tables on SessionId.
- [ ] `TrackVice.exe /export-data C:\Temp\export /upload "<viewer link>" /hours 168` writes the same files.

## 25. New customer

Use a test subscription and a test tenant.

- [ ] **Devices > Demo: new customer** goes through every step with made-up data.
- [ ] **Devices > New customer** with a new storage account: the resource group, the account, both containers and the lifecycle rule are in the Azure portal as the guide said.
- [ ] Again with the same customer and the existing account: the account keeps its settings and other lifecycle rules are kept.
- [ ] The five links have the permissions in [docs/AGENT.md](AGENT.md#the-quick-way-new-customer), and the account key is not saved anywhere on the PC.
- [ ] Upload says the download link reads TrackVice.exe. The test alert arrives in Teams.
- [ ] The saved script has the links, the SHA-256 and `$MaxAgeHours`, and installs the agent on a test device.
- [ ] The Intune part adds the platform script **TrackVice agent**, 64-bit, system context, assigned to the chosen group.
- [ ] After Finish, the customer is in Settings > Customers and **Devices with the TrackVice agent** connects without a new link.
- [ ] `agent/New-TrackViceCustomer.ps1` with the same values makes the same storage, links and script.
- [ ] An account without a role on the subscription gets a message that says what it may not do.

## What to send back

For anything wrong: a screenshot, the session (Save > Collect the session and its logs in a zip), and which log line TrackVice should have read differently.
