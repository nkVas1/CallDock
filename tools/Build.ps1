<#
.SYNOPSIS
  Builds a CallDock release for 64-bit Windows.
.DESCRIPTION
  1. Checks the version and its changelog section (tools/Get-Version.ps1).
  2. Runs the tests.
  3. Publishes the app and the speech worker, both self-contained, into one folder.
  4. Adds FFmpeg (LGPL build pinned by SHA-256) and the Microsoft C++ runtime, so nothing has to be installed separately.
  5. Packs the folder with Velopack: Setup.exe, a portable ZIP and the packages the app updates itself from.
.EXAMPLE
  ./tools/Build.ps1              # everything; the result is in artifacts/releases
.EXAMPLE
  ./tools/Build.ps1 -NoPack      # only the ready-to-run folder artifacts/publish
#>
[CmdletBinding()]
param(
    [string]$Artifacts,
    [switch]$SkipTests,
    [switch]$NoPack,
    # A local build on a computer without Visual Studio. Never for a release: Whisper and screen capture need this runtime.
    [switch]$SkipRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Artifacts) { $Artifacts = Join-Path $repo 'artifacts' }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)

function Invoke-Step([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# --- The version and its release notes ------------------------------------------------------------------------------
$release = & (Join-Path $PSScriptRoot 'Get-Version.ps1')
$version = $release.Version
$notes = $release.Notes
Write-Host "CallDock $version" -ForegroundColor Green

Push-Location $repo
try {
    if (-not $SkipTests) {
        Invoke-Step 'Tests' { dotnet test CallDock.slnx -c Release }
    }

    # --- One folder: the app, the speech worker, FFmpeg, the C++ runtime ---------------------------------------------
    $publish = Join-Path $Artifacts 'publish'
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    Invoke-Step 'Publishing the app' {
        dotnet publish src/CallDock.App/CallDock.App.csproj -c Release -r win-x64 --self-contained -o $publish
    }
    Invoke-Step 'Publishing the speech worker' {
        dotnet publish src/CallDock.Worker/CallDock.Worker.csproj -c Release -r win-x64 --self-contained -o $publish
    }

    # Whisper.net copies its native libraries for every platform; this build runs on 64-bit Windows only.
    foreach ($folder in @((Join-Path $publish 'runtimes'), (Join-Path $publish 'runtimes/noavx'))) {
        Get-ChildItem -LiteralPath $folder -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notin @('win-x64', 'noavx') } | Remove-Item -Recurse -Force
    }

    Write-Host '==> FFmpeg' -ForegroundColor Cyan
    $media = & (Join-Path $PSScriptRoot 'Get-MediaTools.ps1')
    $tools = Join-Path $publish 'tools'
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    Get-ChildItem -LiteralPath $media -File |
        Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('ffmpeg.exe', 'ffprobe.exe') } |
        Copy-Item -Destination $tools
    Copy-Item -LiteralPath (Join-Path (Split-Path $media -Parent) 'LICENSE.txt') -Destination (Join-Path $tools 'FFmpeg-LICENSE.txt')

    if ($SkipRuntime) {
        Write-Warning 'The Microsoft C++ runtime is not included: this build only runs where it is installed.'
    }
    else {
        Write-Host '==> Microsoft C++ runtime' -ForegroundColor Cyan
        # App-local runtime, as Microsoft allows for the files of the Visual Studio Redist folder: no administrator
        # rights and no separate installation, for the installer and the portable ZIP alike.
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with the C++ x64 toolset is required (or pass -SkipRuntime for a local build).' }
        $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not $vs) { throw 'Visual Studio has no C++ x64 toolset installed.' }
        $crt = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Directory -Recurse -Filter 'Microsoft.VC*.CRT' |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]' -and $_.FullName -notmatch 'onecore|debug' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $crt) { throw 'The x64 C++ runtime was not found in the Visual Studio Redist folder.' }
        Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $publish
    }

    foreach ($required in @('CallDock.exe', 'CallDock.Worker.exe', 'runtimes/win-x64', 'runtimes/noavx/win-x64', 'tools/ffmpeg.exe', 'tools/ffprobe.exe',
            'extension/manifest.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "The build is missing $required." }
    }
    if ($NoPack) { Write-Output $publish; return }

    # --- Velopack: Setup.exe, portable ZIP, update packages ----------------------------------------------------------
    [xml]$app = Get-Content -LiteralPath 'src/CallDock.App/CallDock.App.csproj'
    $velopack = $app.SelectSingleNode("//PackageReference[@Include='Velopack']").GetAttribute('Version')
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw "Velopack CLI is missing: dotnet tool install -g vpk --version $velopack" }
    $releases = Join-Path $Artifacts 'releases'
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    $notesFile = Join-Path $Artifacts "release-notes-$version.md"
    Set-Content -LiteralPath $notesFile -Value $notes -Encoding UTF8
    Invoke-Step 'Packing with Velopack' {
        vpk pack --packId CallDock --packVersion $version --runtime win-x64 --packDir $publish --mainExe CallDock.exe `
            --packTitle CallDock --packAuthors 'CallDock contributors' --icon src/CallDock.App/Assets/calldock.ico `
            --releaseNotes $notesFile --outputDir $releases
    }
    Get-ChildItem -LiteralPath $releases -File | Sort-Object Name | ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
    Write-Output $releases
}
finally { Pop-Location }
