# Roadmap

What is planned for the next version of TrackVice.

Released in 1.5.0: Overview as the home page, recent devices with search and Back and Forward, Publish made clear, Report a problem, Suggest a feature, Tell the author, Settings by subject, and only LogVice among the other tools. See CHANGELOG.md.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).

On main, not released yet: get LogVice when it is missing, the Publish banner wraps, and the agent version is shown. Items 1 to 3 below were started; the unfinished work is on the branches b39 (item 1) and b40 (items 2 and 3).

## 1. Who is enrolling, and more for support

- **The user** who enrolls or signs in on the device is shown wherever the device is: the device header, Overview, Recent devices, the agent device list, the wall and the reports. Display name and UPN, from what the device already has (the Autopilot or ESP user, the signed-in account), and the task sequence user for SCCM where the site gives one. Search finds devices by user.
- **Device facts for support** in one card on the device: serial number, maker and model, Windows version, Autopilot profile and group tag, enrollment time, Entra device ID and Intune device ID, network (wired or Wi-Fi), free disk space, TPM, Secure Boot and BIOS mode, and the device's time zone. Only what the device or the existing sources already give; nothing new is collected without a reason shown.
- **Time left**: an estimate of when the deployment will be done, from the customer's reference enrollment or the last 7 days, shown next to the %.
- **Copy for a ticket**: one button that copies a short plain summary to paste into a support ticket: device, user, state, what failed and why in plain words, what it waits for, start time and time taken.
- **Open in Intune and Entra**: links to the device's page in the Intune admin center and Entra ID, when the IDs are known.
- **Notes**: short notes per device, kept on this PC, shown in the list.

## 2. Correct time on the devices

Devices from a new image often run in the wrong time zone during the deployment (often Pacific time), so their own clock and logs show the wrong hour. TrackVice already reads the time zone changes and shows every time in the viewer's own time zone, but support people look at the device too.

- Show the device's time zone and whether its clock is off, in the device facts, with a finding when the zone changed during the deployment or the clock is more than 2 minutes off.
- A tick in Add customer and Customer code: **Turn on automatic time zone on new devices** (off by default). When it is on, the agent turns on automatic time zone (the time zone auto-update service, and the location setting it needs) and asks Windows to sync the clock, early in the deployment. The tick explains that it changes a device setting and uses location.
- Next to it, the policy way, which is preferred where a fixed zone fits: a short guide for an Intune setting that sets the time zone for all devices (Policy CSP TimeLanguageSettings, check the exact setting and the Windows versions it needs before writing the guide).

## 3. Fix: time the device was off counts as deployment time

Seen on a real device: a deployment was stopped by shutting the PC down in the evening and turning it on the next morning. TrackVice then showed "Overall 9 h 27 min" and the restart as "9 h 01 min", although the deployment itself had run for about 26 minutes.

- Time between a shutdown and the next start of Windows (from the restart events) does not count as deployment time: not in Overall, the phase times, Time left, Statistics, Compare, the reference or the reports.
- Overall shows the active time and the time off apart when there was any: "26 min · off 9 h 01 min".
- A restart that was a shutdown for longer than 15 minutes is shown as "Off from 00:56 to 09:57 (9 h 01 min)", not as a restart that took 9 hours; its own time is the shutdown and the start, in seconds or minutes.
- The Gantt shows the time off as a hatched band, and the time axis can collapse it.
- "No progress" and "agent stopped sending" do not fire for a device that is off after a clean shutdown; the device list says "Off since 00:56" instead of stalled. The same rule as the reseal of a pre-provisioned device, for any long shutdown.

## 4. More help while setting up a customer

- Every step of Add customer and Your storage says, before it starts, which account it needs and why: the Azure role for making or using the storage account (Contributor, or Storage Account Contributor on an existing account), the Intune role and the admin consent for the platform script and the group, and what each sign-in will be asked for. A "What you need" box at the top of step 1, with a short checklist the user can tick.
- When a sign-in lacks a role or consent, the message says which role is missing, who can give it, and where in the portal.
- The same as a table in docs/AGENT.md and in Getting started.

## 5. App registration: what, why and how

- A short page in Settings > Intune / cloud (and in docs/AGENT.md) that explains the choice between Microsoft Graph Command Line Tools (the default) and the customer's own app registration:
  - why one's own can be better: the customer controls it and can see and remove it, it can be limited to the read permissions TrackVice uses, Conditional Access and sign-in logs can target it, and some tenants block Microsoft Graph Command Line Tools or require admin consent for it;
  - what it needs: a public client app (no secret), the redirect URI and "allow public client flows" for the device code sign-in, the delegated Graph permissions TrackVice asks for (listed from the code, read-only ones apart from the platform script and group ones used by Add customer), admin consent, and who can make it (Application Administrator or Cloud Application Administrator);
  - step by step in the Entra admin center, and the Client ID field where it goes.
- Optional: a button that makes the app registration in the customer's tenant after a sign-in with the needed role, with exactly those permissions, and fills in the Client ID. Asks first, shows what it will make.

## 6. The whole task sequence

- Today TrackVice shows only the steps that have sent status: steps and groups that have not run yet, or never run because of a condition, are missing (seen: the groups Apps and FK Distribution - Condor Software).
- Read the task sequence itself from the SMS Provider (SMS_TaskSequencePackage, its Sequence) when a run is opened, and show every group and step in the right order from the start: Waiting until it runs, Skipped when the engine skipped it, Disabled when it is disabled in the task sequence. Steps that install from a variable list show the base variable name.
- Needs only the read rights TrackVice already uses on the SMS Provider; when the task sequence cannot be read, show what the status messages give, as today, and say why.
- The same for the agent in a task sequence where the task sequence is in the environment (_SMSTSTaskSequence, if it is there and readable; check before using it).

## 7. Fix: e-mail when there is no mail program

- Seen: "We can't open this 'mailto' link. Your device needs a new app to open this link." when Windows has no mail program set.
- Before opening a mailto link, check that Windows has a program for mailto. When it has none, or opening fails, copy the text to the clipboard and show: "There is no e-mail program on this PC. The text is copied: paste it into an e-mail to dbal.itx@gmail.com", with Copy address and Open a discussion instead.

## 8. Fix: the window at small sizes

- Seen: on a small window, the Overview title runs into the header buttons and the tiles overlap.
- The header buttons fold into a "More" menu when there is no room, the title never overlaps them, and Overview's tiles go into one column below a width. Check every main view, the Settings window and the dialogs at 900 x 600 and at 125 % and 150 % scaling.

## 9. Overview: how many deployments

- Overview shows the total number of deployments for the chosen customer and side (Intune, SCCM or both): today, the last 7 days, the last 30 days and all that TrackVice can see.
- Every run counts, also when the same device is deployed several times: "48 deployments on 41 devices". A device deployed more than once shows how many times, with a link to its runs, and Overview has a short list "Deployed more than once" (device, times, last result), since a device that is deployed again and again is often worth a look.
- Each number opens the list of deployments behind it, filtered.
