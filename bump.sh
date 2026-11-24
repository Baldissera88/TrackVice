#!/bin/sh
# Sets a new TrackVice version: TrackVice.wxs (with a new product code), src/AssemblyInfo.cs, tests/Stubs.cs and the README badge,
# and adds an empty dated section for it at the top of CHANGELOG.md.
# Usage: ./bump.sh <major.minor.patch>
set -e
cd "$(dirname "$0")"

new="$1"
if ! printf '%s\n' "$new" | grep -Eq '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'; then
  echo "Usage: $0 <major.minor.patch>, for example $0 1.2.0" >&2
  exit 2
fi

current=$(sed -n 's/^ *Version="\([^"]*\)".*/\1/p' TrackVice.wxs | head -n 1)
if [ -z "$current" ]; then
  echo "No Version was found in TrackVice.wxs." >&2
  exit 1
fi

# The highest of the two versions comes last when sorted by number, part by part.
highest=$(printf '%s\n%s\n' "$current" "$new" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
if [ "$new" = "$current" ] || [ "$highest" != "$new" ]; then
  echo "$new is not higher than the current version $current." >&2
  exit 1
fi
if grep -q "^## TrackVice $new\$" CHANGELOG.md; then
  echo "CHANGELOG.md already has a section for $new." >&2
  exit 1
fi

if [ -r /proc/sys/kernel/random/uuid ]; then
  guid=$(cat /proc/sys/kernel/random/uuid)
else
  guid=$(uuidgen)
fi
guid=$(printf '%s' "$guid" | tr 'a-f' 'A-F')

sed -i \
  -e "s/<Product Id=\"[^\"]*\"/<Product Id=\"$guid\"/" \
  -e "s/^\( *\)Version=\"[^\"]*\"/\1Version=\"$new\"/" \
  -e "s/DefaultVersion=\"[^\"]*\"/DefaultVersion=\"$new.0\"/" \
  TrackVice.wxs
sed -i \
  -e "s/AssemblyVersion(\"[^\"]*\")/AssemblyVersion(\"$new.0\")/" \
  -e "s/AssemblyFileVersion(\"[^\"]*\")/AssemblyFileVersion(\"$new.0\")/" \
  -e "s/AssemblyInformationalVersion(\"[^\"]*\")/AssemblyInformationalVersion(\"$new\")/" \
  src/AssemblyInfo.cs
sed -i -e "s/\(string Version { get { return \"\)[^\"]*\"/\1$new\"/" tests/Stubs.cs
# The version badge at the top of README.md.
sed -i \
  -e "s/alt=\"Version [0-9.]*\"/alt=\"Version $new\"/" \
  -e "s#badge/version-[0-9.]*-#badge/version-$new-#" \
  README.md

date=$(LC_ALL=C date +'%-d %B %Y')
awk -v v="$new" -v d="$date" '
  !done && /^## / { print "## TrackVice " v; print ""; print d "."; print ""; done = 1 }
  { print }
' CHANGELOG.md > CHANGELOG.md.new
mv CHANGELOG.md.new CHANGELOG.md

for f in TrackVice.wxs src/AssemblyInfo.cs tests/Stubs.cs README.md; do
  if ! grep -q "$new" "$f"; then
    echo "$f was not changed. Check it by hand." >&2
    exit 1
  fi
done

echo "TrackVice $current -> $new, product code {$guid}"
echo "Changed TrackVice.wxs, src/AssemblyInfo.cs, tests/Stubs.cs, the README badge and CHANGELOG.md."
echo "Write the changes in CHANGELOG.md, then build, test and commit."
