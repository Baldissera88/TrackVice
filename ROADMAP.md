# Roadmap

What is planned for the next version of TrackVice. Nothing here is built yet.

## 1. Find devices again

- **Recent devices**: every device opened or followed (agent, SCCM run, Autopilot in Intune, this PC, an opened file) is kept in a list on this PC with customer, source, state and when it was last seen, newest first.
- **One search box** over all of them, across customers and Intune / SCCM: device name, serial number, user, task sequence or Autopilot profile.
- **Back and Forward** in the main window, as in a browser: Alt+Left, Alt+Right and the mouse back button.
- **Pin** a device to keep it at the top; remove a device from the list.
- Reachable from the Devices menu, Overview and the welcome page.

## 2. Overview as the home page

- On a PC that is not deploying, TrackVice opens on **Overview** instead of the PC it runs on. Following this PC is one choice on it.
- On a device that is deploying (OOBE and the ESP, a task sequence, Windows PE, SYSTEM) it still opens straight on that device.
- All customers or one, and Intune, SCCM or both, chosen at the top. Overview shows:
  - **Now**: devices deploying right now (agent devices and SCCM runs) with %, phase and what each waits for; failed devices first.
  - **Today and the last 7 days**: finished, failed, success rate, average time, the slowest steps, and the apps and steps that fail most, each with a link to the devices.
  - **Needs attention**: failed devices, devices with no progress, agents that stopped sending, findings worth acting on, a newer version to publish.
  - **Recent and pinned devices**, and the search box.
  - **Setup**: what is missing, such as Your storage or a first customer.
- Every tile opens the list or device behind it. Overview refreshes by itself. The device view gets Overview and Back buttons.

## 3. See how many use TrackVice, without collecting anything

- **Downloads**: Help > Downloads reads the download counts of each release from GitHub and shows them per version and in total.
- **Help > Tell the author you use TrackVice**: name, company, email and what you use it for, all optional. Opens a pre-filled e-mail to dbal.itx@gmail.com, or a GitHub Discussion. TrackVice sends nothing by itself.
- A line in the README: "Using TrackVice? Tell me", with the same choices.
- Never in the agent or on deploying devices.

## 4. Publish is easy to see

- After a new version is installed, a clear banner at the top of the window: "TrackVice <new> is installed. Your devices still get <old>." with **Publish** and **Later**. After an update from GitHub, the window opens on it.
- The same as a tile on Overview under Needs attention, and as a step in Getting started.
- Later hides it until the next start. It never publishes by itself. After Publish: "Published <new>. New devices get it now; installed agents update within 6 hours."

## 5. What Publish does, in plain words

- Next to every Publish button (the banner, Settings > Your storage, Overview), a short "What does Publish do?" that opens a small explanation with a drawing: this PC, then your storage (`trackvice-agent`), then the devices at your customers.
- It says:
  - what is sent: this TrackVice.exe, signed with your key, and nothing else;
  - who gets it and when: new devices at their next enrollment, installed agents within 6 hours; the platform script in Intune is not changed;
  - what is not affected: your customers' data, links and scripts, GitHub;
  - how to go back: run an older TrackVice.exe from a folder and publish it from the command line, or wait for the agents to remove themselves.
- The same text in docs/AGENT.md and in the Add customer guide, so a customer's own admin understands it too.

## 6. Report a problem

- **Help > Report a problem** (also a link on error messages and on a device's failed state):
  - fields: what happened, what you expected, and steps to repeat it;
  - filled in for you and shown before anything leaves: TrackVice version, Windows version, the mode (this PC, agent devices, SCCM, a file) and the last errors from TrackVice's own log;
  - an optional log bundle, with the secrets in it masked as today, that you look through and attach yourself;
  - buttons: open a pre-filled GitHub issue (the bug template), or a pre-filled e-mail to dbal.itx@gmail.com.
- TrackVice never sends it by itself, never adds customer names, tenants, device names or links unless you type them in, and says so on the page.
- A GitHub issue template "Bug report" with the same fields.

## 7. Wish for a feature

- **Help > Suggest a feature**: what you would like, why, and how you would use it.
- Opens a pre-filled GitHub issue (the feature template), a GitHub Discussion, or an e-mail to dbal.itx@gmail.com.
- A GitHub issue template "Feature request", and a line in the README.

## 8. Settings divided into subjects

Today Settings is one long page with about fifteen sections in a row, from the theme to keyboard shortcuts.

- A list of subjects on the left and one short page per subject on the right:
  - **General**: theme, what TrackVice opens on, updates from GitHub.
  - **This device**: what to read and how far back, ConfigMgr client WMI, event logs, how often to look again.
  - **Customers**: the list, import and export, Customer code.
  - **Intune / cloud**: Autopilot sign-in, tenant, client ID, names from Intune.
  - **SCCM / on-premises**: the SMS Provider and site code to use by default.
  - **Your storage and agents**: Your storage, Publish and what it does, Check security, the update key, keeping the account key.
  - **Other tools**: LogVice, PackVice and NotifyVice.
  - **Data and privacy**: where TrackVice keeps things on this PC, what it sends and to whom, forgetting sign-ins and kept data.
- A search box at the top that finds a setting by name and opens its page.
- Each setting in one short sentence; a longer explanation behind a small "More" where it is needed.
- A mark on settings changed from the default, and Reset on each page.
- Command line, keyboard shortcuts and About move to Help, since they are not settings.
- It remembers the last page.
