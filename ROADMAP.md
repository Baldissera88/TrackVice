# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 1.11.0).

## 1. First use in a few minutes: Quick start

A first-time user goes from install to watching a first deployment in four steps, with no choices that have a good default.

- **Step 0, nothing to set up.** On first start the page offers "Follow this device now": TrackVice follows the deployment on the PC it runs on, with no sign-in and no storage. This works today and is made the first thing you see.
- **Step 1, sign in once** with your Microsoft work account. It is reused for everything below.
- **Step 2, one button: Set up everything.** TrackVice makes what is needed in order and shows each part as it is done: the storage account (resource group, containers, lifecycle rule, access policies), the update key, the first published version, the customer with its container and links, and the customer code. The only questions are the subscription, the customer name and the Intune device group, each with a default.
- **Step 3, add the script.** TrackVice adds the platform script to Intune and assigns it to the group (or copies the task sequence step), and says what it did and where to see it.
- **Step 4, watch live.** The page opens the device list and says when the first device shows up.
- **A checklist at the top** shows the missing role or consent for a step and who can give it, and the next step to do. Nothing is hidden: each part can still be set up by hand.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).
