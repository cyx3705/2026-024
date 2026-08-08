[CmdletBinding()]
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $moduleRoot
$publishRoot = Join-Path $projectRoot 'b-Publish'
$candidateRoot = Join-Path $publishRoot 'current\HistoryMinerva'
$historyRoot = Join-Path $publishRoot 'history\HistoryMinerva'
$workRoot = Join-Path $publishRoot 'work'
$formalRoot = Join-Path $projectRoot 'z-HistoryMinerva'
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')

function Assert-ModuleSnapshot {
    param([Parameter(Mandatory = $true)][string]$Root)

    $manifestPath = Join-Path $Root 'module.manifest.json'
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "HistoryMinerva snapshot is incomplete: $Root"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.type -ne 'HistoryVulcan.Module' -or
        $manifest.name -ne 'HistoryMinerva' -or $manifest.version -ne '4.2.1' -or
        $manifest.artifact -ne 'HistoryMinerva.dll' -or $manifest.docs -ne 'HistoryMinerva.xml' -or
        $manifest.ui -ne $true) {
        throw "HistoryMinerva manifest is invalid: $manifestPath"
    }
    foreach ($relative in @($manifest.artifact, $manifest.docs) + @($manifest.deps)) {
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('..') -or
            -not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) {
            throw "HistoryMinerva manifest has an invalid release-relative file: $relative"
        }
    }
    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath -Encoding UTF8) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64}) \*(?<file>[^\\/]+)$') {
            throw "Invalid SHA256SUMS line: $line"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    $actual = Get-ChildItem -LiteralPath $Root -File | Where-Object Name -ne 'SHA256SUMS'
    if ($hashes.Count -ne $actual.Count) { throw "SHA256SUMS does not cover the complete snapshot: $Root" }
    foreach ($file in $actual) {
        $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($file.Name) -or $hashes[$file.Name] -ne $actualHash) {
            throw "HistoryMinerva checksum mismatch: $($file.Name)"
        }
    }
    if (@(Get-ChildItem -LiteralPath $Root -Filter 'HistoryVulcan*.dll' -File).Count -ne 0) {
        throw "HistoryMinerva snapshot must not copy HistoryVulcan assemblies: $Root"
    }
}

function Invoke-AtomicPromotion {
    param([string]$Stage, [string]$Destination, [string]$Backup)

    $promoted = $false
    try {
        Assert-ModuleSnapshot $Stage
        if (Test-Path -LiteralPath $Destination) { Move-Item -LiteralPath $Destination -Destination $Backup }
        Move-Item -LiteralPath $Stage -Destination $Destination
        $promoted = $true
        Assert-ModuleSnapshot $Destination
    }
    catch {
        if ($promoted -and (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $Destination -Destination "$Destination.failed-$transactionId"
        }
        if (Test-Path -LiteralPath $Backup) { Move-Item -LiteralPath $Backup -Destination $Destination }
        throw
    }
}

$sourceStatus = (& git -C $projectRoot status --porcelain -- ':!b-Publish/**' ':!z-HistoryMinerva/**') -join "`n"
if ($Publish -and -not [string]::IsNullOrWhiteSpace($sourceStatus)) {
    throw "Formal publish requires committed source and documentation:`n$sourceStatus"
}

New-Item -ItemType Directory -Force -Path $publishRoot, (Split-Path -Parent $candidateRoot), $historyRoot, $workRoot | Out-Null
try {
    & (Join-Path $PSScriptRoot 'Build-HistoryMinervaPackage.ps1') -Configuration Release -OutputRoot $candidateRoot
    Assert-ModuleSnapshot $candidateRoot
    Write-Host "Candidate HistoryMinerva 4.2.1: $candidateRoot"

    if ($Publish) {
        $stage = Join-Path $workRoot "HistoryMinerva-formal-$transactionId"
        Copy-Item -LiteralPath $candidateRoot -Destination $stage -Recurse
        $installedVersion = 'none'
        if (Test-Path -LiteralPath (Join-Path $formalRoot 'module.manifest.json')) {
            $installedVersion = [string]((Get-Content -LiteralPath (Join-Path $formalRoot 'module.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version)
        }
        $backup = Join-Path $historyRoot "$installedVersion-$stamp"
        Invoke-AtomicPromotion $stage $formalRoot $backup
        Write-Host "Formal HistoryMinerva 4.2.1: $formalRoot"
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
