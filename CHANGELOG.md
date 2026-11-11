# Changelog

## TrackVice 0.0.1

First preview, 11 November 2026. It has run with the built-in demos and the tests, not yet on a real device.

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
