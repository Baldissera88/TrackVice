# Changelog

## TrackVice 2.1.1

28 November 2026.

- **A security review of all the code**, with every flaw found fixed and tested. Nothing is known to have been misused. The ones that matter most:
  - **Install script**: a value with a curly quote (’) in the customer code, alert link or share path could run commands as SYSTEM on the customer's devices. All quote characters are now handled, and customer codes are strictly checked before they reach a script or a task sequence step.
  - **Agent as SYSTEM**: the task sequence agent kept its log and state in a folder under `C:\Windows\Temp` that any user could prepare, which let a user make SYSTEM write or delete files, stop the agent, or feed it a fake `smsts.log`. The folder is locked to SYSTEM and administrators, and logs are only read when SYSTEM or administrators own them.
  - **Your Windows sign-in**: a customers file, a shared customer, or a session from storage could make TrackVice connect to another server (an SCCM server, a share in a log path), which sends your Windows sign-in there. Imported values are checked, a new SCCM server must be confirmed once, and only local log paths are opened.
  - **Sign-in tokens** for Graph, Azure and Key Vault are only sent to Microsoft's hosts, never to a host named in a reply, and no call with a token follows a redirect.
  - **Key Vault**: a customer secret can only hold its own customer, and a customer code from the vault is only kept when it holds this PC's update key.
  - **Intune script assignment**: adding the TrackVice group to an existing script no longer turns excluded groups into included ones.
  - **What devices upload** is treated as hostile: size and depth limits, cleaned text, zip limits, and bundle files that are not logs get `.txt` added. One crafted log line can no longer stall the agent or the viewer.
  - **Secrets** are masked in more places: customer codes (`tv1:`), encoded commands, `net use`, `schtasks` and `psexec` passwords, unattend passwords, error messages, Copy details and public bug reports.
  - **Downloads**: TrackVice's own update is checked again and held locked until the installer has run, LogVice is downloaded to a folder only administrators can write, and only GitHub's hosts are followed.
  - **MSI**: a repair started by a standard user no longer runs a command as SYSTEM. TrackVice loads DLLs from the Windows system folder only.
  - HTML reports forbid scripts and remote content. CSV cells cannot start a formula.
- **What changes for you**:
  - **Shares (UNC)**: run the new permission commands from Your storage again; with the old ones, devices could overwrite each other's uploads.
  - **Agent install** fails, with a message, if its registry key cannot be locked to SYSTEM and administrators.
  - **Alert address** must be the final address; one that redirects now fails.
  - **App registration** named TrackVice with a secret, other redirect addresses or multi-tenant is no longer picked up by itself; type its Client ID in Settings.
  - **Collect the session and its logs** from an uploaded, Intune or SCCM session holds the session only, no longer this PC's own logs.
  - **Customers from the vault** on a PC without the update key keep no customer codes; get the setup first.
- **SECURITY.md**: how to report a flaw privately, what the agent does on a device, the trust model, and how to check downloads.

## TrackVice 2.1.0

28 November 2026.

- **Collect diagnostics**: on a device with the TrackVice agent, Collect diagnostics asks the agent for its logs, the MDM diagnostics of Windows (Autopilot, enrollment, provisioning and TPM), `dsregcmd /status` and the Intune Management Extension registry keys. The request is signed with your update key, belongs to that one device, is valid for 2 hours and is used once. The agent only runs its own fixed list of tools; nothing in the request is run. The window shows Requested, Received, Collecting and Uploaded, then Open in LogVice or Save zip. The agent needs 2.1.0 or later: publish it with Publish agent update.
- **Pre-flight check** (Devices > Intune / cloud): paste one or more serial numbers and TrackVice asks Intune whether each device will deploy: the Autopilot registration and profile, the device's groups, the Enrollment Status Page with its time limit compared with your earlier deployments, and the required apps with their dependencies and supersedence. Every device gets Ready, Check or Will fail, with what to fix. Read only. Copy as text.
- **Customer report**: Export > Customer report (PDF) for one device, and Report... on Overview for all devices in a period. A clean, printable page with the result, the phases, every app, policy and step, what TrackVice found, network outages and restarts. It is saved as HTML and, with Microsoft Edge on the PC, as PDF. Secrets are masked as in Copy for ticket.
- **Search, jump or act (Ctrl+K)**: one box for devices, the actions on the device in the window, pages and settings.
- **A new look**, with the same information:
  - The top of the device view has a progress ring with the time left, calmer number cards, and phases as connected steps. In a narrow window every card stays.
  - The Progress list has a tile per kind, state pills, a bar under what is running, and phase headers that stay at the top while you scroll.
  - The details of an item say what happened, the likely why and what to do, show the attempts on a small timeline, and put the evidence in a box with Open in LogVice at this line.
  - Overview opens with the answer ("6 deploying · 3 need attention"), key figures, device cards with rings, a chart of the last 7 days and cards for what needs attention.
  - The header shows where you are and the device's facts as chips. Compact, Wall, Gantt, Timeline, Findings, buttons, switches, tabs and the settings pages are redrawn.
  - In the Vice theme, failures are red instead of pink, so they never look like the accent.
  - Short animations, which are off when Windows animations are off or over Remote Desktop.

## TrackVice 2.0.11

25 November 2026.

- Publish agent update: a place that has the version already is no longer counted as "not published". The result says "1 place had it already", or "2.0.10 is already in every place" when there was nothing to publish.
- Fixed: after Publish agent update, the banner in the main window could still show an older publish to Your storage only. It now says what Publish agent update did.
- The publish texts say that installed agents update within 30 minutes during a deployment, otherwise within 6 hours.

## TrackVice 2.0.10

25 November 2026.

- **Settings > Updates**: updates have their own page, as in LogVice: Check for updates, the version you have with Check now or Install, the Release page, and What's new, which shows what changed in this version and the ones before it, also without network.
- The Publish agent update row in Settings says what it does today: it publishes to every place your customers' agents check.

## TrackVice 2.0.9

25 November 2026.

- Fixed: after reinstalling the same device several times, Overview counted every earlier deployment. Agents that stopped sending listed finished deployments and ones replaced by a newer deployment of the device, Agents counted the agents of earlier deployments that no longer exist, and Failed devices listed a failure the device has since deployed past. Now only the newest deployment of each device counts there; the earlier ones stay in the lists and the statistics as history. A finished deployment is never "stopped in the middle".
- The Agents line says why a place's version is not known, for example that this PC has no link that can read there, with the way to fix it.

## TrackVice 2.0.8

25 November 2026.

- **Expected apps** (an option in Settings > Intune / cloud): the Apps phase shows every app the deployment is going to install from the start, instead of growing one by one when Intune delivers the user's apps after sign-in. With an Intune sign-in for the customer, TrackVice asks Intune which apps are required for this user on this device. Without one, it uses the apps of earlier deployments of the same profile. Expected apps are dimmed, marked "Expected" with the reason, count in the phase, the % and the time left, and are replaced by the real item when it appears. They never count as failed, send no alerts and are not uploaded. When the deployment is done, an app assigned in Intune that never came is a finding. Each source can be turned off.

## TrackVice 2.0.7

25 November 2026.

- **Install scripts that do not fail for nothing**: the agent scripts (customer code, links and share) end with success when the agent is already installed and running, and download nothing. A self-updated agent is never replaced by an older one. Before a download they wait up to 3 minutes for the network and try up to 5 times. Update the script in Intune to get this (Customer code... > Update the script in Intune).
- **Check the script in Intune**: Customer code... shows whether the agent script runs as System and which groups it is assigned to, and warns when a group with users or All users is targeted, because the script then also runs when a user signs in. **Assign only to the device group** removes those assignments after asking.
- A later failed run of the agent's own script, while the agent runs, is a warning instead of a failed deployment. For other platform scripts that first succeeded and then failed when they ran again, the details say when, and whether the second run was for the user, with a finding when a script runs for users too. A network error is said in plain words.
- **Get publish access by signing in**: in Publish agent update, a place this PC cannot write to has **Sign in to Azure and get access**. TrackVice finds the storage account in your subscriptions, reads its key and makes the link itself, or says exactly which role is missing. Add a link shows the portal steps with the account and container filled in and checks the pasted link.
- **Agents update sooner**: an installed agent checks every 30 minutes during a deployment and every 6 hours after it, and tries again after 5 to 40 minutes after a network error, or after 30 minutes when nothing is published yet. Its log no longer repeats the same line.
- **Network outages**: the agent records when it could not reach the storage. They show in the timeline ("No network to the storage"), on the Session tab, and in the details of a failure in the same minute.

## TrackVice 2.0.6

25 November 2026.

- **Better time left**: TrackVice learns from earlier deployments of the same customer and Autopilot profile or task sequence (medians, without failed, replaced or unusual ones) and keeps that history on the PC. The estimate adds up what is left one app at a time, covers apps that are not visible yet and the usual restarts, and shows a range and the finish time, for example "about 25 min left · 20–35 · done ~16:35". When a deployment passes the usual time it says "taking longer than usual". The overall % uses the same figures.
- Fixed: when nothing was published yet, agent.log said "That session is no longer in the container". It now says that nothing is published in that place yet and how to publish there.

## TrackVice 2.0.5

25 November 2026.

- **Publish to every place agents check**: Publish agent update now lists every place your customers' agents check for updates (Your storage, a customer's own storage account, a share), with the customers that use it and the version published there. Places that are behind and that this PC can write to are switched on, and one press publishes to all of them, with a result line for each. A place this PC cannot write to says what is missing, and Add a link saves a link for it.
- Overview > Agents compares each agent with the place it checks, not with Your storage, and says where to publish, for example "Contoso: 3 on 2.0.4; contosoagents/trackvice-agent has 1.4.0 (behind)".
- Fixed: agents of a customer with its own storage account never updated, because Publish only published to Your storage.

## TrackVice 2.0.4

25 November 2026.

- **A share instead of Azure storage**: in New customer, step 2 Storage now offers Azure storage or **Share (UNC)**, such as \\server\TrackVice\Contoso, for domain-joined devices on the company network (SCCM task sequences and Hybrid Autopilot). Check tests from this PC that the share can be reached, written and read. Copy icacls commands gives the permissions the devices need. The Links step is skipped, TrackVice.exe and the signed agent update go to the share's agent folder, and the platform script and the task sequence command are made with the share filled in. No customer code is made for a share.

## TrackVice 2.0.3

25 November 2026.

- Fixed: on a device installed from an older image, one app or log entry with an old date could make the deployment look months long (for example "4584 h"). The start is now taken from the enrollment itself, and anything recorded more than an hour before it no longer pulls the start, the phases or the durations back. The old time is still shown in the item details as "Recorded earlier".
- **Overall % by time**: the overall progress now weighs each item by how long it usually takes, from earlier deployments of the same customer when there are some. Microsoft 365 Apps counts as minutes, a policy as seconds, so the % no longer jumps to 97 % while a large app still installs. It stays at most 99 % until everything is done.
- Fixed: Overview listed a device under "Agents that stopped sending" when its old deployment was replaced by a new one on the same device, for example after reinstalling a VM. Such a deployment now shows as "Replaced by a newer deployment".

## TrackVice 2.0.2

25 November 2026.

- **Updates are found at once**: TrackVice checks for a new version at every start, about 8 seconds after it opens, and every 6 hours while it stays open, instead of once a day.
- **Update while watching**: following or watching a deployment no longer blocks the check or the install. TrackVice restarts to update, then opens again and continues watching this device.
- Fixed: after turning Watch off, a found update could not be installed until TrackVice was restarted. Install now checks the device each time it is pressed. It is still not installed in Windows PE, as SYSTEM, while a task sequence or Windows setup and the Enrollment Status Page run, or when the policy DisableUpdateCheck is set.

## TrackVice 2.0.1

24 November 2026.

- **Open in LogVice at the line**: with LogVice 2.0 or later, the log behind a step opens on its line, in the same theme as TrackVice. An older LogVice opens the log as before, and the status bar says that LogVice 2.0 can open at the line.

## TrackVice 2.0.0

24 November 2026.

A new major version, in line with LogVice 2.0. It brings together everything since 1.10.0:

- Your storage in one step: sign in to Azure, pick the subscription, make or choose the storage account and set it up. The subscription box, the ID line and the account list always belong to the same subscription, also in New customer and Add customer.
- The same setup on every PC: customers, Your storage and the update key are saved to your Key Vault and fetched on another PC after an Entra ID sign-in.
- Names from Intune for platform scripts, apps, remediations and configuration profiles.
- Downloads of a deployment's scripts and apps, by permission.
- The overview fits a narrow window: tiles shrink and wrap instead of disappearing.
- Errors wrap in the footer, and subscriptions show the start of their ID.

See 1.11.12 and older below for each change.

## TrackVice 1.11.12

24 November 2026.

- Fixed: in a narrow window the overview no longer removes tiles. Items, In progress, Waiting, Failed, Findings and the overall progress shrink first, then wrap to a second row; Copy for ticket, the note and Open in Intune move to their own row instead of disappearing. The phase tiles get narrower with shortened text, so all phases stay visible down to about 700 pixels.

## TrackVice 1.11.11

24 November 2026.

- Fixed: New customer and Add customer had the same subscription problem as Your storage. The box showed one subscription while the storage accounts and the resource group belonged to another, so an old error stayed and the account list was empty. They now read the subscription the box shows, and Create names the subscription with its full ID.

## TrackVice 1.11.10

24 November 2026.

- Fixed: Set up in Your storage now always uses the subscription the box shows, and the confirmation names the subscription with its full ID, so it is clear where the account and resource group are made.

## TrackVice 1.11.9

24 November 2026.

- Fixed: in Your storage, picking another subscription did nothing on some PCs. The window now checks every moment which subscription the box shows and reads that one when it is free, so it no longer depends on the box telling the window about the change.

## TrackVice 1.11.8

24 November 2026.

- Fixed: in Your storage the subscription you pick is now remembered by TrackVice itself and the box is set back to it after every step, so the box, the ID line and the storage accounts that are read always belong to the same subscription.

## TrackVice 1.11.7

24 November 2026.

- Fixed: in Your storage the storage accounts were read for another subscription than the one shown in the box. The subscription named in the box is now the one that is read.

## TrackVice 1.11.6

24 November 2026.

- New: when Your storage finds no storage accounts, the line under Existing account says what Azure answered: the subscription, how many resource groups and storage accounts it returned, and the account you are signed in as.

## TrackVice 1.11.5

24 November 2026.

- Fixed: in Your storage and Add customer, picking another subscription while the window was still busy (for example right after signing in) was ignored. The box showed the new subscription, but the ID line and the list of storage accounts still belonged to the old one, so the account you wanted was not listed. The choice is now read as soon as the window is free.

## TrackVice 1.11.4

24 November 2026.

- Fixed: an error about one subscription stayed at the bottom of the Your storage and Add customer windows after another subscription was picked. It is cleared when the subscription changes.

## TrackVice 1.11.3

24 November 2026.

- The subscription lists in the setup windows show the start of each subscription ID next to its name, because two subscriptions can have the same name and the storage account may be in the other one.

## TrackVice 1.11.2

24 November 2026.

- Fixed: a long message at the bottom of a setup window, such as the one that says which Azure role is missing, was cut off after one line. It now wraps and the footer grows to show all of it.

## TrackVice 1.11.1

24 November 2026.

- **Edit customer** has a **Get it from Your storage** button under Upload location. It makes the customer's container and viewer link from Your storage, the same way Add customer does, and fills the box in. It also shows the upload link for the devices, with Copy. The button is only there when Your storage is set up on this PC.

## TrackVice 1.11.0

23 November 2026.

- **The same setup on every PC you sign in on**: **Save my setup to the vault** keeps Your storage and the update key in your Key Vault, next to the customers. On another PC, **Get my setup from the vault** fetches them, and that PC can publish too. The same button is in Publish, when it finds another update key than the one on this PC, and in Get started. A key on a PC is never replaced without asking, and nothing in the vault is deleted. The update key is its own secret, so its role can be given only to the people who may publish.
- Publish checks the update key before it publishes to Your storage, and says when it is another one than the one in your storage.

## TrackVice 1.10.0

23 November 2026.

- **Get started**: a page that lists the setup in order (Your storage, Publish, Add customer, the script or task sequence step, a test device), says what each step makes, shows what is done and what is next, and has one button per step. It is in Devices > Get started and in Help, and opens once at start when nothing is set up.
- **Publish agent update** says when Your storage is not set up and offers to set it up, instead of an empty box asking for a SAS link. With Your storage it names the account. The link or folder, and the update key details, are behind a button.
- **Sign in once**: the Azure sign-in and each customer's Intune sign-in are reused by the next dialog in the same run, which asks only for extra permissions. Sign out in Settings, or choosing another customer, forgets them.
- Fixed: on a Windows Server, such as the SCCM site server, TrackVice no longer shows a "Hybrid Entra join" item as a deployment because of repeating device registration events. A server is not followed as an Autopilot enrollment; its task sequences are still followed.

## TrackVice 1.9.0

23 November 2026.

- **Download scripts and app details from a deployment**: right-click an Intune script or remediation, an app, an SCCM task sequence step or an SCCM application and choose Download. You get the script text and command lines, and for an app its details (install command, detection rule, requirements, assignments), not its installer files. It is off until you turn it on per customer, asks before each download, hides secrets, and every download is written to a log with who, what and when.
- **Customers in your own Key Vault**: Settings > Customers > Key Vault keeps your customers in an Azure Key Vault, one secret per customer. On a new PC you sign in with Entra ID and get them. It uses the Key Vault roles (Secrets User to read, Secrets Officer to add and change), and only adds and updates, never deletes.
- **A password-protected customers file**: exporting with the storage links needs a password and makes a .tvcustomers file. Import reads it and the older JSON.
- Fixed: when TrackVice follows a deployment (or a task sequence or Windows setup runs), Update now no longer offers to install and then fails with "could not be installed automatically". It says at once that the update waits until the deployment is done, because the update restarts TrackVice, and offers the release page.

## TrackVice 1.8.0

23 November 2026.

- Fixed: after Publish, the banner showed a squeezed piece of "What does Publish do?" next to Close. After publishing it shows only the result and Close, and the banner's buttons are sized from their text every time, so they are never cut.

## TrackVice 1.7.0

23 November 2026.

- Fixed: after 1.6.0 the header buttons were all squeezed to the same width and lost their look. They keep their own width again and still fold into More in a narrow window.
- Fixed: Copy for ticket, Open in Intune and the note are proper buttons; the detail panel has its margin back, its buttons wrap in a narrow window, and phase titles end with an ellipsis instead of being cut.

## TrackVice 1.6.0

23 November 2026.

- **The whole task sequence**: an SCCM run shows every group and step of the task sequence from the start, read from the site: Waiting until it runs, Skipped or Disabled. Steps that install from a variable list show the variable name.
- **Add the agent to a task sequence for you**: Devices > SCCM / on-premises makes the package with TrackVice.exe, distributes it, checks the boot image for WinPE-NetFx and adds the two steps with the customer code. It asks first, keeps a copy of the task sequence and can undo. The Publish banner offers to update the package when it holds an older version.
- **What you need**: Add customer, Your storage and the guide with links say which account, role and consent each step needs, with a checklist, and say which role or consent is missing and who can give it.
- **Your own app registration**: Settings > Intune / cloud explains why and how, lists the exact Microsoft Graph permissions, and can make the app registration for you.
- **Overview counts deployments**: today, 7 days, 30 days and all, every run counted ("48 deployments on 41 devices"), and the devices deployed more than once.
- **LogVice when it is missing**: a LogVice button asks first, then downloads LogVice from GitHub, checks it against the release's SHA256SUMS.txt and opens the logs. Settings > LogVice shows the version and a Download button.
- **The agent version** on the device, in the agent device list, on the wall, on Overview (Agents) and in reports. Needs attention lists agents behind the published version.
- **Who is enrolling**: the user on the device, in Overview, Recent devices, the device list, the wall and search.
- **For support**: serial, model, Autopilot profile and group tag, Entra and Intune IDs, network, disk, TPM, Secure Boot and time zone in one place; time left; Copy for a ticket; Open in Intune and Entra; notes per device.
- **Time the device was off does not count**: a shutdown of more than 15 minutes is shown as time off ("26 min · off 9 h 01 min"), not as deployment time, in every view, report and statistic, and the device shows "Off since" instead of stalled.
- **Correct time**: the device's time zone and clock, a finding when the zone changed during the deployment or the clock is off, and an option in Add customer to turn on automatic time zone on new devices. docs/AGENT.md explains the Intune setting for a fixed time zone.
- Fixed: the Publish banner no longer runs out of a narrow window.
- Fixed: with no e-mail program on the PC, the text is copied and the page says where to send it.
- Fixed: the window, Overview and the dialogs work at small sizes and at 125 % and 150 % scaling; the header buttons fold into More.
- Fixed: the guide with links asked for a Microsoft Graph permission that does not exist (Group.ReadBasic.All). It asks for GroupMember.Read.All.

## TrackVice 1.5.0

18 November 2026.

- **Overview** is the home page on a PC that is not deploying: devices deploying now, today and the last 7 days, what needs attention (failed devices, agents that stopped sending, findings, a newer version to publish), recent and pinned devices, and setup. All customers or one, Intune, SCCM or both.
- **Recent devices** with one search over all customers and sources, pins, and **Back and Forward** (Alt+Left, Alt+Right and the mouse buttons).
- **Publish** is easy to see: a banner after a newer version starts, a tile on Overview, and **What does Publish do?** next to every Publish button.
- **Help**: Report a problem, Suggest a feature, Tell the author you use TrackVice, and Downloads. They open a pre-filled GitHub issue or e-mail; TrackVice sends nothing by itself. GitHub has bug and feature forms.
- **Settings** is divided into subjects, with search, a mark on changed settings and Reset on each page. Command line and Keyboard shortcuts are under Help.
- PackVice and NotifyVice are taken out of TrackVice for now. LogVice stays.

## TrackVice 1.4.0

17 November 2026.

- **Themes**: Vice is the default on a new install. Three new themes: **Terminal** (green on black, like an old phosphor screen), **Amber** (amber on black) and **Synthwave** (dark purple with neon pink and cyan). Terminal and Amber have square corners and show numbers, times and durations in a monospace font. Settings shows each theme with a small colour sample, and `--theme terminal|amber|synthwave`. Light's secondary text is darker and easier to read.
- **SCCM: applications from a variable list**: each application an Install Application step installs from a task sequence variable list is its own row, Done or Failed with its exit code and how long it took; the step shows how many are done.
- **SCCM: a newer run**: a device window that follows a run says when a newer run of the same task sequence starts on the device, with Follow it.
- Fixed: a task sequence that restarts the computer in Windows and resumes was split into two runs, so the run being followed never ended. It is now one run.
- Fixed: the warning a Run PowerShell Script step with execution policy Bypass sends (11910) became a step of its own shown as Unknown, and could keep a finished run at Waiting. It is now a warning on that step.

## TrackVice 1.3.0

17 November 2026.

- **Welcome.** On first start TrackVice offers four choices: Follow this device, Follow devices from my PC, Open logs or a session, and Look around with the demo. It does not show on a device that deploys, in Windows PE, as SYSTEM or when started with an option. Help > Welcome shows it again.
- **Getting started**, for your own organization or for customers: Your storage, the first customer, a test on one VM, and the device list. Each step opens its guide and ticks itself off. Help > Getting started.
- **Devices in two parts.** A switch at the top of the Devices menu chooses Intune / cloud or SCCM / on-premises, and the menu shows only that side. Each side says what it needs when nothing is set up. SCCM / on-premises has the SMS Provider connection and Add the agent to a task sequence. The demos are in Devices > Demos.
- Statistics and the list of devices with the agent filter by Intune, SCCM or both. `/side intune|sccm|both` does the same for `/fleet-report` and `/export-data`.
- **TrackVice updates itself** from its GitHub releases. Once a day at start it asks GitHub for the latest release and shows a line in the status bar when there is a newer one. Install checks the download against `SHA256SUMS.txt`, installs it and opens TrackVice again. Settings > Updates turns it off; the policy value `DisableUpdateCheck` under `HKLM\SOFTWARE\Policies\TrackVice` turns it off for everyone.
- Every release now has `SHA256SUMS.txt`, with the hashes of the MSI, the exe and the three agent scripts.
- **Task sequences in SCCM: one row per run.** A run begins at the task sequence start message (11140), so Started and Duration are those of the run. Show earlier runs lists the runs before the newest.
- Status messages a device sends late, such as those it kept during Windows PE, now show in the run they belong to.
- **Add customer** asks before it updates a customer that exists: Update makes new links and the old ones stop, Choose another name goes back to the name.
- Step 3 of Add customer has three choices for the assignment: create the group TrackVice - Autopilot devices, a group you choose from a search of the tenant's groups, or no assignment. In a production tenant, start with a small test group.
- The install script says when `$Customer` holds a customer name instead of the code, and where to copy the code.

## TrackVice 1.2.1

16 November 2026.

- Your storage finds the account your customers already upload to and selects it.
- Each customer container has two stored access policies, upload and viewer, and the customer's links refer to them. **New links** and **Remove customer** stop one customer's links at once; deleting the container is a separate choice.
- **Check security** for Your storage: anonymous blob access, HTTPS only, TLS 1.2, container soft delete and the lifecycle rule `trackvice-customers` that deletes uploads in every `tv-` container after 30 days. Each setting is changed only after you confirm it.
- Optional: do not keep the account key on this PC; Add customer and Publish then sign in to Azure.
- Add customer explains the two choices, Your storage and the customer's own Azure, and lists what is uploaded.
- Fixed: with the customer's own Azure, an unused container trackvice-agent was made in their account.

## TrackVice 1.2.0

16 November 2026.

- **Add a customer in three steps.** Devices > Add customer: name, sign in to the customer's tenant, Create. TrackVice makes a private container for the customer, the dynamic group TrackVice - Autopilot devices, the platform script TrackVice agent assigned to it, and adds the customer to the device list. The customer's data can also be kept in their own Azure.
- **Your storage, set up once.** Settings > Your storage: one storage account in your Azure for all customers, where TrackVice versions are published, signed with your key.
- **The platform script has one line**, `$Customer`, the customer code. Devices download the newest TrackVice you published and check its signature, size and SHA-256 before they run it, so the script needs no SHA-256 and no change when you release a new version.
- When TrackVice is newer than the version in your storage, the status bar offers to publish it.
- Customers > Customer code… copies the code, changes the alert link and updates the script in Intune.
- The script with links is now `agent/Install-TrackViceAgent-Links.ps1`. Existing setups keep working.
- Fixed: the install scripts downloaded TrackVice.exe to a fixed name in the Windows Temp folder, where another local account could replace it between the check and the start. They now use a new folder only SYSTEM and administrators can change.
- `/agent-install /customer <code>`.

## TrackVice 1.1.3

16 November 2026.

- Apps lists every app in one place. The apps the ESP waits for come first, in Device setup waits for and Account setup waits for, and Device setup and Account setup show their ESP steps and policies. Device setup and Account setup still count their apps, so the phase cards are unchanged.

## TrackVice 1.1.2

16 November 2026.

- Apps: one phase for every app the ESP does not wait for, grouped into Device, after the ESP; User, after the ESP; Available in Company Portal; Dependencies; and Uninstall. Each app says whether it targets the device or a user, and whether it is required or available. Available apps are not counted as waiting.
- The ESP step Apps in device setup and account setup says which app it waits for.
- The agent stays until 48 hours after the deployment is done instead of 48 hours after it was installed, waits through a pre-provisioning reseal for the user phase, and always removes itself after at most 30 days (`$MaxDays`, `/max-days`).
- Fixed: on a device that changed time zone during the deployment, such as an en-US image moved to the time zone of its region, Intune Management Extension log lines written before the change were read hours wrong, and the deployment seemed to have started hours earlier. TrackVice now reads the changes of time zone from the System event log.
- Fixed: an ESP step could stay Installing when Windows never wrote its time, such as after Continue anyway.
- Fixed: a log separator line could show as an app's text.
- Fixed: a platform script with a warning in its output could stay Installing.

## TrackVice 1.1.1

16 November 2026.

- Fixed: on a device where Windows was installed long before the enrollment, restarts from months back were read from the System event log. The deployment then seemed to have started then, and ran for thousands of hours. Restarts now count from an hour before the first thing the deployment logged, and not from before Intune Management Extension came on the device.
- Fixed: a restart that was asked for but where Windows never started again stayed Installing. It is now closed when the next restart begins, or on the device after half an hour, with the note that no start of Windows was logged.

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
