<#
.SYNOPSIS
  Returns the CallDock version and its release notes, after checking that the version is the same everywhere.
.DESCRIPTION
  The version lives in Directory.Build.props. The Chrome extension's manifest must carry the same number (Chrome shows
  it, and the extension compares it with the app's), and CHANGELOG.md must have a section for it: that section becomes
  the text of the GitHub release and of the "what's new" window of an update.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
$version = $props.SelectSingleNode('//Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props: the version must look like 1.2.3, not '$version'." }

$manifest = Get-Content -LiteralPath (Join-Path $repo 'extension/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne $version) { throw "extension/manifest.json has version $($manifest.version), Directory.Build.props has $version." }

$changelog = [string[]](Get-Content -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Encoding UTF8)
$start = [Array]::FindIndex($changelog, [Predicate[string]] { param($line) $line -match "^## \[$([regex]::Escape($version))\]" })
if ($start -lt 0) { throw "CHANGELOG.md has no section '## [$version]'." }
$end = [Array]::FindIndex($changelog, $start + 1, [Predicate[string]] { param($line) $line -match '^## ' })
if ($end -lt 0) { $end = $changelog.Length }
$notes = if ($end -gt $start + 1) { ($changelog[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $notes) { throw "CHANGELOG.md: the section for $version is empty." }

[pscustomobject]@{ Version = $version; Notes = $notes }
