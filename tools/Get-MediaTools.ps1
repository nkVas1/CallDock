<#
.SYNOPSIS
  Downloads the FFmpeg build CallDock ships with and returns the folder with ffmpeg.exe.
.DESCRIPTION
  An LGPL shared build of FFmpeg 8.1.3 from BtbN/FFmpeg-Builds, pinned by SHA-256. BtbN removes old autobuilds after a
  while, so the same archive is kept as an asset of this repository's "deps-ffmpeg-n8.1.3" release and is tried first.
  The archive is cached in tools/vendor and is never committed.
#>
[CmdletBinding()]
param([string]$Destination)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults, so the default is set here.
if (-not $Destination) { $Destination = Join-Path $PSScriptRoot 'vendor' }

$name = 'ffmpeg-n8.1.3-6-gff48edd8b2-win64-lgpl-shared-8.1.zip'
$expected = '1c9af2356443fec537fe1a64a5b33cb4c54fa212ad6590464423b3437e1aaa44'
$sources = @(
    "https://github.com/nkVas1/CallDock/releases/download/deps-ffmpeg-n8.1.3/$name",
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-29-13-10/$name"
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$archive = Join-Path $Destination 'ffmpeg.zip'
if ((Test-Path -LiteralPath $archive) -and (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    Remove-Item -LiteralPath $archive
}
if (-not (Test-Path -LiteralPath $archive)) {
    foreach ($url in $sources) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $expected) { break }
            Write-Warning "SHA-256 mismatch for $url"
        }
        catch { Write-Warning "Not available: $url ($($_.Exception.Message))" }
        Remove-Item -LiteralPath $archive -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $archive)) { throw 'FFmpeg could not be downloaded from any source.' }
}

$unpacked = Join-Path $Destination 'ffmpeg'
if (-not (Test-Path -LiteralPath $unpacked)) { Expand-Archive -LiteralPath $archive -DestinationPath $unpacked }
$bin = Get-ChildItem -LiteralPath $unpacked -Filter ffmpeg.exe -File -Recurse | Select-Object -First 1
if (-not $bin) { throw 'ffmpeg.exe is missing from the archive.' }
Write-Output $bin.Directory.FullName
