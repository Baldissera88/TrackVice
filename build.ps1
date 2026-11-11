<#
.SYNOPSIS
    Builds TrackVice on Windows: TrackVice.exe and, with -Msi, TrackVice.msi.

.DESCRIPTION
    Uses the C# compiler that ships with .NET Framework 4.x, so no SDK or Visual Studio is needed.
    The MSI needs WiX Toolset 3 (candle.exe and light.exe) on the PATH or in the default install folder.

.PARAMETER Msi
    Also build TrackVice.msi with WiX Toolset 3.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Msi
#>
[CmdletBinding()]
param(
    [switch]$Msi
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$sources = @(Get-ChildItem -Path src -Filter *.cs | Sort-Object -Property Name | ForEach-Object { 'src\' + $_.Name })

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -Path $csc)) {
    throw 'csc.exe from .NET Framework 4.x was not found.'
}

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/langversion:5',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Management.dll', '/reference:System.IO.Compression.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/win32icon:TrackVice.ico', '/resource:TrackVice.ico,TrackVice.ico', '/out:TrackVice.exe'
) + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE."
}
Write-Output 'Built TrackVice.exe'

if ($Msi) {
    $wixBin = $null
    $candle = Get-Command -Name candle.exe -ErrorAction SilentlyContinue
    if ($candle) {
        $wixBin = Split-Path -Path $candle.Source
    }
    else {
        $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\WiX Toolset v3*\bin\candle.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $wixBin = $found.DirectoryName
        }
    }
    if (-not $wixBin) {
        throw 'WiX Toolset 3 was not found. Install it, or build without -Msi.'
    }
    & (Join-Path $wixBin 'candle.exe') -nologo -arch x64 -out TrackVice.wixobj TrackVice.wxs
    if ($LASTEXITCODE -ne 0) {
        throw "candle.exe failed with exit code $LASTEXITCODE."
    }
    & (Join-Path $wixBin 'light.exe') -nologo -out TrackVice.msi TrackVice.wixobj
    if ($LASTEXITCODE -ne 0) {
        throw "light.exe failed with exit code $LASTEXITCODE."
    }
    Remove-Item -Path TrackVice.wixobj, TrackVice.wixpdb -ErrorAction SilentlyContinue
    Write-Output 'Built TrackVice.msi'
}
