#!/bin/sh
# Writes the winget manifests for one TrackVice release.
# Usage: winget/manifests.sh <version> <TrackVice.msi> <release notes file> <output folder>
set -e

version="$1"
msi="$2"
notes="$3"
out="$4"
if [ -z "$version" ] || [ ! -f "$msi" ] || [ -z "$out" ]; then
  echo "Usage: $0 <version> <TrackVice.msi> <release notes file> <output folder>" >&2
  exit 2
fi

id=DanielBaldissera.TrackVice
repo=Baldissera88/TrackVice
schema=1.12.0
url="https://github.com/$repo/releases/download/v$version/TrackVice.msi"
code=$(msiinfo export "$msi" Property | awk -F'\t' '$1=="ProductCode"{print $2}' | tr -d '\r')
upgrade=$(msiinfo export "$msi" Property | awk -F'\t' '$1=="UpgradeCode"{print $2}' | tr -d '\r')
sha=$(sha256sum "$msi" | cut -d' ' -f1 | tr 'a-f' 'A-F')
date=$(date -u +%Y-%m-%d)
dir="$out/manifests/d/DanielBaldissera/TrackVice/$version"
mkdir -p "$dir"

cat > "$dir/$id.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
EOF

cat > "$dir/$id.installer.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $version
MinimumOSVersion: 10.0.14393.0
InstallerType: msi
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ProductCode: '$code'
ReleaseDate: $date
AppsAndFeaturesEntries:
- DisplayName: TrackVice
  Publisher: Daniel L. Baldissera
  ProductCode: '$code'
  UpgradeCode: '$upgrade'
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
EOF

{
  cat <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: Daniel L. Baldissera
PublisherUrl: https://github.com/Baldissera88
PublisherSupportUrl: https://github.com/$repo/issues
Author: Daniel L. Baldissera
PackageName: TrackVice
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 Daniel L. Baldissera
ShortDescription: Follows SCCM task sequences, SCCM apps and updates, Windows Autopilot and Intune apps on one device as they install.
Description: |-
  TrackVice shows one deployment on one Windows device as a live timeline: the phases, every step, app, update and policy in them, and what each one is doing now.
  It reads smsts.log, the ConfigMgr client logs and WMI, the Enrollment Status Page tracking keys, the Intune Management Extension logs and the Autopilot and MDM event logs, and keeps the log line behind every step.
  It finds failed installs, apps that were not detected after install, retry loops, restarts, ESP time limits and installs with no progress. A companion to LogVice and PolicyVice.
Moniker: trackvice
Tags:
- autopilot
- configmgr
- endpoint-management
- esp
- intune
- mecm
- osd
- sccm
- task-sequence
- troubleshooting
EOF
  if [ -n "$notes" ] && [ -s "$notes" ] && grep -q '[^[:space:]]' "$notes"; then
    echo "ReleaseNotes: |-"
    tr -d '\r' < "$notes" | awk 'NF{found=1} found' | sed -e 's/[[:space:]]*$//' -e 's/^/  /' -e 's/^  $//'
  fi
  cat <<EOF
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$version
Documentations:
- DocumentLabel: Changelog
  DocumentUrl: https://github.com/$repo/blob/main/CHANGELOG.md
ManifestType: defaultLocale
ManifestVersion: $schema
EOF
} > "$dir/$id.locale.en-US.yaml"

echo "Wrote $dir"
