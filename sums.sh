#!/bin/sh
# Writes SHA256SUMS.txt next to TrackVice.msi and TrackVice.exe, as sha256sum writes it: "<hash>  <file>" lines.
# It lists the files of a release: the MSI, the exe and the scripts in agent/. TrackVice checks an update against it.
set -e
cd "$(dirname "$0")"
for f in TrackVice.msi TrackVice.exe agent/Install-TrackViceAgent.ps1 agent/Install-TrackViceAgent-Links.ps1 agent/New-TrackViceCustomer.ps1; do
  [ -f "$f" ] || { echo "$f was not found." >&2; exit 1; }
done
{
  sha256sum TrackVice.msi TrackVice.exe
  (cd agent && sha256sum Install-TrackViceAgent.ps1 Install-TrackViceAgent-Links.ps1 New-TrackViceCustomer.ps1)
} > SHA256SUMS.txt.tmp
mv SHA256SUMS.txt.tmp SHA256SUMS.txt
cat SHA256SUMS.txt
