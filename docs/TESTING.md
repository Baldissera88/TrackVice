# Testing TrackVice on a real device

For TrackVice 2.0.0.

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

- [ ] Devices > Intune / cloud > Autopilot in Intune > Sign in gives a code, and signing in at microsoft.com/devicelogin works. If consent is needed, note the message.
- [ ] The list matches Devices > Monitor > Windows Autopilot deployments in the Intune admin center: devices, states and start times.
- [ ] Start an Autopilot deployment. It shows as In progress, and how long after the device it changes.
- [ ] Watch one device: device setup and account setup states and durations match the report.
- [ ] Apps and policies are listed with the right status. Note whether "the ESP waits for" matches the ESP profile's blocking apps.
- [ ] A failed deployment shows the failure text from Intune.
- [ ] After more than an hour of watching it still updates (the sign-in is renewed).

## 8. Other devices: task sequences in SCCM

- [ ] Devices > SCCM / on-premises > Task sequences in SCCM connects with only the SMS Provider name, and finds the site code.
- [ ] The list matches Monitoring > Deployments for the task sequence. Check the Started time against the console: a shift of whole hours means the time zone handling is wrong.
- [ ] Watch a running task sequence: steps appear in order under their groups, a little after they finish on the device.
- [ ] Note the exact status message names for a completed, failed and skipped step, and for the task sequence ending. TrackVice reads the state from these names.
- [ ] A failed step shows its exit code and its output in the evidence.
- [ ] A device that starts from PXE as an unknown computer shows up, and under which name.

## 9. The agent

Use a test subscription, a test tenant and a test device or virtual machine. [docs/AGENT.md](AGENT.md) has the setup.

**Your storage**

- [ ] **Settings > Your storage** says it is not set up. **Devices > Add customer** opens the Your storage guide first.
- [ ] Sign in to Azure, pick a subscription, and set up a new account. Before anything is made, TrackVice shows what it will make and asks. The Azure portal then has the account (StorageV2, Standard_LRS, HTTPS only, TLS 1.2, no anonymous access) and the private container `trackvice-agent`.
- [ ] `trackvice-agent` has `agent/latest.json` and `agent/TrackVice-<version>.exe` of the running version. Settings shows the account, when the links expire and the published version.
- [ ] Set up again with **Existing account**: the account keeps its settings, and nothing is published again.
- [ ] `HKCU\Software\TrackVice` holds no account key or link in plain text.
- [ ] **Remove** asks first, forgets the setup on this PC, and leaves the account in Azure as it was.

**Add customer, in your storage**

- [ ] **Devices > Demos > Add customer** goes through Customer, Sign in and Create with made-up data.
- [ ] **Devices > Add customer**: enter a name such as Contoso and an alert link. A link that does not start with `https://` is refused.
- [ ] Sign in to the test tenant. Before Create, nothing is in Azure or Intune.
- [ ] Create: the container `tv-contoso` is in your storage, private. The group **TrackVice - Autopilot devices** is dynamic with the ZTDId rule. The platform script **TrackVice agent** runs in system context, 64-bit, without a signature check, and is assigned to the group.
- [ ] The result page copies the customer code and the script, and **Open** opens `Documents\TrackVice\Scripts\Install-TrackViceAgent-Contoso.ps1`. Its only filled line is `$Customer`.
- [ ] Add a second customer in the same tenant: the group is used as it is, and TrackVice asks before it replaces the script's content. No keeps the content.
- [ ] Add a customer with **Skip: I add the script to Intune myself**: Intune is not changed, and the result page says so.
- [ ] An account without consent for `Group.ReadWrite.All`: the storage, code and script are made, and the result page says what was not done in Intune.
- [ ] **Devices with the TrackVice agent** connects to the new customer without a link to paste.

**Add customer, in the customer's own Azure**

- [ ] Tick **Store this customer's data in their own Azure**. Step 2 asks for the Azure sign-in, subscription and account as well.
- [ ] Create: the customer's account has the containers `trackvice` and `trackvice-agent` and the lifecycle rule `trackvice-cleanup`. Your storage has no new container.
- [ ] The device of that customer uploads to the customer's `trackvice`, and downloads TrackVice from your `trackvice-agent`.

**The one-line script on a new Autopilot device**

- [ ] `TrackVice.exe /agent-install /customer "<code>"` as administrator on a test PC returns at once, the task TrackVice Agent runs as SYSTEM, and the PC shows in the list within a minute.
- [ ] `C:\Program Files\TrackVice Agent\agent.log` says it started, uploaded and that self-update is on.
- [ ] `HKLM\SOFTWARE\TrackVice\Agent` has the upload link, the alert link, the update key, the update link and the customer name. A standard user cannot read it.
- [ ] `/agent-remove` removes the task and the key.
- [ ] Reset an Autopilot device of the test tenant. The script runs during device setup, and the device shows in the list. Note when, compared with the ESP, and whether device preparation is in the session.
- [ ] The script's output in Intune, or `IntuneManagementExtension.log`, says the TrackVice version and that the agent is installed.
- [ ] After a restart the device continues as the same row in the list. Watching it shows the same items as TrackVice run on it.
- [ ] A device enrolled long ago that is in the group: the script says "Not a new enrollment" and installs nothing.
- [ ] Put a cut code in `$Customer`, and a link: the script stops with "could not be read" and "not a TrackVice customer code".
- [ ] Publish from another Windows user with a new key, then run the script: it says `agent/latest.json` is not signed with the update key in the customer code. Publish again from your own Windows user afterwards.

**A new version**

- [ ] Start a newer TrackVice. The status bar says it is not published to your storage, with **Publish**. Nothing is published before the click.
- [ ] Click **Publish**: `agent/latest.json` names the new version, and the status bar line goes away. **Settings > Your storage** shows the new published version.
- [ ] An installed agent takes the new version after a restart, or within 6 hours, and stays the same row in the list.
- [ ] A new device gets the new version directly from the script.
- [ ] An older TrackVice does not offer to publish.

**Customer code…**

- [ ] **Settings > Customers**: the customer made with Add customer has **Customer code…**, and one made otherwise has not.
- [ ] The code is masked, and **Copy** copies the whole code.
- [ ] Change the alert link and save: the code changes, and the saved script has the new code.
- [ ] Sign in and **Update the script in Intune**: it asks first, the platform script **TrackVice agent** has the new content, and its assignment is kept.
- [ ] A device that runs the new script sends its alerts to the new link.

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
- [ ] With an alert link in the customer code, or `$Alert` in a script with links, a failed app gives one card in the channel, with the device, the deployment and what to do. A restart of the device does not send it again.
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

For a setup with links, as in [docs/AGENT.md](AGENT.md#agent-updates), with a test device or a virtual machine. A setup with a customer code is checked in section 9.

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

## 24. Power BI

- [ ] Statistics > **Export for Power BI** writes `Enrollments.csv`, `Items.csv`, `Findings.csv` and `README.txt`. Load the folder in Power BI as the README says, and relate the tables on SessionId.
- [ ] `TrackVice.exe /export-data C:\Temp\export /upload "<viewer link>" /hours 168` writes the same files.

## 25. New customer with links

For existing setups. Use a test subscription and a test tenant.

- [ ] **Devices > Demos > New customer** goes through every step with made-up data.
- [ ] **Devices > New customer with links** with a new storage account: the resource group, the account, both containers and the lifecycle rule are in the Azure portal as the guide said.
- [ ] Again with the same customer and the existing account: the account keeps its settings and other lifecycle rules are kept.
- [ ] The five links have the permissions in [docs/AGENT.md](AGENT.md#new-customer-with-links), and the account key is not saved anywhere on the PC.
- [ ] Upload says the download link reads TrackVice.exe. The test alert arrives in Teams.
- [ ] The saved script is a copy of `agent/Install-TrackViceAgent-Links.ps1` with the links, the SHA-256 and `$MaxAgeHours`, and installs the agent on a test device.
- [ ] The Intune part adds the platform script **TrackVice agent**, 64-bit, system context, assigned to the chosen group.
- [ ] After Finish, the customer is in Settings > Customers and **Devices with the TrackVice agent** connects without a new link.
- [ ] `agent/New-TrackViceCustomer.ps1` with the same values makes the same storage, links and script.
- [ ] An account without a role on the subscription gets a message that says what it may not do.

## 26. Welcome and Getting started

Use a Windows user that has not started TrackVice before, or remove `WelcomeSeen` from `HKCU\Software\TrackVice`.

1. Start TrackVice. The welcome page shows the four choices, and nothing is read from Intune or Azure before a click.
2. **Follow this device** watches this PC. **Open logs or a session** offers both. **Look around** plays the Autopilot demo.
3. **Follow devices from my PC** asks for your own organization or for customers, and Getting started uses the words of the choice.
4. Each step opens its guide. After Your storage and Add customer, those steps are ticked. After a test device uploads and the list is opened, the last two are ticked, and the line under the toolbar goes away.
5. **Don't show again**: the next start opens without the welcome. **Help > Welcome** and **Help > Getting started** show them again.
6. At Shift+F10 during the ESP, in a task sequence and as SYSTEM: no welcome, TrackVice follows the device at once.

## 27. Devices: Intune / cloud and SCCM / on-premises

1. The switch at the top of the Devices menu changes the entries to the other side. A restart of TrackVice keeps the side.
2. With nothing set up, Intune / cloud says what it needs and offers **Add customer**, and SCCM / on-premises offers **Enter the SMS Provider**.
3. **SMS Provider connection** saves the server with the customer in use. Switching customers switches the site.
4. **Add the agent to a task sequence** copies the command with the customer code of the customer in use.
5. **Devices > Demos** has the demos of the side in use and those for both.
6. Statistics with Intune, SCCM and Both: the counts match the enrollments of each kind. A task sequence followed by Autopilot counts under both.
7. `TrackVice.exe /fleet-report fleet.html /upload "<viewer link>" /side sccm` lists only task sequences. `/side x` stops with a message.

## 28. Updates from GitHub

Use a PC where the previous release is installed with the MSI, and a copy of the exe in a folder.

1. **Settings > Updates > Check now** says the newer version, and when it last checked.
2. The status bar says "TrackVice <version> is out" at the next start, at most once a day.
3. **Install** with the MSI: Windows asks for administrator rights, TrackVice closes, installs and opens again in the new version.
4. **Install** with the copy in a folder: the exe in that folder is replaced and opens again.
5. After the update, the status bar offers to publish the new version to Your storage.
6. Set `DisableUpdateCheck` = 1 (DWORD) under `HKLM\SOFTWARE\Policies\TrackVice`: Settings > Updates says it is turned off by your organization, and no connection to `api.github.com` is made.
7. As the agent, at Shift+F10 during the ESP and in a task sequence: no connection to `api.github.com`.
8. The release has `SHA256SUMS.txt`, and its hashes match `Get-FileHash` of the MSI, the exe and the three scripts.

## 29. Task sequences in SCCM: one row per run

1. Run the same task sequence deployment twice on one test device. The list has one row for the newest run, with its own Started and Duration, as the console shows them.
2. **Show earlier runs** adds the first run as a row of its own, and the status line counts the runs left out when it is off.
3. Open each run: it shows only the steps of that run.
4. A run that restarts into Windows stays one run.
5. Status messages the device sends after Windows PE show in the run with the time of their step, after a Refresh.

## 30. Add customer: an existing name, the assignment and the script message

1. Add customer with the name of a customer that exists: Next asks first. **Choose another name** goes back to the name with it selected. **Update** goes on.
2. Step 3 offers the three assignments. **Create the group TrackVice - Autopilot devices** is the default.
3. **Assign to a group I choose**: searching a word of a group's name lists security groups, each marked dynamic or assigned. Create assigns the script to that group and makes no group.
4. **Do not assign**: the script is in Intune without an assignment, and no group is made. The result page says to assign it.
5. Put the customer's name in `$Customer` and run the script: it says the value looks like a customer name and where to copy the code.

## 31. TrackVice 2.0.0: Your storage, Key Vault, names, downloads and the narrow overview

1. **Your storage, the subscription.** Sign in to Azure with an account that has more than one subscription. Pick another subscription: the box, the **ID** line under it, the resource groups and the storage accounts all change to that subscription. **Existing account** lists its accounts, or says "This subscription has no storage accounts." with what Azure answered. Repeat in **Add customer** and **New customer with links**.
2. **Your storage, a new account.** With Reader only, **New account** fails with a message that names Contributor or Owner. With Contributor it makes the account.
3. **Key Vault, customers.** On a vault with Azure RBAC, **Sign in**, **Save customers to the vault** needs Key Vault Secrets Officer, **Get customers from the vault** needs Key Vault Secrets User. A secret changed after your last sync asks before it is overwritten. Nothing is deleted in the vault.
4. **Key Vault, my setup.** **Save my setup to the vault** makes `trackvice-setup-storage` and `trackvice-setup-updatekey`. On a second PC (or Windows user), **Get my setup from the vault** gives Your storage and the key, and Publish works there. With User on the first secret only, TrackVice says the update key is denied and still gets the rest.
5. **Publish with another key.** Publish a version from PC A. On PC B, which has its own key, Publish says it is signed with another update key and offers **Get my setup from the vault**.
6. **Names from Intune.** Each switch alone: a script, a remediation, an app and a configuration profile show their name. A switch without its permission fails alone.
7. **Downloads.** With **Allow downloading scripts and app details** off, there is no **Download…** in the right-click menu. Turn it on: a platform script, a remediation and an app can be downloaded after the question, secrets are hidden, and `downloads.log` has one line each, without the content.
8. **The narrow overview.** Make the window 700 pixels wide: Overview and a followed deployment keep all their tiles, which shrink and wrap, and all phases stay visible.

## What to send back

For anything wrong: a screenshot, the session (Save > Collect the session and its logs in a zip), and which log line TrackVice should have read differently.
