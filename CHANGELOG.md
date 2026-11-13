# Changelog

## TrackVice 0.0.7

13 November 2026. Built against the logs and registry of a real Autopilot enrollment.

- Platform scripts: every script with its result, exit code, output and error text, in a phase of their own.
- Microsoft 365 Apps from the Office CSP, with products, channel, languages and result.
- MSI apps the MDM client installs, with download and install state, error and product name.
- What the ESP waits for: policies, Wi-Fi and VPN profiles, certificates, MSI and Store apps, each with the time the ESP saw it arrive.
- Policies: every setting the MDM client set, grouped by policy area, from the MDM event log. This part has only run in tests.
- A folder with registry exports (.reg) is read like the registry of the device.
- Fixed: a line about a device session made an item named after the all-zero user ID.
- Fixed: Win32 app state changes were not read from the report lines the agent writes today, so apps stayed at their first state.
- Fixed: the agent's first pass over every app, where it only works out what the ESP waits for, showed as an install starting.
- The install command of a Win32 app is read from the line after the step that announces it.
- Reporting bookkeeping no longer fills the events of an app, and the same thing is not said twice in a row.
- The ESP's "Sidecar" shows as the Intune Management Extension.

## TrackVice 0.0.6

13 November 2026. The agent has uploaded to a real Azure storage account and installed on Windows 11. It has not yet been through a full Autopilot enrollment.

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

13 November 2026. The views of other devices have run with demo data and tests, not yet against a real tenant or site.

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

First preview, 11 November 2026. It has run with the built-in demos and the tests, not yet on a real device.

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
