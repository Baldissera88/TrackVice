#!/bin/sh
# Builds the non-UI code with every tests/*.cs file and runs the tests under Mono.
set -e
cd "$(dirname "$0")/.."
API=/usr/lib/mono/4.5-api
SRC=$(ls src/*.cs | grep -v -e '/Ui\.' -e '/Program\.cs$' -e '/LogViceLink\.cs$' | sort)
mcs -langversion:5 -target:exe -warn:4 -warnaserror+ -noconfig -nostdlib \
  -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Xml.dll -r:$API/System.Runtime.Serialization.dll \
  -r:$API/System.Management.dll -r:$API/System.IO.Compression.dll -r:$API/System.Security.dll \
  -out:tests/TrackViceTests.exe $SRC tests/*.cs
mono tests/TrackViceTests.exe "$@"
