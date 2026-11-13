#!/bin/sh
# Builds TrackVice.exe with Mono. Every src/*.cs file is compiled.
set -e
cd "$(dirname "$0")"
API=/usr/lib/mono/4.5-api
SRC=$(ls src/*.cs | sort)
mcs -langversion:5 -target:winexe -platform:anycpu -optimize+ -warn:4 -warnaserror+ -noconfig -nostdlib \
  -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Xml.dll -r:$API/System.Runtime.Serialization.dll \
  -r:$API/System.Management.dll -r:$API/System.Drawing.dll -r:$API/System.Windows.Forms.dll -r:$API/System.IO.Compression.dll -r:$API/System.Security.dll \
  -win32icon:TrackVice.ico -resource:TrackVice.ico,TrackVice.ico -out:TrackVice.exe $SRC
echo EXE_OK
