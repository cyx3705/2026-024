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
$appShellPackageRoot = Join-Path $projectsRoot '2026-023-AppShell\z-Package-AppShell'
$appShellManifestPath = Join-Path $appShellPackageRoot 'manifest.json'
$appShellCorePath = Join-Path $appShellPackageRoot 'host\AppShell.Core.dll'
$moduleProject = Join-Path $moduleRoot 'src\HistoryMinerva\HistoryMinerva.csproj'
$moduleManifestPath = Join-Path $projectRoot 'z-HistoryMinerva\module.manifest.json'
$versionPropsPath = Join-Path $moduleRoot 'build\HistoryMinerva.Version.props'

foreach ($required in @($appShellManifestPath, $appShellCorePath, $moduleProject, $moduleManifestPath, $versionPropsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release input is missing: $required"
    }
}

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw -Encoding UTF8
$moduleVersion = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.HistoryMinervaVersion } | Where-Object { $_ })[0]
$expectedHostVersion = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.AppShellVersion } | Where-Object { $_ })[0]
$expectedManifestHash = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.AppShellManifestSha256 } | Where-Object { $_ })[0]
$expectedCoreHash = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.AppShellCoreSha256 } | Where-Object { $_ })[0]
if ($moduleVersion -notmatch '^\d+\.\d+\.\d+$' -or $expectedHostVersion -ne '3.1.9' -or
    $expectedManifestHash -notmatch '^[0-9A-F]{64}$' -or $expectedCoreHash -notmatch '^[0-9A-F]{64}$') {
    throw "Invalid HistoryMinerva/AppShell snapshot declaration: HistoryMinerva=$moduleVersion AppShell=$expectedHostVersion"
}

$hostManifest = Get-Content -LiteralPath $appShellManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($hostManifest.product -ne 'AppShell' -or $hostManifest.version -ne $expectedHostVersion) {
    throw "AppShell host snapshot must be ${expectedHostVersion}: $appShellManifestPath"
}
$manifestHash = (Get-FileHash -LiteralPath $appShellManifestPath -Algorithm SHA256).Hash
$coreHash = (Get-FileHash -LiteralPath $appShellCorePath -Algorithm SHA256).Hash
if ($manifestHash -ne $expectedManifestHash) {
    throw "AppShell manifest hash changed. Expected=$expectedManifestHash Actual=$manifestHash"
}
if ($coreHash -ne $expectedCoreHash) {
    throw "AppShell.Core hash changed. Expected=$expectedCoreHash Actual=$coreHash"
}
$coreVersion = (Get-Item -LiteralPath $appShellCorePath).VersionInfo.FileVersion
if ($coreVersion -ne '3.1.9.0') {
    throw "AppShell.Core file version must be 3.1.9.0: $coreVersion"
}

if (-not $SkipBuild) {
    & dotnet build $moduleProject -c $Configuration -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "Mapping $Configuration build failed with exit code $LASTEXITCODE"
    }
}

& (Join-Path $PSScriptRoot 'Update-HistoryMinervaManifest.ps1') -ManifestPath $moduleManifestPath
$moduleManifest = Get-Content -LiteralPath $moduleManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
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

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot 'z-Package-HistoryMinerva'
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
Copy-Item -LiteralPath $moduleManifestPath -Destination (Join-Path $OutputRoot 'module.manifest.json')

$snapshot = [ordered]@{
    schemaVersion = 1
    module = 'HistoryMinerva'
    moduleVersion = $moduleVersion
    appShellVersion = $expectedHostVersion
    appShellManifestSha256 = $manifestHash
    appShellCoreSha256 = $coreHash
    appShellSource = '../2026-023-AppShell/z-Package-AppShell'
}
$snapshotJson = $snapshot | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'appshell.snapshot.json'),
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

$privateHostDlls = @(Get-ChildItem -LiteralPath $OutputRoot -Filter 'AppShell*.dll' -File)
if ($privateHostDlls.Count -ne 0) {
    throw "HistoryMinerva package must not carry AppShell DLLs: $($privateHostDlls.Name -join ', ')"
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
Write-Host "AppShell $expectedHostVersion manifest SHA256: $manifestHash"
