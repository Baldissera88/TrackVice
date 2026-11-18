# Roadmap

What is planned for the next version of TrackVice.

Everything that was here is released in TrackVice 1.5.0: Overview as the home page, recent devices with search and Back and Forward, Publish made clear, Report a problem, Suggest a feature, Tell the author, Settings by subject, and only LogVice among the other tools. See CHANGELOG.md.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).

## 1. Get LogVice when it is missing

- When a LogVice button is used and LogVice is not found, TrackVice asks first: "LogVice is not on this PC. Download LogVice <version> from GitHub?" with **Download and open**, **Find LogVice.exe** and **Cancel**. Nothing is downloaded without that click.
- It downloads the latest LogVice release from github.com/Baldissera88/LogVice, checks it against the release's SHA256SUMS.txt, and stops with a clear message if the check fails.
- On a normal PC: installs the MSI when the user is an administrator, otherwise keeps LogVice.exe in TrackVice's own folder under %LOCALAPPDATA%. Then opens the logs in it, as the button meant to.
- On a device that is deploying (Shift+F10 in OOBE, F8 in a task sequence): only on the same click, keeps LogVice.exe in a temporary folder for this session; never installs anything there. In Windows PE it says that LogVice needs the full Windows.
- Shows progress and the size; can be cancelled. Remembers where LogVice is afterwards. Settings > LogVice shows the version found and a Download button.

## 2. Fix: the Publish banner is cut off

- At a normal window width the banner's buttons run out of the window ("What does Publish d..."). The banner must wrap its text and keep its buttons inside the window at every width.

## 3. Who is enrolling, and more for support

- **The user** who enrolls or signs in on the device is shown wherever the device is: the device header, Overview, Recent devices, the agent device list, the wall and the reports. Display name and UPN, from what the device already has (the Autopilot or ESP user, the signed-in account), and the task sequence user for SCCM where the site gives one. Search finds devices by user.
- **Device facts for support** in one card on the device: serial number, maker and model, Windows version, Autopilot profile and group tag, enrollment time, Entra device ID and Intune device ID, network (wired or Wi-Fi), free disk space, TPM, Secure Boot and BIOS mode, and the device's time zone. Only what the device or the existing sources already give; nothing new is collected without a reason shown.
- **Time left**: an estimate of when the deployment will be done, from the customer's reference enrollment or the last 7 days, shown next to the %.
- **Copy for a ticket**: one button that copies a short plain summary to paste into a support ticket: device, user, state, what failed and why in plain words, what it waits for, start time and time taken.
- **Open in Intune and Entra**: links to the device's page in the Intune admin center and Entra ID, when the IDs are known.
- **Notes**: short notes per device, kept on this PC, shown in the list.

## 4. Correct time on the devices

Devices from a new image often run in the wrong time zone during the deployment (often Pacific time), so their own clock and logs show the wrong hour. TrackVice already reads the time zone changes and shows every time in the viewer's own time zone, but support people look at the device too.

- Show the device's time zone and whether its clock is off, in the device facts, with a finding when the zone changed during the deployment or the clock is more than 2 minutes off.
- A tick in Add customer and Customer code: **Turn on automatic time zone on new devices** (off by default). When it is on, the agent turns on automatic time zone (the time zone auto-update service, and the location setting it needs) and asks Windows to sync the clock, early in the deployment. The tick explains that it changes a device setting and uses location.
- Next to it, the policy way, which is preferred where a fixed zone fits: a short guide for an Intune setting that sets the time zone for all devices (Policy CSP TimeLanguageSettings, check the exact setting and the Windows versions it needs before writing the guide).

## 5. Fix: time the device was off counts as deployment time

Seen on a real device: a deployment was stopped by shutting the PC down in the evening and turning it on the next morning. TrackVice then showed "Overall 9 h 27 min" and the restart as "9 h 01 min", although the deployment itself had run for about 26 minutes.

- Time between a shutdown and the next start of Windows (from the restart events) does not count as deployment time: not in Overall, the phase times, Time left, Statistics, Compare, the reference or the reports.
- Overall shows the active time and the time off apart when there was any: "26 min · off 9 h 01 min".
- A restart that was a shutdown for longer than 15 minutes is shown as "Off from 00:56 to 09:57 (9 h 01 min)", not as a restart that took 9 hours; its own time is the shutdown and the start, in seconds or minutes.
- The Gantt shows the time off as a hatched band, and the time axis can collapse it.
- "No progress" and "agent stopped sending" do not fire for a device that is off after a clean shutdown; the device list says "Off since 00:56" instead of stalled. The same rule as the reseal of a pre-provisioned device, for any long shutdown.

## 6. Show the agent version

The agent already sends its version with every upload, but TrackVice only shows it in the Power BI export and at the foot of a report.

- **On the device**: the header says "Agent 1.5.0" next to the source, and the Session tab shows the version, when the agent was installed and when it last updated itself.
- **In the agent device list and the wall**: a Version column, which can be sorted and filtered.
- **On Overview**: an "Agents" tile with how many devices run each version, compared with the version in Your storage: "12 on 1.5.0, 3 on 1.4.0 (they update within 6 hours)".
- **Needs attention**: devices whose agent is older than the published version and has not updated for more than 6 hours while it runs, with the likely reason (no update key or link in the script, the signature check failed, the link expired).
- The version is part of Copy for a ticket and the reports.
