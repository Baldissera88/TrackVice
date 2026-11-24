# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 1.11.11).

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

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).
