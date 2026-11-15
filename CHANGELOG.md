# Changelog

## TrackVice 1.1.0

15 November 2026.

- Devices > Wall view: every device with the agent on one screen, full screen, as a tile with its progress, what runs now and its last upload. A summary of running, finished and failed today, filters for running, failed and finished today, a search, and finished devices that stay 1 to 8 hours. Click a tile to watch it. `--wall [/upload <location>]` starts in it.
- The agent updates itself. Settings > Agent updates > Publish agent update uploads a TrackVice.exe with a signed `agent/latest.json`, or `/publish-agent /to <location>` from the command line. Agents installed with `$UpdateKey` and `$UpdateLink` (`/update-key` and `/update`) check at start and every 6 hours, and install a newer version only when its signature, size and SHA-256 match. See docs/AGENT.md.
- Devices > Check devices before enrollment: for serial numbers you type, paste or pick from Autopilot, whether each is registered in Windows Autopilot, which deployment profile and Enrollment Status Page it gets, and whether the ESP is likely to run past its time limit, going by earlier enrollments. Export as CSV or HTML. Reads `DeviceManagementServiceConfig.Read.All`, with `DeviceManagementApps.Read.All` and `Device.Read.All` when their switches are on.
- Restarts from the System event log in a phase of their own and on the timeline: who asked for each, when the device went down and came back, and unexpected shutdowns. The Gantt tab draws a line at each restart.
- Win32 apps show their download size, time and speed, and a **Slow download** finding when a download took over 5 minutes at under 5 Mbit/s. Statistics has a Download column.
- Devices > Use this enrollment as the reference: one reference enrollment per customer. Every enrollment you watch for that customer gets a badge and a **Differs from the reference** finding, Statistics a Reference column, and Compare with another enrollment a third choice, The reference.
- Export > Ready to ship report, and `/ship-report <file.html> [/from <session.json>]`: one page that says whether a device can go to its user, checked against the reference, with room for the technician to sign. Exit code 2 when it is not ready.
- PackVice: Open the installer in PackVice opens the .msi a Win32 app or SCCM application was installed from, when it is still in the IME cache or ccmcache on this PC. NotifyVice: Devices > Tell the user with NotifyVice, for a device with the agent that finished without failures.
- Statistics > Export for Power BI, and `/export-data <folder> /upload <location>`: Enrollments.csv, Items.csv and Findings.csv with a README that says how to load them.
- Devices > New customer: a guide that makes the storage account, the containers, the lifecycle rule and the links in Azure, uploads TrackVice.exe, fills in the platform script and can add it to Intune. `agent/New-TrackViceCustomer.ps1` does the same with the Az modules, without Intune. See docs/AGENT.md.

## TrackVice 1.0.5

14 November 2026.

- An app whose MSI product code is not on the device yet says so in plain words: Detection: not installed yet.
- An error from Intune shows its sentence only, not the JSON around it, and a server error says it is usually passing.

## TrackVice 1.0.4

14 November 2026.

- Fixed: the ESP steps of account setup showed as done as soon as they started. Windows counts each step's time while it runs, so a step is now running while its count still goes on, and done when it stops.

## TrackVice 1.0.3

14 November 2026.

- Account setup is shown: Security policies, Certificates, Network connections and Apps as the ESP shows them, with when each was done, and what account setup waited for. Read from the FirstSync keys of the enrollment and the user.
- Device setup shows the same four steps, and Continue anyway when the ESP was left before device setup was done.
- Fixed: a policy or profile that account setup waited for was counted in device setup.
- Fixed: installing the agent again while it ran, as Intune does when the platform script is changed, failed because the exe was in use. The running agent is now left alone when the exe is the same, and stopped and replaced when it is new. The device keeps its session.

## TrackVice 1.0.2

14 November 2026.

- Names from Intune are kept per customer, so they are there at the next start, also in Statistics and Compare, and are read again in the background once a day.
- The Intune sign-in is kept encrypted for your Windows account per customer, so the names and the Autopilot list need no new sign-in. Settings > Intune sign-in > Sign out deletes it and the kept names.
- A script, app or remediation that shows by its ID makes TrackVice ask Intune for the new names by itself.
- About lists the agent, statistics and comparisons, and shows the right build date.

## TrackVice 1.0.1

14 November 2026.

- Fixed: Statistics and Compare showed platform scripts and apps by their ID, also after their names were read from Intune.

## TrackVice 1.0.0

14 November 2026. The first full release.

New

- Gantt tab: one bar per item across time, grouped by phase. **Policies** adds the policy areas, and **Only what took time** leaves out items that took less than a second. Click a bar to go to the item.
- Findings explain failures: what an error code means and what to do next, the error text of a failed platform script with a hint when a 64-bit script ran in 32-bit PowerShell, a phase that is near the ESP time limit, items that wait for a restart, and a common cause when three or more items in a phase fail with the same code.
- Autopilot event log: milestones on the timeline, and Device preparation items for TPM attestation, device enrollment by Autopilot, MDM enrollment, offline domain join, the domain controller check and hybrid Entra join, with findings for a hybrid join that fails.
- Account setup is read from the ESP keys of the user who signed in.
- Pre-provisioning: the time the device was boxed between the technician phase and the user does not count as running time or as no progress.
- Autopilot device preparation is recognised, and the Session tab says so.
- Remediations on the device, from the Intune Management Extension registry and HealthScripts.log, in a phase of their own.
- Devices > Statistics for devices with the agent: enrollments, finished, with failures, average, median and longest time over 24 hours to 90 days, a table of apps, scripts and steps with fail rate and times, and a table of enrollments. Export as CSV. Double-click an enrollment to watch it.
- Devices > Compare with another enrollment: the session you watch side by side with an upload from a device with the agent, or a saved session file. Items that differ in result, exit code, tries or time, findings only one side has, and the last events of each. Export as CSV.
- Customers: the Intune tenant, agent upload location and SCCM site of each customer you work for. A selector at the top of every device list switches between them. Settings > Customers adds, edits, imports and exports them. The storage links are encrypted for your Windows account and only exported when you ask for it.
- Devices > Names from Intune replaces Get script names from Intune. It reads script names (`DeviceManagementScripts.Read.All`), app names (`DeviceManagementApps.Read.All`), remediation names (`DeviceManagementScripts.Read.All`) and the configuration profiles Intune has for the device (`DeviceManagementConfiguration.Read.All`). The sign-in asks only for the permissions of what you tick.
- HTML report rewritten: a summary, a Gantt time line, the findings, details per phase, every event, and a print layout. `/title` sets its title.
- `/fleet-report <file> /upload <location> [/hours n]` writes one report over every enrollment the agents uploaded: duration over time, every enrollment, the slowest items and what fails most.
- Release tooling: `release.ps1` builds, signs and publishes a release, `bump.ps1` and `bump.sh` set the version, `winget/New-Manifests.ps1` writes the winget manifests for `DanielBaldissera.TrackVice`, a CI build runs on every push, and docs/RELEASE.md describes the steps.

Agent

- `/alert <https link>` sends an alert to a Teams workflow or another webhook when something fails and when a deployment is finished. `/alert-test` sends one test alert. `$Alert` in `agent/Install-TrackViceAgent.ps1` sets it from Intune.
- Passwords, keys and tokens in command lines, script output and log lines are masked before the session leaves the device. `/no-mask` turns that off.
- The agent uploads a zip of the device's logs when something fails, when the device goes idle and at the end.
- LogVice > Logs from the device downloads that zip for a device you watch, and Open in LogVice opens the device's copy of a log.
- SCCM task sequences: `/agent-start` starts the agent from a Run Command Line step and returns at once, `/agent-stop` makes the last upload and stops it, and `/id` sets the session ID. One session runs from Windows PE to Windows, across restarts and a new computer name, and the agent ends with the task sequence. See docs/AGENT.md.

Fixed

- A rolled-over smsts.log could lose lines or count a step twice.
- Unpacking a log zip could write outside its folder.
- Statistics: the fail rate and the averages were counted wrong.
- Menus listed some entries twice.

## TrackVice 0.0.10

13 November 2026.

- When a device with the agent stops uploading, the window says so: a finding with the time of the last upload, and "NO UPLOAD" in the status bar. Until now it kept showing the last state as if it were live.
- Fixed: the ID of a workload thread in the IME log became an app named after that ID.
- Fixed: a user app in the device phase showed as unknown. It is not applicable there, as the agent itself reports.
- The platform script can skip devices that were enrolled long ago (`$MaxAgeHours`), so it can be assigned to all devices.

## TrackVice 0.0.9

13 November 2026.

- Devices > Get script names from Intune: platform scripts show their name from Intune instead of the start of their ID. It signs in with one more read permission, DeviceManagementScripts.Read.All, and only when you ask for it. After that, the names are read at every Intune sign-in and kept while a device uploads again.
- Fixed: the Policies group could show a duration of over a year. It counted policies the Windows image set before the device was enrolled. Only policies from the device's own MDM enrollments count now.

## TrackVice 0.0.8

13 November 2026.

- Fixed: listing task sequences from a real SCCM site failed with "Generic failure". The time in the query was written with a time zone offset, which the SMS Provider does not take.
- Times from the SMS Provider are read as UTC, so they are no longer shifted by the time zone of your PC.
- When the SMS Provider refuses a query, the message now gives its reason and the query.

## TrackVice 0.0.7

13 November 2026. Built against the logs and registry of a real Autopilot enrollment.

- Platform scripts: every script with its result, exit code, output and error text, in a phase of their own.
- Microsoft 365 Apps from the Office CSP, with products, channel, languages and result.
- MSI apps the MDM client installs, with download and install state, error and product name.
- What the ESP waits for: policies, Wi-Fi and VPN profiles, certificates, MSI and Store apps, each with the time the ESP saw it arrive.
- Policies: every setting the MDM client set, grouped by policy area, from the MDM event log.
- A folder with registry exports (.reg) is read like the registry of the device.
- Fixed: a line about a device session made an item named after the all-zero user ID.
- Fixed: Win32 app state changes were not read from the report lines the agent writes today, so apps stayed at their first state.
- Fixed: the agent's first pass over every app, where it only works out what the ESP waits for, showed as an install starting.
- The install command of a Win32 app is read from the line after the step that announces it.
- Reporting bookkeeping no longer fills the events of an app, and the same thing is not said twice in a row.
- The ESP's "Sidecar" shows as the Intune Management Extension.

## TrackVice 0.0.6

13 November 2026.

- The TrackVice agent: TrackVice runs on a device without a window while it deploys, and uploads its whole session as it changes, to an Azure Storage container or a folder. See docs/AGENT.md.
- `/agent-install /upload <location>` installs it as a startup task that runs as SYSTEM, survives the restarts of a deployment and removes itself after 48 hours. `/agent-remove` removes it.
- `agent/Install-TrackViceAgent.ps1` installs it from an Intune platform script, so it runs from the start of device setup. It checks the exe's hash and reports why an install failed.
- docs/AGENT.md is the guide: storage container, links, Intune, testing, maintenance and troubleshooting.
- Devices > Devices with the TrackVice agent lists the uploaded sessions and follows one live, with every item, log line and finding the device itself has.
- A demo of that list, with made-up devices that move on while you watch.

## TrackVice 0.0.5

13 November 2026.

- Fixed: watching a device from the Intune list could show an empty window. When Intune does not return a deployment on its own, TrackVice now shows it from the list, and says why the rest is missing.
- A deployment from Intune always shows the deployment as a whole, with its state and duration.
- A read error is shown in the window and the status bar instead of "Nothing found".
- Times more than two days back read as days, not hours.

## TrackVice 0.0.4

13 November 2026.

- The Intune sign-in is one button: it gets the code, copies it and opens the sign-in page, shows the code large while it waits, and explains the common sign-in errors.

## TrackVice 0.0.3

13 November 2026.

- Devices (Ctrl+D): follow deployments on other devices.
- Autopilot in Intune: sign in to Microsoft Graph read-only with a device code, list Autopilot deployments, and follow one: device setup, account setup, the failure Intune recorded, and every app and policy with its status.
- Task sequences in SCCM: connect to the SMS Provider with your Windows account, list task sequence runs, and follow one step by step from its status messages, with each step's output.
- Settings: the client ID and tenant for the Intune sign-in.
- Fixed: columns set to share the remaining width did not, in the timeline and findings tables.
- Demos of both lists, with made-up devices.

## TrackVice 0.0.2

12 November 2026.

- Settings page (Ctrl+,): theme, history to read, ConfigMgr client WMI and event logs on or off, watch when TrackVice opens, how often to read, when to warn about no progress, and where LogVice is.
- History to read can now be Since install, which reads everything since Windows was installed, for a device that was deployed some time ago.
- Fixed: on a device that finished its deployment earlier, ESP and Win32 app states showed the time TrackVice read them instead of the time they were saved. They now carry the time their registry key was written, so the timeline, durations and the start of the deployment are right.
- When the deployment started before the history TrackVice reads, the status bar and the Session tab say so.
- An install the ESP still records as running from more than a day ago is called out as left over, not as stuck now.
- The Session tab shows when Windows was installed.

## TrackVice 0.0.1

11 November 2026. The first version.

- Follows SCCM task sequences through Windows PE and full Windows, following smsts.log as it moves.
- Follows SCCM applications, packages, content downloads and software updates from the client logs and ConfigMgr client WMI.
- Follows Autopilot with the Enrollment Status Page: device preparation, device setup and account setup, the profile, the join type and the ESP time limit.
- Follows Intune Win32 apps from the IME logs and the Win32Apps key, with names from the IME logs.
- One timeline in UTC with the log line, registry value, WMI instance or event record behind every event.
- Findings: failed, not detected after install, retry loop, restart needed, ESP time limit, no progress, task sequence failed and MDM errors.
- Phase rail, progress list with live progress, details, timeline, findings and session tabs.
- Compact view that stays on top, for OOBE and for watching over a shoulder.
- Save and open sessions, read a folder of logs, collect a zip with the session and its logs, export HTML and CSV.
- Command line for scripts and remediations, with exit code 2 when something failed.
- Opens logs and event channels in LogVice.
- Light, Dark, Vice and follow-Windows themes, the same as LogVice and PolicyVice.
- `--demo` and `--demo osd` play an Autopilot deployment and a task sequence through the real parsers.
