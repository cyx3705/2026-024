[CmdletBinding()]
param(
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $moduleRoot 'module.manifest.json'
}
$versionPropsPath = Join-Path $moduleRoot 'build\HistoryMinerva.Version.props'

if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
    throw "HistoryMinerva version source is missing: $versionPropsPath"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "HistoryMinerva module manifest is missing: $ManifestPath"
}

[xml]$versionProps = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $versionPropsPath), [Text.UTF8Encoding]::new($false))
$version = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.HistoryMinervaVersion } | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw "HistoryMinerva version source is invalid: $versionPropsPath"
}

$manifest = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ManifestPath), [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($manifest.name -ne 'HistoryMinerva' -or $manifest.type -ne 'HistoryVulcan.Module' -or $manifest.schemaVersion -ne 1) {
    throw "HistoryMinerva module manifest identity is invalid: $ManifestPath"
}

$manifest.version = $version
[IO.File]::WriteAllText((Resolve-Path -LiteralPath $ManifestPath), ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Updated HistoryMinerva manifest to version $version at $ManifestPath"
