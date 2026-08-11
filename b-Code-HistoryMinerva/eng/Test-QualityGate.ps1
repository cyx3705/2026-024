[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourceRoot = Join-Path $root 'b-Code-HistoryMinerva'
$testRoot = Join-Path $root 'b-Code-HistoryMinerva-Tests'
$violations = [System.Collections.Generic.List[string]]::new()

function Add-Violation([string]$Message) {
    $null = $violations.Add($Message)
}

function Read-XmlProperty([xml]$Document, [string]$Name) {
    $values = @($Document.Project.PropertyGroup | ForEach-Object { $_.$Name } | Where-Object { $_ })
    if ($values.Count -eq 0) { return '' }
    return [string]$values[0]
}

function Assert-SameSet([string]$Label, [string[]]$Expected, [string[]]$Actual) {
    $expectedSorted = @($Expected | Sort-Object -Unique)
    $actualSorted = @($Actual | Sort-Object -Unique)
    if (($expectedSorted -join "`n") -cne ($actualSorted -join "`n")) {
        Add-Violation "$Label differs. Expected=[$($expectedSorted -join ', ')] Actual=[$($actualSorted -join ', ')]"
    }
}

# The shared OneHistory contract remains authoritative for repository structure.
$contractScript = Join-Path $root 'b-Code\Test-ProjectContract.ps1'
if (-not (Test-Path -LiteralPath $contractScript -PathType Leaf)) {
    Add-Violation 'b-Code/Test-ProjectContract.ps1 is missing'
}
else {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $contractScript -Instantiation
    if ($LASTEXITCODE -ne 0) {
        Add-Violation "Project contract failed with exit code $LASTEXITCODE"
    }
}

# Suppressions hide compiler/analyzer regressions. Only missing XML documentation is intentional.
$suppressionPattern = 'NoWarn|SuppressMessage|#pragma\s+warning\s+disable'
$docWarningWhitelist = @('CS1573', 'CS1591')
$excluded = '\\(bin|obj|isolated)\\'
foreach ($activeRoot in @($sourceRoot, $testRoot)) {
    $files = Get-ChildItem -LiteralPath $activeRoot -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.targets' -and $_.FullName -notmatch $excluded }
    foreach ($file in $files) {
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $lineNumber++
            if ($line -notmatch $suppressionPattern) { continue }
            $codes = @([regex]::Matches($line, '[A-Z]{2}\d{4}') | ForEach-Object Value)
            $effective = @($codes | Where-Object { $_ -notin $docWarningWhitelist })
            if ($line -match 'NoWarn' -and $effective.Count -eq 0) { continue }
            Add-Violation "Suppression token: $($file.FullName):$lineNumber"
        }
    }
}

# Production files over 1000 lines must be split. Test fixtures and CAD probes are excluded.
$hotspots = @(
    Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src') -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.xaml' -and $_.FullName -notmatch $excluded } |
        ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName; Lines = ([IO.File]::ReadAllLines($_.FullName)).Count }
        } |
        Where-Object Lines -gt 1000 |
        Sort-Object Lines -Descending
)
foreach ($hotspot in $hotspots) {
    Add-Violation "Production hotspot over 1000 lines: $($hotspot.Path) ($($hotspot.Lines) lines)"
}

$projectManifestPath = Join-Path $root 'project.manifest.json'
$agentsPath = Join-Path $root 'AGENTS.md'
$versionPropsPath = Join-Path $sourceRoot 'build\HistoryMinerva.Version.props'
$sourceManifestPath = Join-Path $sourceRoot 'module.manifest.json'
foreach ($required in @($projectManifestPath, $agentsPath, $versionPropsPath, $sourceManifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Add-Violation "Required project input is missing: $required"
    }
}

[xml]$versionProps = [IO.File]::ReadAllText($versionPropsPath)
$sourceVersion = Read-XmlProperty $versionProps 'HistoryMinervaVersion'
# 宿主兼容性按下限判定，不再钉精确版本或构建哈希——见 Build-HistoryMinervaPackage.ps1 的说明。
$requiredVulcan = Read-XmlProperty $versionProps 'MinimumHistoryVulcanVersion'
if ($sourceVersion -notmatch '^\d+\.\d+\.\d+$') { Add-Violation "Invalid HistoryMinervaVersion: $sourceVersion" }
if ($requiredVulcan -notmatch '^\d+\.\d+\.\d+$') { Add-Violation "Invalid MinimumHistoryVulcanVersion: $requiredVulcan" }

$projectManifest = [IO.File]::ReadAllText($projectManifestPath) | ConvertFrom-Json
if ([string]$projectManifest.project.id -ne '2026-024' -or [string]$projectManifest.project.name -ne 'HistoryMinerva') {
    Add-Violation 'project.manifest.json identity must be 2026-024/HistoryMinerva'
}
if ([string]$projectManifest.project.version -ne $sourceVersion) {
    Add-Violation "project.manifest.json version $($projectManifest.project.version) != $sourceVersion"
}
if ([string]$projectManifest.project.branch -ne '2026-024-HistoryMinerva') {
    Add-Violation 'project.manifest.json branch must be 2026-024-HistoryMinerva'
}
if ([string]$projectManifest.historyVulcanHost.version -ne $requiredVulcan -or
    [string]$projectManifest.historyVulcanHost.manifestSha256 -ne $requiredManifestHash -or
    [string]$projectManifest.historyVulcanHost.coreSha256 -ne $requiredCoreHash) {
    Add-Violation 'project.manifest.json HistoryVulcan projection differs from HistoryMinerva.Version.props'
}
if ([string]$projectManifest.commands.verify -notmatch 'Test-QualityGate\.ps1') {
    Add-Violation 'project.manifest.json commands.verify must invoke Test-QualityGate.ps1'
}

$sourceManifest = [IO.File]::ReadAllText($sourceManifestPath) | ConvertFrom-Json
if ([string]$sourceManifest.name -ne 'HistoryMinerva' -or [string]$sourceManifest.version -ne $sourceVersion) {
    Add-Violation "Source module manifest identity/version differs from HistoryMinerva $sourceVersion"
}
if (-not [bool]$sourceManifest.ui -or [string]$sourceManifest.mcpExposure -ne 'readonly') {
    Add-Violation 'Source module manifest must declare ui=true and mcpExposure=readonly'
}

$technicalPath = Join-Path $root ([string]$projectManifest.documents.technicalContract)
$verificationPath = Join-Path $root ([string]$projectManifest.documents.verification)
$apiPath = Join-Path $root ([string]$projectManifest.documents.package)
$technicalText = [IO.File]::ReadAllText($technicalPath)
$verificationText = [IO.File]::ReadAllText($verificationPath)
$apiText = [IO.File]::ReadAllText($apiPath)
if ($technicalText -notmatch "(?m)^# HistoryMinerva $([regex]::Escape($sourceVersion)) .+$") {
    Add-Violation "Technical contract title does not project version $sourceVersion"
}
if ($verificationText -notmatch "(?m)^# HistoryMinerva $([regex]::Escape($sourceVersion)) .+$") {
    Add-Violation "Verification contract title does not project version $sourceVersion"
}
if ($apiText -notmatch "HistoryMinerva ``$([regex]::Escape($sourceVersion))``") {
    Add-Violation "Module API does not project source version $sourceVersion"
}
if ($apiText -notmatch "HistoryVulcan[^\r\n]*``$([regex]::Escape($requiredVulcan))``") {
    Add-Violation "Module API does not project HistoryVulcan $requiredVulcan"
}

# Command documentation is compared with both explicit registration sites.
$identitySource = [IO.File]::ReadAllText((Join-Path $sourceRoot 'src\HistoryMinerva.Contracts\HistoryMinervaIdentity.cs'))
$commandRootMatch = [regex]::Match($identitySource, 'CommandRoot\s*=\s*"(?<root>[a-z][a-z0-9]*)"')
if (-not $commandRootMatch.Success) {
    Add-Violation 'HistoryMinervaIdentity.CommandRoot is missing'
    $commandRoot = 'minerva'
}
else {
    $commandRoot = $commandRootMatch.Groups['root'].Value
}
$backendSource = [IO.File]::ReadAllText((Join-Path $sourceRoot 'src\HistoryMinerva\SWuseCommands.cs'))
$backendCommands = @(
    [regex]::Matches($backendSource, 'Command\("(?<method>[a-z][a-z0-9]*)"\)') |
        ForEach-Object { "$commandRoot.worker.$($_.Groups['method'].Value)" } |
        Sort-Object -Unique
)
$uiSource = [IO.File]::ReadAllText((Join-Path $sourceRoot 'src\HistoryMinerva\HistoryMinervaUiModule.cs'))
$uiCommands = @(
    [regex]::Matches($uiSource, 'CommandRoot\s*\+\s*"(?<suffix>\.conversion\.[a-z][a-z0-9]*)"') |
        ForEach-Object { $commandRoot + $_.Groups['suffix'].Value } |
        Sort-Object -Unique
)
$sourceCommands = @($backendCommands + $uiCommands | Sort-Object -Unique)
$apiCommands = @(
    [regex]::Matches($apiText, '(?m)^\|\s*`(?<name>minerva(?:\.[a-z0-9]+){2})`\s*\|') |
        ForEach-Object { $_.Groups['name'].Value } |
        Sort-Object -Unique
)
if ($backendCommands.Count -ne 5 -or $uiCommands.Count -ne 3) {
    Add-Violation "Expected 5 backend and 3 frontend commands; found $($backendCommands.Count) and $($uiCommands.Count)"
}
Assert-SameSet 'Module API command catalog' $sourceCommands $apiCommands
if (@([regex]::Matches($uiSource, 'RegisterToolWindow\(')).Count -ne 1 -or
    $uiSource -notmatch 'Id\s*=\s*HistoryMinervaIdentity\.WindowId' -or
    $uiSource -notmatch 'DefaultSide\s*=\s*DockSide\.Center') {
    Add-Violation 'HistoryMinerva UI must register exactly one identity-backed center window'
}

# The formal snapshot may lag the source version, but its own manifest, file set and hashes are immutable.
$formalRoot = Join-Path $root 'z-HistoryMinerva'
$formalManifestPath = Join-Path $formalRoot 'module.manifest.json'
$checksumPath = Join-Path $formalRoot 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $formalManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    Add-Violation 'z-HistoryMinerva formal manifest or SHA256SUMS is missing'
}
else {
    $formalManifest = [IO.File]::ReadAllText($formalManifestPath) | ConvertFrom-Json
    $expectedFormalFiles = @(
        [string]$formalManifest.artifact
        [string]$formalManifest.docs
        @($formalManifest.deps | ForEach-Object { [string]$_ })
        'module.manifest.json'
        'historyvulcan.snapshot.json'
        'SHA256SUMS'
    )
    $actualFormalFiles = @(Get-ChildItem -LiteralPath $formalRoot -File | ForEach-Object Name)
    Assert-SameSet 'z-HistoryMinerva file boundary' $expectedFormalFiles $actualFormalFiles

    $declaredHashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($checksumPath)) {
        $match = [regex]::Match($line, '^(?<hash>[A-Fa-f0-9]{64}) \*(?<file>.+)$')
        if (-not $match.Success) {
            Add-Violation "Invalid SHA256SUMS line: $line"
            continue
        }
        $declaredHashes[$match.Groups['file'].Value] = $match.Groups['hash'].Value.ToUpperInvariant()
    }
    $hashTargets = @($actualFormalFiles | Where-Object { $_ -ne 'SHA256SUMS' })
    Assert-SameSet 'z-HistoryMinerva SHA256SUMS coverage' $hashTargets @($declaredHashes.Keys)
    foreach ($file in $hashTargets) {
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $formalRoot $file) -Algorithm SHA256).Hash
        if ($declaredHashes.ContainsKey($file) -and $declaredHashes[$file] -ne $actualHash) {
            Add-Violation "z-HistoryMinerva hash mismatch: $file"
        }
    }
    $formalSnapshot = [IO.File]::ReadAllText((Join-Path $formalRoot 'historyvulcan.snapshot.json')) | ConvertFrom-Json
    if ([string]$formalSnapshot.moduleVersion -ne [string]$formalManifest.version -or
        [string]$formalSnapshot.historyVulcanVersion -notmatch '^\d+\.\d+\.\d+$') {
        Add-Violation 'z-HistoryMinerva snapshot metadata is invalid or differs from its own manifest'
    }
}

# Verify the exact sibling host snapshot, not only its assembly version.
$vulcanRoot = [IO.Path]::GetFullPath((Join-Path $root '..\2026-023-HistoryVulcan\z-HistoryVulcan'))
$vulcanManifestPath = Join-Path $vulcanRoot 'manifest.json'
$vulcanCorePath = Join-Path $vulcanRoot 'host\HistoryVulcan.Core.dll'
if (-not (Test-Path -LiteralPath $vulcanManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $vulcanCorePath -PathType Leaf)) {
    Add-Violation "HistoryVulcan formal snapshot is incomplete: $vulcanRoot"
}
else {
    $vulcanManifest = [IO.File]::ReadAllText($vulcanManifestPath) | ConvertFrom-Json
    if ([string]$vulcanManifest.product -ne 'HistoryVulcan' -or [string]$vulcanManifest.version -ne $requiredVulcan) {
        Add-Violation "HistoryVulcan identity/version mismatch: $($vulcanManifest.product) $($vulcanManifest.version)"
    }
    if ((Get-FileHash -LiteralPath $vulcanManifestPath -Algorithm SHA256).Hash -ne $requiredManifestHash) {
        Add-Violation 'HistoryVulcan manifest SHA256 differs from the pinned value'
    }
    if ((Get-FileHash -LiteralPath $vulcanCorePath -Algorithm SHA256).Hash -ne $requiredCoreHash) {
        Add-Violation 'HistoryVulcan.Core SHA256 differs from the pinned value'
    }
    $coreIdentity = [Reflection.AssemblyName]::GetAssemblyName($vulcanCorePath)
    if ($coreIdentity.Version.ToString() -ne "$requiredVulcan.0") {
        Add-Violation "HistoryVulcan.Core identity $($coreIdentity.Version) != $requiredVulcan.0"
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "QUALITY GATE: $violation" -ForegroundColor Red
    }
    exit 1
}

Write-Host ("Quality gate passed: contract; suppressions 0; production hotspots {0}; version {1}; commands {2}; formal snapshot SHA; HistoryVulcan {3} pinned." -f $hotspots.Count, $sourceVersion, $sourceCommands.Count, $requiredVulcan)
