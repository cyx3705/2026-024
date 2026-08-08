[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$ApplicationDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $moduleRoot
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $projectRoot 'z-Package-HistoryMinerva'
}
$PackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
$ApplicationDataRoot = [System.IO.Path]::GetFullPath($ApplicationDataRoot)
$appShellRoot = Join-Path $ApplicationDataRoot 'AppShell'
$frontSlot = Join-Path $appShellRoot 'Modules\HistoryMinerva'
$serviceSlot = Join-Path $appShellRoot 'service\Modules\HistoryMinerva'
$appShellBackupRoot = Join-Path $appShellRoot 'module-backups'
$stagingRoot = Join-Path $appShellRoot 'module-staging'
# 4.2.0: Mapping and SWuse merged into HistoryMinerva. Retire every legacy slot so
# the host never loads both the old and the new identity at once.
$retiredSlots = @(
    (Join-Path $appShellRoot 'Modules\Mapping'),
    (Join-Path $appShellRoot 'service\Modules\Mapping'),
    (Join-Path $appShellRoot 'Modules\SWuse'),
    (Join-Path $appShellRoot 'service\Modules\SWuse'),
    (Join-Path $ApplicationDataRoot 'OneHistoryStudio\Modules\SE2SW'),
    (Join-Path $ApplicationDataRoot 'OneHistoryStudio\Modules\SWuse')
)
$legacyBackupRoot = Join-Path $ApplicationDataRoot 'OneHistoryStudio\module-backups'
$sumsPath = Join-Path $PackageRoot 'SHA256SUMS'

function Read-PackageHashes {
    param([string]$Path)

    $hashes = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64}) \*(?<name>[^\\/]+)$') {
            throw "Invalid SHA256SUMS line: $line"
        }
        $hashes[$Matches.name] = $Matches.hash.ToUpperInvariant()
    }
    return $hashes
}

function Test-SlotHashes {
    param(
        [string]$Directory,
        [System.Collections.IDictionary]$Hashes
    )

    foreach ($name in $Hashes.Keys) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Deployed slot is missing ${name}: $Directory"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ne $Hashes[$name]) {
            throw "Hash mismatch: $path Expected=$($Hashes[$name]) Actual=$actual"
        }
    }
}

if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container) -or
    -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
    throw "HistoryMinerva package is incomplete: $PackageRoot"
}
$hashes = Read-PackageHashes -Path $sumsPath
foreach ($required in @(
        'HistoryMinerva.dll', 'HistoryMinerva.xml', 'HistoryMinerva.Contracts.dll',
        'HistoryMinerva.Api.dll', 'HistoryMinerva.Worker.exe',
        'HistoryMinerva.Worker.dll', 'HistoryMinerva.Worker.deps.json', 'HistoryMinerva.Worker.runtimeconfig.json',
        'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll',
        'module.manifest.json', 'appshell.snapshot.json')) {
    if (-not $hashes.Contains($required)) {
        throw "HistoryMinerva package hash list is missing: $required"
    }
}
Test-SlotHashes -Directory $PackageRoot -Hashes $hashes
if (@(Get-ChildItem -LiteralPath $PackageRoot -Filter 'AppShell*.dll' -File).Count -ne 0) {
    throw 'HistoryMinerva package must not carry AppShell DLLs.'
}

Write-Host "Package: $PackageRoot"
Write-Host "Frontend slot: $frontSlot"
Write-Host "Service slot:  $serviceSlot"
Write-Host "Retired slots: $($retiredSlots -join '; ')"
if (-not $Apply) {
    Write-Host 'Preview only. Close AppShell and rerun with -Apply to deploy.'
    return
}

$running = @(Get-Process -Name 'AppShell', 'AppShell.ServiceHost' -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) {
    throw "AppShell must be closed before deployment. Running PID(s): $($running.Id -join ', ')"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$transactionRoot = Join-Path $stagingRoot "HistoryMinerva-$stamp"
$frontStage = Join-Path $transactionRoot 'frontend'
$serviceStage = Join-Path $transactionRoot 'service'
$null = New-Item -ItemType Directory -Path $frontStage -Force
$null = New-Item -ItemType Directory -Path $serviceStage -Force
foreach ($name in $hashes.Keys) {
    Copy-Item -LiteralPath (Join-Path $PackageRoot $name) -Destination (Join-Path $frontStage $name)
    Copy-Item -LiteralPath (Join-Path $PackageRoot $name) -Destination (Join-Path $serviceStage $name)
}
Copy-Item -LiteralPath $sumsPath -Destination (Join-Path $frontStage 'SHA256SUMS')
Copy-Item -LiteralPath $sumsPath -Destination (Join-Path $serviceStage 'SHA256SUMS')
Test-SlotHashes -Directory $frontStage -Hashes $hashes
Test-SlotHashes -Directory $serviceStage -Hashes $hashes

$null = New-Item -ItemType Directory -Path $appShellBackupRoot -Force
$frontBackup = Join-Path $appShellBackupRoot "HistoryMinerva-frontend-$stamp"
$serviceBackup = Join-Path $appShellBackupRoot "HistoryMinerva-service-$stamp"
$frontInstalled = $false
$serviceInstalled = $false
try {
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $frontSlot) -Force
    if (Test-Path -LiteralPath $frontSlot) {
        Move-Item -LiteralPath $frontSlot -Destination $frontBackup
    }
    Move-Item -LiteralPath $frontStage -Destination $frontSlot
    $frontInstalled = $true

    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $serviceSlot) -Force
    if (Test-Path -LiteralPath $serviceSlot) {
        Move-Item -LiteralPath $serviceSlot -Destination $serviceBackup
    }
    Move-Item -LiteralPath $serviceStage -Destination $serviceSlot
    $serviceInstalled = $true

    Test-SlotHashes -Directory $frontSlot -Hashes $hashes
    Test-SlotHashes -Directory $serviceSlot -Hashes $hashes
}
catch {
    if ($serviceInstalled -and (Test-Path -LiteralPath $serviceSlot)) {
        Remove-Item -LiteralPath $serviceSlot -Recurse -Force
    }
    if (Test-Path -LiteralPath $serviceBackup) {
        Move-Item -LiteralPath $serviceBackup -Destination $serviceSlot
    }
    if ($frontInstalled -and (Test-Path -LiteralPath $frontSlot)) {
        Remove-Item -LiteralPath $frontSlot -Recurse -Force
    }
    if (Test-Path -LiteralPath $frontBackup) {
        Move-Item -LiteralPath $frontBackup -Destination $frontSlot
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force
    }
}

foreach ($retired in $retiredSlots) {
    if (Test-Path -LiteralPath $retired) {
        $backupRoot = if ($retired.StartsWith($appShellRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $appShellBackupRoot
        } else {
            $legacyBackupRoot
        }
        $null = New-Item -ItemType Directory -Path $backupRoot -Force
        Move-Item -LiteralPath $retired -Destination (Join-Path $backupRoot "$(Split-Path -Leaf $retired)-retired-$stamp")
    }
}

Write-Host 'HistoryMinerva deployment completed and both AppShell slots match the formal package.'
Write-Host 'The legacy Mapping/SWuse/SE2SW slots were retired when present.'
