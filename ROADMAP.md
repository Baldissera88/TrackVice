# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 1.8.0).

## 1. Download scripts and apps during a deployment

Right-click a step, app or script in a running deployment and choose Download, to get a copy of what the device is running or installing: a PowerShell script from Intune, an app's install files or detection rule, or a script or package from a task sequence step.

- It is off by default and set by permission. An admin turns it on per customer in Your storage / Settings, and chooses who may use it. A supporter without the permission does not see the menu item.
- Works live on the device and remotely through the agent. Remote downloads go through the customer's own storage and are removed after a short time.
- Every download is written to a log with who, what and when, and secrets that appear in a script are not copied into reports.
- Nothing is downloaded that the device itself could not read.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).

## 2. Sign in on a new PC and get your customers

Install TrackVice on a laptop or lab PC, sign in with Entra ID, and your customers, storage and customer codes are there, without setting anything up again.

- The customers are kept in an Azure Key Vault in your own subscription, one secret per customer. Nothing is encrypted by TrackVice itself, and no password or link is typed in or copied.
- Access is the Key Vault role Key Vault Secrets User (read) or Officer (add and change). Remove the role and that user and PC lose access. Key Vault logs who read what, and Conditional Access and MFA apply to the sign-in.
- Sign-in uses your Entra account in the browser, so an existing session signs you in without typing a password again. TrackVice asks once for the app registration it already makes and uses the delegated Key Vault permission.
- Changes on one PC show up on the others. Each PC keeps a copy for offline use, encrypted for the Windows account as today.
- Without Azure, the same list can be exported to a password-protected file and imported on the other PC.
