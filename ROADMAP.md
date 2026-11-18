# Roadmap

What is planned for the next version of TrackVice.

Everything that was here is released in TrackVice 1.5.0: Overview as the home page, recent devices with search and Back and Forward, Publish made clear, Report a problem, Suggest a feature, Tell the author, Settings by subject, and only LogVice among the other tools. See CHANGELOG.md.

Wishes are welcome: in TrackVice, Help > Suggest a feature, or [Discussions](https://github.com/Baldissera88/TrackVice/discussions).

## 1. Get LogVice when it is missing

- When a LogVice button is used and LogVice is not found, TrackVice asks first: "LogVice is not on this PC. Download LogVice <version> from GitHub?" with **Download and open**, **Find LogVice.exe** and **Cancel**. Nothing is downloaded without that click.
- It downloads the latest LogVice release from github.com/Baldissera88/LogVice, checks it against the release's SHA256SUMS.txt, and stops with a clear message if the check fails.
- On a normal PC: installs the MSI when the user is an administrator, otherwise keeps LogVice.exe in TrackVice's own folder under %LOCALAPPDATA%. Then opens the logs in it, as the button meant to.
- On a device that is deploying (Shift+F10 in OOBE, F8 in a task sequence): only on the same click, keeps LogVice.exe in a temporary folder for this session; never installs anything there. In Windows PE it says that LogVice needs the full Windows.
- Shows progress and the size; can be cancelled. Remembers where LogVice is afterwards. Settings > LogVice shows the version found and a Download button.

## 2. Fix: the Publish banner is cut off

- At a normal window width the banner's buttons run out of the window ("What does Publish d..."). The banner must wrap its text and keep its buttons inside the window at every width.
