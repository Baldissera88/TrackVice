# The TrackVice agent

Intune and SCCM only know what a device has reported. The agent is TrackVice itself, running on the device without a window while it deploys. It reads the device the way the window does and uploads the whole session as it changes: every app, policy and task sequence step, with the log lines, registry values and events behind them.

On your own PC, **Devices > Devices with the TrackVice agent** lists every device that has uploaded and follows one live, and **Devices > Wall view** shows them all on one screen.

Setting up takes two parts. You set up [Your storage](#set-up-once-your-storage) once: one storage account in your own Azure for all customers. Then you [add each customer in three steps](#add-a-customer-in-three-steps). The platform script for a customer holds one line, the customer code, and never needs a new hash when TrackVice changes.

Setups made before this, with a storage account and links per customer, keep working. They are described in [Setup with links](#setup-with-links-existing-setups).

![Devices with the TrackVice agent](images/trackvice-agent-list.png)

## Contents

- [How it works](#how-it-works)
- [What you need](#what-you-need)
- [Set up once: Your storage](#set-up-once-your-storage)
- [Add a customer in three steps](#add-a-customer-in-three-steps)
- [The customer code](#the-customer-code)
- [Test on one device first](#test-on-one-device-first)
- [Follow the devices](#follow-the-devices)
- [New TrackVice versions](#new-trackvice-versions)
- [The settings in the code](#the-settings-in-the-code)
- [Data in the customer's own Azure](#data-in-the-customers-own-azure)
- [Change a customer later](#change-a-customer-later)
- [Assigning it to all devices](#assigning-it-to-all-devices)
- [Alerts in Teams](#alerts-in-teams)
- [Masking of secrets](#masking-of-secrets)
- [Logs from the device](#logs-from-the-device)
- [Statistics and Compare](#statistics-and-compare)
- [SCCM task sequences](#sccm-task-sequences)
- [Agent updates](#agent-updates)
- [Alternative: a Win32 app](#alternative-a-win32-app)
- [Alternative: a folder instead of a storage account](#alternative-a-folder-instead-of-a-storage-account)
- [Setup with links (existing setups)](#setup-with-links-existing-setups)
- [Maintenance](#maintenance)
- [Troubleshooting](#troubleshooting)
- [What is uploaded, and security](#what-is-uploaded-and-security)

## How it works

```text
Your PC                     Intune platform script             Device                                  Your storage
-------                     ----------------------             ------                                  ------------
Publish TrackVice   ------------------------------------------------------------------------------>  trackvice-agent
                            Install-TrackViceAgent.ps1  --->   reads agent/latest.json, checks the     agent/latest.json
                            ($Customer = 'tv1:...')            signature, downloads TrackVice.exe,     agent/TrackVice-<version>.exe
                                                               checks size and SHA-256
                                                               TrackVice.exe /agent-install /customer
                                                               scheduled task "TrackVice Agent"
                                                               reads logs, ESP keys, events
                                                               uploads sessions/<id>.json.gz  ----->  tv-<customer>
                                                               and logs/<id>.zip
TrackVice lists and follows it  <------------------------------------------------------------------  (viewer link)
```

| | |
|---|---|
| Install | The platform script runs `TrackVice.exe /agent-install /customer "<code>"`. That copies TrackVice to `C:\Program Files\TrackVice Agent`, saves the upload link and the settings, registers the scheduled task **TrackVice Agent** (SYSTEM, at startup), starts it and returns within seconds, so it does not hold up the Enrollment Status Page |
| While it runs | Reads the device every 2 seconds. Uploads when something changed, at most every 10 seconds, and every 30 seconds otherwise so the list shows it is alive |
| Restarts | The task starts the agent again. It reads everything since Windows was installed and continues as the same row in the list |
| Idle | With nothing running and nothing new for 30 minutes it reads every 15 seconds and uploads every 5 minutes. It wakes up when something starts, such as account setup after pre-provisioning |
| Logs | Uploads a zip of the device's logs when something fails, when it goes idle and at the end. See [Logs from the device](#logs-from-the-device) |
| Alerts | With an alert link, posts to Teams when something fails and when the deployment is finished. See [Alerts in Teams](#alerts-in-teams) |
| Updates | Checks your storage for a newer signed TrackVice at start and every 6 hours, and replaces itself. See [New TrackVice versions](#new-trackvice-versions) |
| End | 48 hours after the deployment is done it uploads one last time and removes its task and the saved link. Something new that starts in that time, such as an app or a restart, makes it count again when that is done. After pre-provisioning it waits for the user phase without counting. In any case it removes itself 30 days after it was installed |

The first minutes of a deployment happen before Intune can run anything on the device. They are still in the session, because the agent reads the logs and the ESP registry keys back to the Windows install.

The Intune Management Extension runs platform scripts before it installs Win32 apps, which is why a platform script is the recommended way to install the agent.

## What you need

| For | What is needed |
|---|---|
| Your storage, once | An Azure subscription of your own. For a new storage account: Contributor (or Owner) on the subscription. For an existing account: Storage Account Contributor on that account. The cost is small: a session is a few hundred kilobytes per device |
| Add customer | No Azure role: TrackVice makes the customer's container with the account key it keeps. For the Intune part, an account in the customer's tenant, see [Who signs in](#who-signs-in-and-what-they-need) |
| The devices | Outbound HTTPS (port 443) to `<account>.blob.core.windows.net` of your storage, and of the customer's storage when the data is kept there |
| Files | `TrackVice.exe` on your PC. Add customer writes the platform script from `agent/Install-TrackViceAgent.ps1` for you |

Your storage does not have to be in the customer's tenant. The devices only use the links in the customer code.

## Set up once: Your storage

**Settings > Your storage > Set up** runs a short guide. **Devices > Add customer** also starts it the first time, when Your storage is not set up.

1. **Sign in to Azure**, with a code in the browser. Leave Azure tenant empty for the tenant of your account. Signing in and listing change nothing.
2. **Pick the subscription.**
3. **Pick the account**: a new account just for TrackVice, with a resource group (pick one or type a new name), a name and a region, or an existing account.
4. **Set up.** TrackVice shows what it will make and asks before it starts.

**What it makes and keeps**

| Where | What |
|---|---|
| Resource group | Only when you type a name that does not exist yet |
| Storage account | For a new one: StorageV2, Standard_LRS, HTTPS only, TLS 1.2, no anonymous blob access. An existing account keeps its settings. Allow storage account key access must be on |
| Container `trackvice-agent` | Private. TrackVice versions for the devices: `agent/latest.json` and `agent/TrackVice-<version>.exe`. A container that is there already is left as it is |
| On this PC | The account name, the account key, a Read link and a Create and Write link to `trackvice-agent`, valid 5 years. Encrypted for your Windows account with DPAPI, in the TrackVice settings of your Windows user |
| The update key | Made when this PC has none. The private key stays in `%LOCALAPPDATA%\TrackVice\agent-update-key.bin`, encrypted for your Windows account. It is the same key as for [Agent updates](#agent-updates) |
| First version | When `trackvice-agent` has no `agent/latest.json`, the running TrackVice.exe is published to it, signed with the update key |

The account key is kept so Add customer can make a container for each customer and sign its links without a new sign-in. Treat this PC and your Windows account like the key.

Settings shows the account, the date the links expire, and the version published for the devices. **Publish** publishes the running TrackVice when it is newer. **Set up again** signs new links, for example before they expire. **Remove** forgets the key and the links on this PC only. Nothing in Azure is deleted, and devices keep working.

## Add a customer in three steps

**Devices > Add customer…** Nothing is created until you press **Create** in step 3. **Devices > Demo: add customer** shows it with made-up data.

**1. Customer**

| Field | What it does |
|---|---|
| Customer name | Shown at the top of the device lists. When a customer with this name exists, its tenant and upload location are replaced |
| Store this customer's data in their own Azure | Off by default: the uploads go to your storage. On: see [Data in the customer's own Azure](#data-in-the-customers-own-azure) |
| Alert link (optional) | A Teams workflow link, see [Alerts in Teams](#alerts-in-teams). It must be an https address |

**2. Sign in**

Sign in to the customer's tenant for Intune, with a code in the browser. Tenant is optional: the tenant ID or a domain such as `contoso.onmicrosoft.com`. Signing in changes nothing.

**Skip: I add the script to Intune myself** goes on without Intune. Create then makes the storage, the customer code and the script, and saves the script on this PC.

**3. Create**

The page lists exactly what will be made, with two checkboxes, both on:

- **Create the group TrackVice - Autopilot devices**: a dynamic device group with the rule `(device.devicePhysicalIds -any (_ -contains "[ZTDId]"))`, which holds every device registered with Windows Autopilot. A group with that name is used when it is there.
- **Add the platform script TrackVice agent and assign it to that group**: system context, no signature check, 64-bit PowerShell. When the tenant has a script with that name, TrackVice asks before it replaces its content. Either way it is assigned to the group, and the assignments it has are kept.

Press **Create**. The result page lists what was made, with **Copy** for the customer code and the script, and **Open** for the saved script.

**What Create makes**

| Where | What |
|---|---|
| Your storage | A private container `tv-<customer>`, such as `tv-contoso` or `tv-northwind-traders`: lowercase letters, digits and single hyphens. A container that is there is used |
| Links | An upload link with Create and Write, for the devices, and a viewer link with Read and List, for you. Both on that container, valid 2 years |
| The customer code | See [The customer code](#the-customer-code) |
| The script | `Install-TrackViceAgent-<customer>.ps1` in `Documents\TrackVice\Scripts`: `agent/Install-TrackViceAgent.ps1` with `$Customer` filled in |
| Intune, if signed in | The group and the platform script, as ticked |
| TrackVice on this PC | The customer with its tenant and the viewer link, and the customer code. Both encrypted for your Windows account |

### Who signs in, and what they need

| Sign-in | What the account needs |
|---|---|
| Azure, for Your storage | Contributor (or Owner) on the subscription for a new account, since the name is checked and the resource group is made there. Storage Account Contributor on an existing account. It signs in with the public client of Azure PowerShell |
| Intune, in Add customer | An Intune role that can add and assign platform scripts, such as Intune Administrator, and a role that can make security groups, such as Groups Administrator, in the customer's tenant. Consent there for the delegated permissions `DeviceManagementScripts.ReadWrite.All` and `Group.ReadWrite.All`. Dynamic groups need Microsoft Entra ID P1 in the tenant. It uses the client ID saved for the customer, or Microsoft Graph Command Line Tools |
| Intune, in Customer code… | The same Intune role, and consent for `DeviceManagementScripts.ReadWrite.All` |
| Azure, for the customer's own Azure | As for Your storage, in the customer's subscription |

## The customer code

The customer code is one line that starts with `tv1:`. It is the only value in the platform script:

```powershell
$Customer = 'tv1:eyJuIjoiQ29udG9zbyIsInUiOi...'   # The customer code from TrackVice > Devices > Add customer
```

It holds, as base64url of a small JSON:

| Name | What |
|---|---|
| `n` | The customer name |
| `u` | The upload link: Create and Write on the customer's container |
| `a` | The Read link to `trackvice-agent` in your storage, where devices download TrackVice |
| `k` | The public update key. Devices only run a TrackVice that is signed with the private key on your PC |
| `h` | The alert link, when there is one |
| `m`, `keep`, `d` | 24, 48 and 30, see [The settings in the code](#the-settings-in-the-code) |

**Keep it like a password.** Anyone with it can upload to the customer's container and read the TrackVice versions in `trackvice-agent`. It cannot read the uploads, and it cannot change what the devices run, since that needs the private key.

TrackVice keeps each code on this PC, encrypted for your Windows account. **Settings > Customers > Customer code…** shows it masked, with **Copy**.

**What the script does on the device**

1. Reads the code, and stops with a message when it is empty or not a code.
2. Exits with success and installs nothing on a device enrolled more than 24 hours ago (`m`).
3. Downloads `agent/latest.json` from `trackvice-agent` and checks its signature with the key in the code. It refuses a file that names anything but `agent/TrackVice-<version>.exe`, or that is not signed with that key.
4. Downloads that exe to `%TEMP%`, and checks its size and SHA-256 against the signed ones.
5. Runs `TrackVice.exe /agent-install /customer "<code>"` and reports its output and exit code.

So a device always installs the newest version you published, and the script never needs a new hash.

## Test on one device first

On a test PC or virtual machine, in PowerShell **as administrator**, in the folder with `TrackVice.exe`:

```powershell
.\TrackVice.exe /agent-install /customer "<customer code>"
```

This skips the 24-hour check of the script, so it also works on a PC enrolled long ago. It prints "The TrackVice agent is installed and running" with when it removes itself. Check:

- Task Scheduler has the task **TrackVice Agent**, running.
- `C:\Program Files\TrackVice Agent\agent.log` says the agent started, made its first upload, and that self-update is on.
- **Devices > Devices with the TrackVice agent**, with the customer chosen at the top, has a row for the PC.

Remove it again:

```powershell
& 'C:\Program Files\TrackVice Agent\TrackVice.exe' /agent-remove
```

Then enroll or reset one Autopilot device of the customer. The script runs during device setup, and the device appears in the list.

## Follow the devices

Keep the list open on your PC. The device appears during device setup. The list refreshes every 15 seconds, and watching a device reads its session every 5 seconds.

| Column | Meaning |
|---|---|
| State | How many items are done, what is running right now, and how many failed |
| Deployment | Autopilot, task sequence, or both |
| Last upload | When the device last uploaded. A running device that has been silent for 5 minutes says so: it is off, has no network, or is restarting |

Add customer saves the viewer link with the customer, encrypted for your Windows account, so there is nothing to paste.

**Demo: devices with the agent** in the same menu shows the view with made-up devices.

## New TrackVice versions

You publish a version once, to your storage. Every customer gets it from there.

**Publish.** When TrackVice starts and the version in your storage is older than the one running, the status bar says "TrackVice <version> is not published to your storage." with **Publish**. Nothing is published until you click it. **Settings > Your storage > Publish** does the same. TrackVice uploads `agent/TrackVice-<version>.exe` first and `agent/latest.json` last, signed with the update key. To publish another version, publish from that version of TrackVice.

**New devices** download the newest published version when the script runs.

**Installed agents** check `agent/latest.json` at start and every 6 hours, and install a newer version signed with the key in their customer code. A version that is the same or older is not installed. How the replacement works is in [Agent updates](#agent-updates).

If publishing makes a new update key, because this PC had none, TrackVice says so. Codes made before have the old key, so their devices refuse the new version. See [Maintenance](#maintenance).

## The settings in the code

Add customer puts these defaults in every customer code. They come from `src/CustomerCode.cs`.

| Name | Default | What it does |
|---|---|---|
| `m` | 24 | The script installs the agent only on a device enrolled less than this many hours ago. 0 installs it on every device |
| `keep` | 48 | Hours the agent stays after the deployment is done |
| `d` | 30 | Days after install when the agent always removes itself |

Add customer has no fields for them. To use other values for one customer, change them in the code, put the new code in `$Customer` of the script, and add the script to Intune again. In PowerShell:

```powershell
$code = 'tv1:...'
$text = $code.Substring(4).Replace('-', '+').Replace('_', '/')
while ($text.Length % 4) {
    $text += '='
}
$values = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($text)) | ConvertFrom-Json
$values.m = 12
$json = $values | ConvertTo-Json -Compress
'tv1:' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
```

TrackVice keeps the code it made, not one changed by hand. **Customer code…** shows and uses its own copy, so **Update the script in Intune** there puts the defaults back.

For a test on one device, `/keep <hours>` and `/max-days <n>` after `/customer "<code>"` on the command line override the values in the code.

## Data in the customer's own Azure

Some customers want the uploads in their own subscription. Tick **Store this customer's data in their own Azure** in step 1. Step 2 then also signs in to Azure in the same tenant, where you pick the subscription and a new or existing storage account, as in the [New customer with links](#new-customer-with-links) guide.

Create then makes, in the customer's storage account:

| | |
|---|---|
| Containers | `trackvice` for the uploads, and `trackvice-agent`. Both private. `trackvice-agent` stays empty: the devices still download TrackVice from your storage |
| Lifecycle rule | `trackvice-cleanup` deletes the blobs under `trackvice/sessions/` and `trackvice/logs/` after 30 days |
| Links | Upload (Create, Write) and viewer (Read, List) on `trackvice`, valid 2 years. The account key is read once to sign them, and not kept |

The customer code, the script and the Intune part are the same as for your storage. The code holds the upload link to the customer's storage, and the Read link to your `trackvice-agent`. The devices need to reach both storage accounts.

## Change a customer later

**Settings > Customers**, on the customer's row, **Customer code…** It is there for customers made with Add customer on this PC.

| | |
|---|---|
| Customer code | Masked, with **Copy** |
| Alert link | Change or clear it, then **Save the alert link**. That makes a new customer code and saves the script again in `Documents\TrackVice\Scripts` |
| Update the script in Intune | Sign in to the customer's tenant, then **Update**. It puts the script for the code in the platform script **TrackVice agent**, after you confirm. Its assignments are kept |

Devices that run the script from then on get the new code. Editing a script in Intune makes it run again on the devices that already ran it, but on a device enrolled more than 24 hours ago it installs nothing. Agents already installed keep the alert link they were installed with.

## Assigning it to all devices

A platform script runs once on every device it is assigned to, also on devices that were enrolled long ago. Those would each install the agent, upload their history and fill your list.

The group **TrackVice - Autopilot devices** holds every Autopilot device, also old ones. The script therefore installs the agent only on a device whose Intune Management Extension folder is younger than 24 hours (`m` in the code), which means a device that has just enrolled. Older devices exit with success and install nothing.

A platform script runs once per device. After a reset the device runs it again. Editing a script in Intune makes it run again on the devices that already ran it.

## Alerts in Teams

The agent can post to a Teams channel when something fails and when a deployment is finished, so you do not have to keep the list open. It posts to a Teams workflow, or to any other web address that takes JSON.

**Make the workflow**

1. In Teams, open the channel, choose **…** > **Workflows**, and pick the template **Post to a channel when a webhook request is received**. Its trigger is **When a Teams webhook request is received**.
2. Give it a name, such as TrackVice alerts, choose the team and the channel, and add the workflow.
3. Copy the link it shows. That is the alert link. It starts with `https://`.

Anyone with the link can post to the channel, so keep it like the upload link.

**Test it** from your own PC:

```powershell
.\TrackVice.exe /agent /alert "<alert link>" /alert-test
```

It prints "The test alert was sent." and a card with a test alert appears in the channel.

**Use it.** Enter it in step 1 of Add customer, or later in **Customer code…**. With links, fill in `$Alert` in `agent/Install-TrackViceAgent-Links.ps1`. On the command line, add `/alert "<alert link>"` to `/agent-install`, `/agent-start` or `/agent`.

| | |
|---|---|
| What is sent | One card per failure: failed items, items not detected after install, a failed task sequence, the ESP time limit and a failed hybrid join, with the device, the deployment, the progress and what to do. One card when the deployment is finished: nothing has run for 30 minutes, with how long it took and its slowest part |
| What is not sent | Warnings, MDM event log errors, and failures that ended more than 2 hours before the agent started, such as those of a device enrolled long ago |
| How often | Each failure once. At most 15 alerts per device. Account setup after pre-provisioning gives a second finished card |
| When it fails | It tries again after 1, 2 and 4 minutes, then gives up. `agent.log` says "Alert sent" or why not |
| Restarts | Sent alerts are remembered in `alerts.txt` in the agent's folder, so a restart does not send them again |
| On the device | The alert link is saved with the upload link under `HKLM\SOFTWARE\TrackVice\Agent` and removed with it |

Alerts go over https. The texts are masked like the session, see below.

## Masking of secrets

Before a session, a log zip or an alert leaves the device, the agent replaces passwords, keys and tokens with `***`. It finds them:

- by name, in `name=value`, `name: value` and JSON: names with password, pwd, secret, token, api key, access key, account key, license key, product key, client secret and `sig`
- after switches such as `-Password`, `--token` and `/password`
- in `Bearer` and `Basic` headers, `https://user:password@host`, `ConvertTo-SecureString "..." -AsPlainText` and `net user <name> <password>`

Settings about passwords, such as `PasswordExpirationDays`, and values that are only a variable, such as `-Password $pw`, are left as they are.

Masking is on by default. Add `/no-mask` to `/agent-install`, `/agent-start` or `/agent` to upload everything as it is. Masking covers the session and the alerts. The log files in the log zip are uploaded as they are on the device; only the session in the zip is masked. It cannot know every secret, so treat the container like the logs themselves.

## Logs from the device

Besides its session, the agent uploads the device's logs as one zip, `logs/<id>.zip`, next to `sessions/<id>.json.gz`:

| | |
|---|---|
| When | When the first item fails, when the device goes idle after something ran, and at the end. Never more often than every 10 minutes |
| What | The session, every log file the session has a line from, every Intune Management Extension log, and the task sequence logs. A log over 20 MB goes in as its last 20 MB |
| Size | A zip over 60 MB is not uploaded. `agent.log` says so |

On your PC, watch the device from the list, then choose **LogVice > Logs from the device**. TrackVice downloads the zip, unpacks it under `%LOCALAPPDATA%\TrackVice\Logs\<device>-<id>` and opens the folder. **Open in LogVice** on an item or an event then opens the device's copy of that log. A new download replaces the older copy of that device, and copies older than 30 days are removed.

The demo devices under **Demo: devices with the agent** have logs too, made up like the rest.

## Statistics and Compare

The uploads also feed two views on your PC. Both read the location you connected to in **Devices with the TrackVice agent**, or ask for it.

- **Devices > Statistics for devices with the agent** goes over every session uploaded in the last 24 hours, 7, 30 or 90 days: how long enrollments take, and which apps, scripts and steps are slow or fail. Double-click an enrollment to watch it. See [Statistics](../README.md#statistics) in the README.
- **Devices > Compare with another enrollment > A device with the agent** puts the session you watch beside one from the uploads. See [Compare](../README.md#compare) in the README.

The same uploads make a report from the command line, for example every enrollment of the last week:

```powershell
.\TrackVice.exe /fleet-report fleet.html /upload "<viewer link>" /hours 168
```

## SCCM task sequences

The agent can also follow an SCCM task sequence, from Windows PE to the end, as one session in the list. In a task sequence it is not installed: `/agent-start` starts it in the background and returns at once, and it stops by itself when the task sequence ends.

**What you need**

- A boot image with the **WinPE-NetFx** component, for the part in Windows PE. Without it, the agent can only start once Windows runs.
- A network path from Windows PE and Windows to the storage account, or to the share.
- A package with `TrackVice.exe` in it, distributed to your distribution points. No program is needed.
- The customer code, or an upload link from [step 3](#step-3-the-three-links) of the setup with links.

**The steps.** Add a **Run Command Line** step at each of these places, all with the same command line and the TrackVice package:

```text
TrackVice.exe /agent-start /customer "<customer code>"
```

With links, use `/upload "<upload link>"` instead of `/customer "<customer code>"`. From the code, the agent in a task sequence uses the upload link and the alert link. It does not update itself, so the package decides the version.

| Where | Why |
|---|---|
| After **Partition Disk** | Starts the agent in Windows PE |
| After **Setup Windows and ConfigMgr** | The restart into Windows ends the agent from Windows PE. This starts it again in Windows |
| After each **Restart Computer** step | A restart ends the agent. This starts it again |
| Last step, optional | `TrackVice.exe /agent-stop` makes the last upload and stops the agent at once, instead of when it sees the end in smsts.log |

On every one of these steps, tick **Continue on error** on the Options tab, so the task sequence goes on if the agent cannot start or stop. `/agent-start` returns at once when the agent already runs, so an extra step does no harm.

Add `/alert "<alert link>"` to send [alerts](#alerts-in-teams), and `/no-mask` to turn off [masking](#masking-of-secrets). Give them on every `/agent-start` step.

**One session from Windows PE to Windows.** The session ID comes from the device's SMBIOS UUID, or its wired MAC address when the UUID is missing, hashed, so it is the same in Windows PE and in Windows. The agent also writes it to `Windows\Temp\TrackVice` on the Windows being installed, once Apply Operating System has put that there. The list shows the device under its current name; the earlier names, such as the `MININT-` name of Windows PE, are kept in the session, and searching the list for one finds the device. Give `/id <id>` on every step only when the hardware has no usable UUID or MAC address. A new run of a task sequence on the same device uses the same ID, so it replaces the earlier session.

| | |
|---|---|
| In Windows PE | Reads smsts.log only. ConfigMgr client WMI and the event logs come once Windows runs |
| In Windows | Reads everything, as the installed agent does |
| End | When smsts.log says the task sequence ended, when no step has run for 30 minutes, after `/keep <hours>` (default 48), or with `/agent-stop`. It uploads the session and the logs one last time |
| Its log | `%WINDIR%\Temp\TrackVice\agent.log`. In Windows PE that is on `X:` and is gone after the restart |
| On the device | Nothing is installed: no task, no registry key. The state file and log stay in `%WINDIR%\Temp\TrackVice` |

To check the steps before a real deployment, run a task sequence with them on a test device, keep the list open on your PC, and look at `agent.log` with F8 in Windows PE.

## Agent updates

An installed agent can update itself, so a new TrackVice version reaches the devices without a new hash in the platform script. You publish a version with a signed file next to it, and the agents install it only when the signature is made with your key.

With a customer code this is always on: the code holds the key and the link to `trackvice-agent` in your storage, and you publish as in [New TrackVice versions](#new-trackvice-versions). The rest of this section is for setups with links, and describes what every installed agent does.

**Make the key.** In TrackVice, open **Settings > Agent updates > Publish agent update** and press **Create key**. The key is also made the first time you publish, and by Your storage. The private key stays on your PC in `%LOCALAPPDATA%\TrackVice\agent-update-key.bin`, encrypted for your Windows account, and is used for every customer. The dialog shows its fingerprint, and **Copy** copies the public key.

**Put the key in the script.** Fill in the two lines of `agent/Install-TrackViceAgent-Links.ps1`:

```powershell
$UpdateKey  = '<the public key>'
$UpdateLink = 'https://<account>.blob.core.windows.net/trackvice-agent?sp=r&...'
```

`$UpdateLink` is a container link with **Read** only, or a folder. Use a container only you write to, such as `trackvice-agent` from [New customer with links](#new-customer-with-links), not the one the devices upload to. The script passes them to `/agent-install` as `/update-key` and `/update`. An install with a key that is not an RSA public key of at least 3072 bits, or with a link that cannot be used, fails with the reason.

**Publish a version.** In **Publish agent update**, choose the TrackVice.exe, enter the link of the same container with **Create** and **Write** (it is saved for the customer in use), and press **Publish**. Or from the command line:

```powershell
.\TrackVice.exe /publish-agent /to "<link with Create and Write>" /exe "<path to the new TrackVice.exe>"
```

Without `/exe` it publishes itself. It uploads `agent/TrackVice-<version>.exe` first and `agent/latest.json` last. That file names the version, the SHA-256, the size, the file name and the time, signed with the key.

**What the devices do**

| | |
|---|---|
| When | At start and every 6 hours, while the installed agent runs |
| Check | Reads `agent/latest.json`, checks the signature with its key, and installs only a newer version. The same version does nothing, and an older one is not installed |
| Download | Downloads `agent/TrackVice-<version>.exe` into the `update` folder of the agent, and checks its size and SHA-256 against the signed ones |
| Replace | Starts a copy of itself, `TrackVice-apply.exe`, makes its last upload and exits. The copy waits up to a minute for it, replaces `TrackVice.exe`, keeps the one before as `TrackVice.exe.old`, checks the hash again and starts the task |
| After | The agent continues as the same row in the list, with the same session and the same end time. The next start removes `TrackVice-apply.exe` and the `update` folder |
| On failure | When the exe cannot be replaced or has another hash, the earlier version is started again |

**When the signature does not match**, `agent.log` says "agent/latest.json is not signed with the update key of this device. It is ignored." The agent goes on with the version it has. The same happens with a manifest signed with another key, such as one from another PC: devices only trust the key they were installed with. A wrong size or SHA-256 says "It is not installed."

Only an agent installed with `/agent-install` updates itself. The agent in a task sequence and `/agent` do not. With links, devices still download the TrackVice.exe of `$Download` first, and update right after they start. To save that restart, also upload the new exe over `TrackVice.exe` and update `$Sha256`.

## Alternative: a Win32 app

Use this when you want the Enrollment Status Page to track the agent, or when scripts are not allowed. It starts when the Intune Management Extension reaches it, which can be after other apps.

| | |
|---|---|
| Content | `TrackVice.exe`, packed as .intunewin |
| Install command | `TrackVice.exe /agent-install /customer "<customer code>"`, or with links `/upload "<upload link>"` |
| Uninstall command | `"%ProgramFiles%\TrackVice Agent\TrackVice.exe" /agent-remove` |
| Install behavior | System |
| Detection rule | File `%ProgramFiles%\TrackVice Agent\TrackVice.exe` exists |

No download and no hash check are needed, because the exe is in the package. With a customer code the agent then updates itself from your storage. The install command holds the code, so it is readable by Intune administrators like the script. The 24-hour check is in the script only, so assign the app to new devices only.

## Alternative: a folder instead of a storage account

The upload location can also be a folder or a share:

```powershell
TrackVice.exe /agent-install /upload "\\server\share\TrackVice"
```

The agent writes as the device's computer account, so the share has to allow that. This fits domain-joined devices and testing, not Autopilot devices on the internet. In the list, enter the same folder as the upload location.

## Setup with links (existing setups)

Before customer codes, each customer had its own storage account, and the platform script held the links and the SHA-256 of TrackVice.exe. Those setups keep working. Their script is `agent/Install-TrackViceAgent-Links.ps1`, with `$Download`, `$Sha256`, `$Upload`, `$KeepHours`, `$MaxAgeHours`, `$Alert`, `$UpdateKey` and `$UpdateLink`. For a new customer, use [Add customer](#add-a-customer-in-three-steps) instead.

### New customer with links

**Devices > New customer with links…** does the work of steps 1 to 6 below. It goes through Customer, Storage, Links, Upload, Alerts and Script, and nothing is created until you press a step's button. **Devices > Demo: new customer** shows it with made-up data.

| | |
|---|---|
| In Azure | A storage account (new, or an existing one that keeps its settings), the private containers `trackvice` and `trackvice-agent`, and the lifecycle rule `trackvice-cleanup` that deletes `trackvice/sessions/` and `trackvice/logs/` after 30 days. It uploads TrackVice.exe to `trackvice-agent` and checks that the download link can read it |
| Links | Valid 6 months, 1 year or 2 years. Upload (Create, Write on `trackvice`), viewer (Read, List on `trackvice`), download (Read on `TrackVice.exe`), agent updates (Read on `trackvice-agent`) and publish (Create, Write on `trackvice-agent`). The account key is read once to sign them, and not kept |
| The script | `Install-TrackViceAgent-<customer>.ps1`, a copy of `agent/Install-TrackViceAgent-Links.ps1` with the links, the SHA-256, `$Alert`, `$MaxAgeHours` (24) and, when this PC has an update key, `$UpdateKey` and `$UpdateLink` filled in. Keep it like a password |
| Intune, if you choose | The platform script **TrackVice agent**, set as in [step 6](#step-6-the-platform-script-in-intune), assigned to the device group you pick. It needs an Intune role that can add and assign platform scripts, and consent for `DeviceManagementScripts.ReadWrite.All` and `Group.ReadBasic.All` |
| On this PC | The customer with its tenant and the viewer link, and the publish link, encrypted for your Windows account |

The Azure sign-in needs the same roles as [Your storage](#who-signs-in-and-what-they-need). If your organization blocks the Azure PowerShell client, enter your own app registration under Client ID.

`agent/New-TrackViceCustomer.ps1` does the Azure part from PowerShell 5.1 with the Az.Accounts and Az.Storage modules, without Intune. It writes the filled script and returns the links; paste the viewer link into **Devices with the TrackVice agent** yourself.

```powershell
.\New-TrackViceCustomer.ps1 -CustomerName Contoso -SubscriptionId <subscription id> -ResourceGroupName rg-trackvice
```

Its other parameters are `-StorageAccountName`, `-Location` (default westeurope), `-RetentionDays` (30), `-LinkDays` (365), `-ExePath`, `-Alert`, `-MaxAgeHours` (24), `-UpdateKey` and `-OutputPath`. `Get-Help .\New-TrackViceCustomer.ps1 -Detailed` describes them.

`$MaxAgeHours` is 24 in the filled script, so it installs nothing on a PC that was enrolled long ago. To test on your own PC as in [step 4](#step-4-test-from-your-own-pc) and [step 5](#step-5-fill-in-and-test-the-script), set `$MaxAgeHours = 0` in a copy of the script.

### Step 1: the storage container

Use an existing storage account, or create one: Azure portal > Storage accounts > Create.

| Setting | Value |
|---|---|
| Performance | Standard |
| Redundancy | Locally-redundant storage (LRS) |
| Advanced > Allow storage account key access | Enabled (the default) |
| Networking > Public network access | Enabled from all networks |

Devices upload from wherever they enroll, so the account has to be reachable from the internet. That setting only decides who can reach the account. The container stays private, and nothing in it can be read or written without a link.

Then create the container:

1. Open the storage account > Data storage > Containers > **+ Container**.
2. Name it `trackvice`. Keep the access level private.

Give TrackVice its own container. A link gives access to the whole container it was made for, so do not reuse a container that holds other files.

### Step 2: upload TrackVice.exe

1. Open the `trackvice` container and choose **Upload**. Upload `TrackVice.exe`.
2. Note the hash of that file. `agent/Install-TrackViceAgent-Links.ps1` checks it before running the exe:

```powershell
(Get-FileHash .\TrackVice.exe -Algorithm SHA256).Hash
```

Instead of uploading the exe, the script can download it from this repository's release. Then you need no download link, only the hash of that release's `TrackVice.exe`, which is in the release notes:

```powershell
$Download = 'https://github.com/Baldissera88/TrackVice/releases/download/v1.0.0/TrackVice.exe'
```

Your own container keeps you independent of GitHub and lets you decide when devices get a new version.

### Step 3: the three links

A link is a shared access signature (SAS): a URL with a token that allows only what you tick. You need three.

| Link | Made on | Permissions | Goes into |
|---|---|---|---|
| Download | The blob `TrackVice.exe` | Read | `$Download` in `agent/Install-TrackViceAgent-Links.ps1`. Not needed when you download from GitHub |
| Upload | The container `trackvice` | Create, Write | `$Upload` in `agent/Install-TrackViceAgent-Links.ps1` |
| Viewer | The container `trackvice` | Read, List | TrackVice on your PC |

For all three: signing method **Account key**, allowed protocols **HTTPS only**, and an expiry you will remember to renew, such as one year. Copy the **Blob SAS URL**, not only the token. The portal shows it once, so save each link as you make it.

**Download link**

1. Open the `trackvice` container and click `TrackVice.exe`.
2. Open the **Generate SAS** tab.
3. Permissions: **Read** only. Set the expiry. Press **Generate SAS token and URL**.

**Upload link**

1. Data storage > Containers. On the `trackvice` row, click `…` > **Generate SAS**.
2. Permissions: **Create** and **Write** only. Set the expiry. Generate.

**Viewer link**

1. On the same screen, change the permissions to **Read** and **List** only.
2. Generate again and copy the new URL.

The links look like this:

```text
Download  https://<account>.blob.core.windows.net/trackvice/TrackVice.exe?sp=r&...&sig=...
Upload    https://<account>.blob.core.windows.net/trackvice?sp=cw&...&sig=...
Viewer    https://<account>.blob.core.windows.net/trackvice?sp=rl&...&sig=...
```

### Step 4: test from your own PC

Before Intune is involved, check that the links work. In PowerShell as administrator, in the folder with `TrackVice.exe`:

```powershell
.\TrackVice.exe /agent /upload "<upload link>" /once
```

It prints nothing when it works. A `sessions` folder with one file appears in the container.

Then open TrackVice > **Devices > Devices with the TrackVice agent**, paste the **viewer** link into Upload location and press **Connect**. Your PC is in the list. **Watch this device** shows the same as TrackVice run on the PC itself.

### Step 5: fill in and test the script

Open `agent/Install-TrackViceAgent-Links.ps1` and fill in the first lines. Keep the single quotes:

```powershell
$Download  = 'https://<account>.blob.core.windows.net/trackvice/TrackVice.exe?sp=r&...'
$Sha256    = '<the hash from step 2>'
$Upload    = 'https://<account>.blob.core.windows.net/trackvice?sp=cw&...'
$KeepHours = 48
$MaxAgeHours = 0
$Alert     = ''
```

`$MaxAgeHours` works like `m` in a customer code, see [Assigning it to all devices](#assigning-it-to-all-devices). Leave it at `0` while you test on your own PC, and set it to `24` before you assign the script to all devices. `$Alert` is explained in [Alerts in Teams](#alerts-in-teams).

Run it on your own PC, in PowerShell **as administrator**:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Install-TrackViceAgent-Links.ps1
```

It prints "The TrackVice agent is installed and running" within a few seconds. Check:

- Task Scheduler has the task **TrackVice Agent**, running.
- `C:\Program Files\TrackVice Agent\agent.log` says the agent started and made its first upload.
- The list in TrackVice has a new row for your PC, and its last upload stays recent.

Remove it from your PC again:

```powershell
& 'C:\Program Files\TrackVice Agent\TrackVice.exe' /agent-remove
```

### Step 6: the platform script in Intune

Intune admin center > Devices > Scripts and remediations > **Platform scripts** > Add > Windows 10 and later.

| Setting | Value |
|---|---|
| Name | TrackVice agent |
| Description | Installs the TrackVice agent during enrollment. It uploads the device's deployment progress to the trackvice storage container until 48 hours after the deployment is done, then removes itself. |
| Script location | Your filled-in `Install-TrackViceAgent-Links.ps1` |
| Run this script using the logged on credentials | No |
| Enforce script signature check | No |
| Run script in 64 bit PowerShell Host | Yes |
| Assignments | A **device** group with your Autopilot devices. Start with one test device |

Assign it to devices, not users, so it runs during device setup before anyone signs in. The same settings apply when you add a script from Add customer by hand.

### Step 7: follow the devices

Enroll or reset the test device and keep the list open on your PC, as in [Follow the devices](#follow-the-devices). The viewer link is saved encrypted for your Windows account, so you only paste it once.

## Maintenance

| When | Do this |
|---|---|
| A new TrackVice version | Publish it to your storage: click **Publish** in the status bar, or **Settings > Your storage > Publish**. New devices get it at once, and installed agents within 6 hours. With links, publish it with **Publish agent update** to each customer's `trackvice-agent`, or upload the new `TrackVice.exe` over the old one and update `$Sha256` in `agent/Install-TrackViceAgent-Links.ps1` in Intune. Until the hash is updated that script fails on purpose |
| The customer's links expire | Upload and viewer links from Add customer are valid 2 years. Run **Add customer** again with the same name: it uses the container that is there, signs new links, and replaces the upload location of the customer. Put the new script in Intune (it asks before it replaces the content) |
| The links to `trackvice-agent` expire | They are valid 5 years. **Settings > Your storage > Set up again** signs new ones. Codes made before keep the old Read link, so make them again as above before it expires |
| Links of a setup with links expire | Run **New customer with links** again with the same customer name and the existing account, or make new links by hand. Then update `$Download`, `$Upload` and `$UpdateLink` in `agent/Install-TrackViceAgent-Links.ps1` in Intune, the task sequence steps and the upload location in TrackVice |
| A new PC or Windows user | The update key cannot move, and the account key and customer codes are kept for one Windows user. Set up Your storage there, which makes a new key. Publish, then make every customer code again with Add customer and update the scripts in Intune: devices only trust the key in their code. With links, put the new key in `$UpdateKey` and update the script in Intune |
| A link or customer code got out | Rotate the storage account key the links were signed with (Security + networking > Access keys). That cancels every link signed with that key: in your storage, every customer's links and the links to `trackvice-agent`. Then **Set up again** in Your storage and make the customer codes again |
| The containers grow | In the customer's own Azure and with links, the lifecycle rule deletes uploads after 30 days. Your storage has no such rule: add a lifecycle management rule on the account that deletes blobs under `tv-<customer>/sessions/` and `tv-<customer>/logs/` after 30 days |
| Stop using it | Remove the assignment in Intune. Agents already installed remove themselves when their time is up |

To remove the agent from one device now:

```powershell
& 'C:\Program Files\TrackVice Agent\TrackVice.exe' /agent-remove
Remove-Item 'C:\Program Files\TrackVice Agent' -Recurse
```

## Troubleshooting

| What you see | Why, and what to do |
|---|---|
| The script stops with "cannot be run because it contains a #requires statement" | PowerShell is not running as administrator |
| "Fill in $Customer at the top of the script" | The script has no customer code. Copy the script from Add customer, or put the code from **Customer code…** in `$Customer` |
| "This is not a TrackVice customer code. It starts with tv1:" | `$Customer` holds something else, such as a link. Copy the code again |
| "The customer code could not be read" | The code was cut off or changed when copied. Copy it again with **Copy** in **Customer code…** |
| "The customer code has no upload link, download link or update key" | The code is not one Add customer made. Make it again |
| The script fails at `Invoke-WebRequest` with 404 for `agent/latest.json` | Nothing is published yet in `trackvice-agent`. Publish from **Settings > Your storage** |
| The script fails at `Invoke-WebRequest` with 403 | The Read link to `trackvice-agent` in the code expired, or the account key was rotated. Set up Your storage again and make the code again. A device that cannot reach the storage account at all fails here too |
| "agent/latest.json is not signed with the update key in the customer code. It is refused." | The version was published with another key than the one in the code, such as from another PC. Publish from the PC that made the code, or make the code again on the PC that publishes |
| "agent/latest.json names ... It is refused." or "has no valid SHA-256 or size" | The file in `trackvice-agent` was not made by TrackVice's publishing. Publish again |
| "... has ... bytes and SHA-256 ..., expected ... It is refused." | The exe in `trackvice-agent` is not the one that was signed, or the download was cut off. Publish again |
| "TrackVice.exe /agent-install returned 1." | The reason follows, from TrackVice itself, such as a code with a setting below zero or an alert link that is not https |
| The script installs nothing and says "Not a new enrollment" | The device was enrolled more than 24 hours ago (`m` in the code, `$MaxAgeHours` with links). To test on such a device, run `TrackVice.exe /agent-install /customer "<code>"` as in [Test on one device first](#test-on-one-device-first) |
| Your storage: "Your account may not ..." | The account lacks a role on the subscription or the storage account, see [Who signs in](#who-signs-in-and-what-they-need). A blocked sign-in usually means the Azure PowerShell client is not allowed in your tenant |
| Add customer: "refused the account key (HTTP 403...)" | The key of your storage account was rotated. Set up Your storage again |
| Add customer: "Access denied" in Intune | The account needs an Intune role that can add scripts, a role that can make groups, and consent for `DeviceManagementScripts.ReadWrite.All` and `Group.ReadWrite.All`. The result page says what was not done. Add the saved script by hand, or use **Customer code… > Update the script in Intune** later |
| Customer code…: "This tenant has no platform script called TrackVice agent" | It was not added, or has another name. Add the saved script as a platform script with that name, or run Add customer again |
| **Customer code…** is not on the customer's row | The customer was not made with Add customer on this PC and Windows user |
| The script says the SHA-256 is not the expected one | Links only: the exe in the container is not the one `$Sha256` in `agent/Install-TrackViceAgent-Links.ps1` was taken from. Take the hash again from the uploaded file |
| The script fails at `Invoke-WebRequest` with 403 or 404, with links | `$Download` is wrong or expired. Opening it in a browser should download the exe |
| "The link does not allow TrackVice to upload" | The upload link lacks Create and Write, or you used the viewer link |
| "The link does not allow TrackVice to list" | The viewer link lacks Read and List, or you used the upload link |
| "The storage account refused the link" | The link expired or was cut off when copied, or the storage account's networking does not allow this network |
| "The link points at a file in the container" | Use the link of the container itself, not of a folder or a blob in it |
| The device is not in the list | Look at `C:\Program Files\TrackVice Agent\agent.log` on the device. No folder means the script did not run or stopped: check the script's device status in Intune and `IntuneManagementExtension.log`, which has the script's output. An "Upload failed" line gives the reason |
| The device is in the list twice | The agent was installed again, which starts a new session. The older row stops updating |
| No alerts arrive | Run the test in [Alerts in Teams](#alerts-in-teams). `agent.log` says "Alerts are off" when the link is not an https address, and "Alert not sent" with the reason when the workflow refused it |
| "The device has not uploaded its logs yet" | The agent uploads them when something fails, when the device goes idle and at the end. Try again later |
| In a task sequence, the device is not in the list | Look at `%WINDIR%\Temp\TrackVice\agent.log` with F8. No log in Windows PE usually means the boot image lacks WinPE-NetFx |
| New customer with links: "Your account may not ..." in Azure | The account lacks a role on the subscription or the storage account. A blocked sign-in usually means the Azure PowerShell client is not allowed: enter your own app under Client ID |
| New customer with links or Your storage: "has no account key" | The storage account has Allow storage account key access turned off. The links need it |
| New customer with links: "Access denied" in Intune | The account needs an Intune role that can add scripts, and consent for `DeviceManagementScripts.ReadWrite.All` and `Group.ReadBasic.All`. Or skip the Intune part and add the saved script by hand |
| `agent.log` says "Self-update is off" | The reason follows: an update key but no update link, a key that is not usable, or a link that cannot be opened |
| `agent.log` says "is not signed with the update key of this device" | The version was published with another key than the one the agent was installed with. Publish from the PC that has the key. With links, you can also update `$UpdateKey` |
| `agent.log` says "No update information in" | Nothing is published there yet, or the link lacks Read or points at another container |
| The agent stays on its version | It checks every 6 hours. A version that is not newer than the one it runs is not installed. `agent.log` says "Update check:" with the reason |
| The script is blocked | `TrackVice.exe` is not code signed. WDAC, AppLocker or Smart App Control can block it. Sign it with your own certificate or allow it by hash |

## What is uploaded, and security

One file per device session, `sessions/<id>.json.gz`, in the same format as a saved session, and the device's logs as `logs/<id>.zip`. It holds the computer name, the Autopilot profile and tenant, app and policy names, install command lines, exit codes and the log lines about them, the policy settings the MDM client set, and the output and error text of platform scripts. Install command lines and script output can hold secrets when a package passes them that way. The agent [masks](#masking-of-secrets) the ones it recognises, but the log files in the zip are as they are on the device. Treat the container like the logs themselves.

| | |
|---|---|
| The customer code | Holds the upload link, the Read link to `trackvice-agent`, the public update key and the alert link. Anyone with it can upload to that customer's container, post to the alert channel, and download the TrackVice versions. It cannot read uploads or list containers. Keep it like a password |
| The upload link | Can add and replace files in one customer's container. It cannot list or read, so one device cannot see another device's session, and one customer cannot reach another customer's container |
| The viewer link | Can read every session and every log zip of one customer. Treat it like a password |
| `logs/<id>.zip` | The session and the device's log files. Readable with the viewer link only |
| The alert link | Anyone with it can post to the Teams channel. The alerts carry the device name, the deployment and the text of each failure, masked |
| The Read link to `trackvice-agent` | Can read every file in that container. Keep only TrackVice files there |
| The account key of your storage | Kept on your PC, encrypted for your Windows account. It can do anything in that storage account, including reading every customer's uploads. It is used only to make customer containers and sign links. Remove in Settings forgets it |
| The publish link and the signing key | Together they put a version on every device that trusts the key. The key is on your PC only, encrypted for your Windows account. Someone who can write to `trackvice-agent` cannot make the devices run anything else, since the script and the agent check the signature, the size and the SHA-256 |
| The download link | With links only: can only download `TrackVice.exe` |
| On the device | The upload link, the alert link, the update key, the update link and the customer name, when there are any, are saved under `HKLM\SOFTWARE\TrackVice\Agent`, readable by administrators and SYSTEM only, and removed when the agent ends. In a task sequence nothing is saved there |
| In Intune | The script, with the customer code or the links, is readable by Intune administrators. |
| In SCCM | The task sequence, with the code or the upload and alert links, is readable by anyone who can read task sequences |
| On the device, otherwise | The agent only reads. It adds its folder, its task and its registry key, and nothing else. An update only replaces `TrackVice.exe` in its own folder. In a task sequence it only adds `%WINDIR%\Temp\TrackVice` |
