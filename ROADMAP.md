# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 2.1.0).

## 1. First use in a few minutes: Quick start

A first-time user goes from install to watching a first deployment in four steps, with no choices that have a good default.

- **Step 0, nothing to set up.** On first start the page offers "Follow this device now": TrackVice follows the deployment on the PC it runs on, with no sign-in and no storage. This works today and is made the first thing you see.
- **Step 1, sign in once** with your Microsoft work account ("Sign in with Microsoft" on the page and in the header). One sign-in is used for Azure, your Key Vault and Intune, and is kept on the PC, encrypted for your Windows account, until you sign out.
- **A setup you made on another PC is found by itself.** After the sign-in TrackVice looks for your Key Vault in your subscriptions. When it finds your setup there, it shows what it found (Your storage, the update key, the customers) and one button: **Use this setup**. Nothing is changed in Azure or Intune, and Step 2 and 3 are skipped.
- **Step 2, one button: Set up everything.** TrackVice makes what is needed in order and shows each part as it is done: the storage account (resource group, containers, lifecycle rule, access policies), the update key, the first published version, the customer with its container and links, and the customer code. The only questions are the subscription, the customer name and the Intune device group, each with a default.
- **Step 3, add the script.** TrackVice adds the platform script to Intune and assigns it to the group (or copies the task sequence step), and says what it did and where to see it.
- **Step 4, watch live.** The page opens the device list and says when the first device shows up.
- **Saved to your Key Vault at the end**, so the next PC you sign in on gets the same setup.
- **An account button in the header on every page**: your name and account. It opens a panel that says what the sign-in is used for (Your storage, the Key Vault, Intune for each customer, Intune names) with Ready, In sync or Needs consent, and buttons for Give consent, Switch account and Sign out. It replaces the separate sign-in buttons in the dialogs.
- **A checklist at the top** shows the missing role or consent for a step and who can give it, and the next step to do. Nothing is hidden: each part can still be set up by hand.

## 2. TrackVice-hosted storage as the default

New users should not have to think about Azure storage. During setup the default choice is storage hosted by TrackVice; your own storage stays as the second choice.

- **Default: TrackVice-hosted storage.** You sign in with Entra ID (a multi-tenant app registration), and a small service (an Azure Function) gives TrackVice a short-lived SAS link to a container that belongs only to your tenant. No key is ever built into the app or the repo, and no tenant can read another tenant's data.
- **Data:** stored in the EU, deleted after 7 days, with a quota per tenant against misuse. The uploads hold device names, user names, the tenant ID and logs, so this is not anonymous: setup asks for clear consent, and a privacy policy and a data processing agreement come with it.
- **Second choice: your own storage**, set up with one click as Your storage does today, for organizations whose security rules keep logs inside their own tenant. You can move from hosted to your own storage later.
- **If the service is down,** TrackVice says so and offers to switch to your own storage.

## 3. Signed files: no "unknown publisher" warning

Today the files are not code signed, so Windows SmartScreen and the UAC prompt warn about an unknown publisher.

- **Sign every file** that is released or published to storage: TrackVice.exe, TrackVice.msi, the agent and the PowerShell scripts, with a timestamp so the signature stays valid after the certificate expires.
- **Microsoft Artifact Signing** (formerly Trusted Signing) in Azure. It is a monthly fee, the keys stay in Azure, and the signature gives a base reputation in SmartScreen. Public Trust is open to organizations in the EU, but individual developers only in the USA and Canada, so the publisher must be a company (for example MANSOFT A/S, if it can and wants to be the publisher). The other choice is an OV code signing certificate from a certificate authority, which today must be kept on a hardware token or in a cloud HSM.
- **Signing in the build**, so no unsigned file is ever released, and a check in TrackVice that an update is signed by the same publisher before it is installed.
- **What it changes:** the publisher name is shown instead of "Unknown publisher". SmartScreen may still warn for a short time after the first signed release while reputation builds up. Files deployed by Intune or SCCM are not checked by SmartScreen, so devices are not affected either way.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).
