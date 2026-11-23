# Roadmap

What is planned for the next version of TrackVice.

Everything released so far is in CHANGELOG.md (latest: TrackVice 1.9.0).

## 1. Set up without the Azure portal

Setting up Your storage, the update key, Publish and a customer takes many steps in different places, with links and keys to copy by hand. The aim is one guided setup where you sign in once.

- **One sign-in.** A single Entra ID sign-in for the whole setup, shown at the top with who and which tenant. It is reused for Azure, storage, Intune and Key Vault, so each step does not ask again.
- **TrackVice creates the storage.** It lists your subscriptions, creates or picks a resource group, makes the storage account (with a free name suggested), the containers, the lifecycle rule and the access policies, and shows what it made. It needs the Azure role Contributor on the resource group.
- **No links to paste.** Publish and the viewer use your signed-in account on your own storage (role Storage Blob Data Contributor). SAS links are made by TrackVice only for the agents, and are never typed in.
- **The update key is automatic.** It is made and used by TrackVice, and the fingerprint and script lines move under Advanced.
- **A checklist.** One page shows each part as done, missing or failed, with the role or consent that is missing and who can give it, and a button for the next step.
- **Plain words.** Each step says what it makes and why, and the portal terms (SAS, container, stored access policy) are explained where they appear.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).
