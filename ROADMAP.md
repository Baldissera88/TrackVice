# Roadmap

What is planned for the next version of TrackVice.

Released in 1.5.0: Overview as the home page, recent devices with search and Back and Forward, Publish made clear, Report a problem, Suggest a feature, Tell the author, Settings by subject, and only LogVice among the other tools. See CHANGELOG.md.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).

In the test build 1.5.1, not released yet: get LogVice when it is missing, the Publish banner wraps, the agent version, who is enrolling and the support facts, time off that does not count, and correct time on the devices.

## 1. More help while setting up a customer

- Every step of Add customer and Your storage says, before it starts, which account it needs and why: the Azure role for making or using the storage account (Contributor, or Storage Account Contributor on an existing account), the Intune role and the admin consent for the platform script and the group, and what each sign-in will be asked for. A "What you need" box at the top of step 1, with a short checklist the user can tick.
- When a sign-in lacks a role or consent, the message says which role is missing, who can give it, and where in the portal.
- The same as a table in docs/AGENT.md and in Getting started.

## 2. App registration: what, why and how

- A short page in Settings > Intune / cloud (and in docs/AGENT.md) that explains the choice between Microsoft Graph Command Line Tools (the default) and the customer's own app registration:
  - why one's own can be better: the customer controls it and can see and remove it, it can be limited to the read permissions TrackVice uses, Conditional Access and sign-in logs can target it, and some tenants block Microsoft Graph Command Line Tools or require admin consent for it;
  - what it needs: a public client app (no secret), the redirect URI and "allow public client flows" for the device code sign-in, the delegated Graph permissions TrackVice asks for (listed from the code, read-only ones apart from the platform script and group ones used by Add customer), admin consent, and who can make it (Application Administrator or Cloud Application Administrator);
  - step by step in the Entra admin center, and the Client ID field where it goes.
- Optional: a button that makes the app registration in the customer's tenant after a sign-in with the needed role, with exactly those permissions, and fills in the Client ID. Asks first, shows what it will make.

## 3. The whole task sequence

- Today TrackVice shows only the steps that have sent status: steps and groups that have not run yet, or never run because of a condition, are missing (seen on a real site: whole groups were missing until they ran).
- Read the task sequence itself from the SMS Provider (SMS_TaskSequencePackage, its Sequence) when a run is opened, and show every group and step in the right order from the start: Waiting until it runs, Skipped when the engine skipped it, Disabled when it is disabled in the task sequence. Steps that install from a variable list show the base variable name.
- Needs only the read rights TrackVice already uses on the SMS Provider; when the task sequence cannot be read, show what the status messages give, as today, and say why.
- The same for the agent in a task sequence where the task sequence is in the environment (_SMSTSTaskSequence, if it is there and readable; check before using it).

## 4. Fix: e-mail when there is no mail program

- Seen: "We can't open this 'mailto' link. Your device needs a new app to open this link." when Windows has no mail program set.
- Before opening a mailto link, check that Windows has a program for mailto. When it has none, or opening fails, copy the text to the clipboard and show: "There is no e-mail program on this PC. The text is copied: paste it into an e-mail to dbal.itx@gmail.com", with Copy address and Open a discussion instead.

## 5. Fix: the window at small sizes

- Seen: on a small window, the Overview title runs into the header buttons and the tiles overlap.
- The header buttons fold into a "More" menu when there is no room, the title never overlaps them, and Overview's tiles go into one column below a width. Check every main view, the Settings window and the dialogs at 900 x 600 and at 125 % and 150 % scaling.

## 6. Overview: how many deployments

- Overview shows the total number of deployments for the chosen customer and side (Intune, SCCM or both): today, the last 7 days, the last 30 days and all that TrackVice can see.
- Every run counts, also when the same device is deployed several times: "48 deployments on 41 devices". A device deployed more than once shows how many times, with a link to its runs, and Overview has a short list "Deployed more than once" (device, times, last result), since a device that is deployed again and again is often worth a look.
- Each number opens the list of deployments behind it, filtered.
