#!/bin/sh
# wixl writes 8.3 names only. This adds the long names to the Directory, File and Shortcut tables,
# and adds what wixl leaves out of TrackVice.wxs: closing a running TrackVice before an upgrade.
set -e
msi="$1"
[ -f "$msi" ] || { echo "Usage: $0 <TrackVice.msi>" >&2; exit 2; }
msi="$(cd "$(dirname "$msi")" && pwd)/$(basename "$msi")"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"
for t in Directory File Shortcut CustomAction InstallExecuteSequence; do msiinfo export "$msi" $t > $t.idt; done
python3 - <<'PY'
def patch(name, fn):
    lines = open(name + '.idt', newline='').read().split('\r\n')
    out = lines[:3] + [fn(l.split('\t')) for l in lines[3:] if l] + ['']
    open(name + '.idt', 'w', newline='').write('\r\n'.join(out))
def d(c):
    if c[0] == 'INSTALLDIR': c[2] = 'TRACKVICE|TrackVice'
    return '\t'.join(c)
def f(c):
    if c[0] == 'TrackViceExe': c[2] = 'TRACKVICE.EXE|TrackVice.exe'
    return '\t'.join(c)
def s(c):
    if c[0] == 'StartMenuShortcut':
        c[2] = 'TRACKVICE|TrackVice'
        c[4] = '[#TrackViceExe]'
    return '\t'.join(c)
patch('Directory', d); patch('File', f); patch('Shortcut', s)

# wixl leaves out the CloseTrackVice action from TrackVice.wxs and ignores MajorUpgrade Schedule.
# Add the action (34 exe in a directory + 64 ignore exit code + 1024 deferred + 2048 no impersonation),
# and run it right after InstallInitialize, before the files are installed. The old version is removed after
# InstallFinalize: Windows Installer allows no action between InstallInitialize and RemoveExistingProducts (error 2613).
# taskkill.exe is named by its full path in the 64-bit system folder, in quotes, so no search order decides what runs
# as SYSTEM. A row wixl wrote itself is replaced, so the command is always this one.
close = ['CloseTrackVice', '3170', 'SystemFolder', '"[System64Folder]taskkill.exe" /F /IM TrackVice.exe', '']
found = []
def a(c):
    if c[0] != 'CloseTrackVice': return '\t'.join(c)
    found.append(1)
    return '\t'.join(close)
patch('CustomAction', a)
if not found:
    with open('CustomAction.idt', 'a', newline='') as fh:
        fh.write('\t'.join(close) + '\r\n')
# Not in a repair (REINSTALL): a repair is the one run a standard user can start, and nothing as SYSTEM should open
# a console window then.
seq = {'CloseTrackVice': '1501', 'RemoveExistingProducts': '6700'}
cond = {'CloseTrackVice': 'NOT REINSTALL'}
placed = []
def q(c):
    if c[0] in seq: c[2] = seq[c[0]]
    if c[0] in cond: c[1] = cond[c[0]]
    placed.append(c[0])
    return '\t'.join(c)
patch('InstallExecuteSequence', q)
if 'CloseTrackVice' not in placed:
    with open('InstallExecuteSequence.idt', 'a', newline='') as fh:
        fh.write('\t'.join(['CloseTrackVice', cond['CloseTrackVice'], seq['CloseTrackVice']]) + '\r\n')
PY
msibuild "$msi" -i Directory.idt File.idt Shortcut.idt CustomAction.idt InstallExecuteSequence.idt
# Stop when the MSI does not hold the command and the condition: a release must never go out with another one.
msiinfo export "$msi" CustomAction | grep -qF '"[System64Folder]taskkill.exe" /F /IM TrackVice.exe' || { echo "CloseTrackVice is not in $msi." >&2; exit 1; }
msiinfo export "$msi" InstallExecuteSequence | grep -q '^CloseTrackVice.NOT REINSTALL.1501' || { echo "CloseTrackVice has no condition in $msi." >&2; exit 1; }
