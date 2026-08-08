[CmdletBinding()]
param(
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path (Split-Path -Parent $moduleRoot) 'z-SWuse\module.manifest.json'
}
$versionPropsPath = Join-Path $moduleRoot 'build\SWuse.Version.props'
if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
    throw "未找到唯一版本源：$versionPropsPath"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "未找到模块清单：$ManifestPath"
}

[xml]$versionProps = [System.IO.File]::ReadAllText(
    (Resolve-Path -LiteralPath $versionPropsPath),
    [System.Text.UTF8Encoding]::new($false))
$version = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.SWuseVersion } | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本源无效：$versionPropsPath"
}

$manifest = [System.IO.File]::ReadAllText(
    (Resolve-Path -LiteralPath $ManifestPath),
    [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($manifest.name -ne 'SWuse') {
    throw "清单模块名不是 SWuse：$ManifestPath"
}
$manifest.version = $version
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    (Resolve-Path -LiteralPath $ManifestPath),
    $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "已由 $versionPropsPath 同步清单版本：$version"
