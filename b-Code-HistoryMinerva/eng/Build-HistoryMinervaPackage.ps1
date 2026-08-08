[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $moduleRoot
$projectsRoot = Split-Path -Parent $projectRoot
$historyVulcanPackageRoot = Join-Path $projectsRoot '2026-023-HistoryVulcan\z-HistoryVulcan'
$historyVulcanManifestPath = Join-Path $historyVulcanPackageRoot 'manifest.json'
$historyVulcanCorePath = Join-Path $historyVulcanPackageRoot 'host\HistoryVulcan.Core.dll'
$moduleProject = Join-Path $moduleRoot 'src\HistoryMinerva\HistoryMinerva.csproj'
$moduleManifestPath = Join-Path $moduleRoot 'module.manifest.json'
$versionPropsPath = Join-Path $moduleRoot 'build\HistoryMinerva.Version.props'

foreach ($required in @($historyVulcanManifestPath, $historyVulcanCorePath, $moduleProject, $moduleManifestPath, $versionPropsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release input is missing: $required"
    }
}

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw -Encoding UTF8
$moduleVersion = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.HistoryMinervaVersion } | Where-Object { $_ })[0]
 $expectedHostVersion = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.HistoryVulcanVersion } | Where-Object { $_ })[0]
if ($moduleVersion -notmatch '^\d+\.\d+\.\d+$' -or $expectedHostVersion -ne '3.2.2') {
    throw "Invalid HistoryMinerva/HistoryVulcan snapshot declaration: HistoryMinerva=$moduleVersion HistoryVulcan=$expectedHostVersion"
}

$hostManifest = Get-Content -LiteralPath $historyVulcanManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($hostManifest.product -ne 'HistoryVulcan' -or $hostManifest.version -ne $expectedHostVersion) {
    throw "HistoryVulcan host snapshot must be ${expectedHostVersion}: $historyVulcanManifestPath"
}
$coreVersion = (Get-Item -LiteralPath $historyVulcanCorePath).VersionInfo.FileVersion
if ($coreVersion -ne '3.2.2.0') {
    throw "HistoryVulcan.Core file version must be 3.2.2.0: $coreVersion"
}

if (-not $SkipBuild) {
    & dotnet build $moduleProject -c $Configuration -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "Mapping $Configuration build failed with exit code $LASTEXITCODE"
    }
}

& (Join-Path $PSScriptRoot 'Update-HistoryMinervaManifest.ps1') -ManifestPath $moduleManifestPath
$moduleManifestText = [System.IO.File]::ReadAllText($moduleManifestPath, [System.Text.Encoding]::UTF8)
$moduleManifest = $moduleManifestText | ConvertFrom-Json
if ($moduleManifest.name -ne 'HistoryMinerva' -or $moduleManifest.version -ne $moduleVersion) {
    throw "Module manifest does not match HistoryMinerva $moduleVersion"
}

$buildOutput = Join-Path $moduleRoot "src\HistoryMinerva\bin\$Configuration\net8.0-windows"
$runtimeFiles = @(
    'HistoryMinerva.dll',
    'HistoryMinerva.xml',
    'HistoryMinerva.Contracts.dll',
    'HistoryMinerva.Api.dll',
    'HistoryMinerva.Worker.exe',
    'HistoryMinerva.Worker.dll',
    'HistoryMinerva.Worker.deps.json',
    'HistoryMinerva.Worker.runtimeconfig.json',
    'Microsoft.CodeAnalysis.dll',
    'Microsoft.CodeAnalysis.CSharp.dll'
)
foreach ($file in $runtimeFiles) {
    $source = Join-Path $buildOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Release artifact is missing: $source"
    }
}

# Candidates are generated outside product sources. The publish script validates and
# atomically promotes this immutable candidate to the formal Z directory.
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot 'b-Publish\current\HistoryMinerva'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$projectPrefix = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($projectPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay inside the HistoryMinerva project: $OutputRoot"
}
if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $OutputRoot

foreach ($file in $runtimeFiles) {
    Copy-Item -LiteralPath (Join-Path $buildOutput $file) -Destination (Join-Path $OutputRoot $file)
}
# The source manifest is copied unchanged into the candidate release tree.
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'module.manifest.json'),
    $moduleManifestText,
    [System.Text.UTF8Encoding]::new($false))

$snapshot = [ordered]@{
    schemaVersion = 1
    module = 'HistoryMinerva'
    moduleVersion = $moduleVersion
    historyVulcanVersion = $expectedHostVersion
    historyVulcanSource = '../2026-023-HistoryVulcan/z-HistoryVulcan'
}
$snapshotJson = $snapshot | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'historyvulcan.snapshot.json'),
    $snapshotJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

# Git stores package JSON/XML with LF. Normalize before hashing so a clean checkout
# preserves exactly the bytes declared by SHA256SUMS.
foreach ($textFile in Get-ChildItem -LiteralPath $OutputRoot -File |
             Where-Object { $_.Extension -in @('.json', '.xml') }) {
    $text = [System.IO.File]::ReadAllText($textFile.FullName, [System.Text.UTF8Encoding]::new($false))
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    [System.IO.File]::WriteAllText($textFile.FullName, $text, [System.Text.UTF8Encoding]::new($false))
}

$privateHostDlls = @(Get-ChildItem -LiteralPath $OutputRoot -Filter 'HistoryVulcan*.dll' -File)
if ($privateHostDlls.Count -ne 0) {
    throw "HistoryMinerva package must not carry HistoryVulcan DLLs: $($privateHostDlls.Name -join ', ')"
}

$hashLines = Get-ChildItem -LiteralPath $OutputRoot -File |
    Where-Object { $_.Name -ne 'SHA256SUMS' } |
    Sort-Object Name |
    ForEach-Object { "{0} *{1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name }
[System.IO.File]::WriteAllLines(
    (Join-Path $OutputRoot 'SHA256SUMS'),
    $hashLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "HistoryMinerva $moduleVersion package created: $OutputRoot"
Write-Host "HistoryVulcan $expectedHostVersion formal snapshot verified."
