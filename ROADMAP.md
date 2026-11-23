# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 1.10.0).

## 1. The same setup on every PC you sign in on

Sign in with Entra ID on any PC and see the same thing: your customers, Your storage, the published version, and the right to publish, without setting up again.

- **Your whole setup in your Key Vault**, not only the customers: Your storage (account name, the links, the account key when kept), the update key and the customers, as secrets. Signing in on a new PC fetches them and keeps a copy for offline use, encrypted for the Windows account as today.
- **The update key can move.** Today it stays on the PC that made it, so only that PC can publish. On the PC that has it now, one button saves it to the vault, so the codes and devices you already have keep working. Any PC you sign in on can then publish.
- **Roles decide who can do what.** Key Vault Secrets User reads the customers and storage settings. The update key is its own secret with its own role assignment, because whoever can read it can sign updates for every device: give it only to the people who may publish.
- **Changes show everywhere.** A new customer, a renewed link or a new published version on one PC shows on the others.
- **Without Azure, nothing changes.** Setup by links and the export file still work as they do.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).
