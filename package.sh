#!/bin/sh
set -e
cd "$(dirname "$0")"
./build.sh
wixl -a x64 -o TrackVice.msi TrackVice.wxs
./patch_msi.sh "$PWD/TrackVice.msi"
echo PACKAGE_OK
