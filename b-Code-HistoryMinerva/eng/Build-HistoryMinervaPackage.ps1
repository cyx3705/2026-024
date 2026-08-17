[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [switch]$SkipBuild,
    # 宿主快照根。缺省按"本仓与 2026-023-HistoryVulcan 同库根"推导；从 AI 工作树运行时
    # 工作树在库根之外，该相对路径必然指空，由调用方显式传入或经环境变量继承。
    [string]$HistoryVulcanPackageRoot = $env:HISTORYVULCAN_PACKAGE_ROOT
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $moduleRoot
$projectsRoot = Split-Path -Parent $projectRoot
$historyVulcanPackageRoot = if ([string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    Join-Path $projectsRoot '2026-023-HistoryVulcan\z-HistoryVulcan'
}
else {
    [IO.Path]::GetFullPath($HistoryVulcanPackageRoot)
}
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
# 宿主兼容性按**下限**而非精确相等判定。
# 精确钉（含 SHA256）曾是 Core 会随每个消费方增长时的合理自保，但 3.9.0 起 Core 已冻结：
# 公开面只许降不许升，变更须满足冻结合同的三条判据并显式声明。模块该信的是那份合同，
# 不是一串哈希——而哈希更糟：实测宿主源码零改动原地重建，Core.dll 的 SHA256 就会变，
# 它钉死的其实是「我当时看到的那一次构建」，不是「某个版本」。
# 结果是宿主每发一版，N 个模块全部被迫改钉、重建、重发，功能上一行不需要动。
# 真正的兼容性由加载器 Smoke 验证——把模块装进 ALC 跑一遍才能发现「宿主删了我在用的 API」，
# 字节比对只能发现「宿主重新构建过」。
$minimumHostVersion = @($versionProps.Project.PropertyGroup | ForEach-Object { $_.MinimumHistoryVulcanVersion } | Where-Object { $_ })[0]
if ($moduleVersion -notmatch '^\d+\.\d+\.\d+$' -or $minimumHostVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid HistoryMinerva/HistoryVulcan declaration: HistoryMinerva=$moduleVersion MinimumHistoryVulcan=$minimumHostVersion"
}

$hostManifest = Get-Content -LiteralPath $historyVulcanManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($hostManifest.product -ne 'HistoryVulcan') {
    throw "Not a HistoryVulcan host snapshot: $historyVulcanManifestPath"
}
$actualHostVersion = [string]$hostManifest.version
if ([version]$actualHostVersion -lt [version]$minimumHostVersion) {
    throw "HistoryVulcan host snapshot $actualHostVersion is older than the required minimum $minimumHostVersion"
}
# 构建时看到的宿主版本记入快照作为溯源信息，但不参与门禁：它回答「我是对着哪一版验证的」，
# 不回答「我只能跑在哪一版上」。两者混为一谈正是耦合的来源。
Write-Host "HistoryVulcan host: $actualHostVersion (minimum $minimumHostVersion)"

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
$manifestRuntimeFiles = @([string]$moduleManifest.artifact, [string]$moduleManifest.docs) +
    @($moduleManifest.deps | ForEach-Object { [string]$_ })
if ((($runtimeFiles | Sort-Object) -join "`n") -cne (($manifestRuntimeFiles | Sort-Object) -join "`n")) {
    throw "Module manifest runtime file set differs from the package contract."
}
foreach ($file in $runtimeFiles) {
    $source = Join-Path $buildOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Release artifact is missing: $source"
    }
}

# Candidates are generated outside product sources. The publish script validates and
# atomically promotes this immutable candidate to the formal Z directory.
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot 'z-Publish\current\HistoryMinerva'
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
    historyVulcanVersion = $actualHostVersion
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

$expectedPackageFiles = @($runtimeFiles + @(
    'module.manifest.json',
    'historyvulcan.snapshot.json',
    'SHA256SUMS'
) | Sort-Object)
$actualPackageFiles = @(Get-ChildItem -LiteralPath $OutputRoot -File | ForEach-Object Name | Sort-Object)
if ((($expectedPackageFiles | Sort-Object) -join "`n") -cne (($actualPackageFiles | Sort-Object) -join "`n")) {
    throw "Candidate package file boundary mismatch. Expected=[$($expectedPackageFiles -join ', ')] Actual=[$($actualPackageFiles -join ', ')]"
}

$declaredHashes = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $OutputRoot 'SHA256SUMS'))) {
    $match = [regex]::Match($line, '^(?<hash>[A-Fa-f0-9]{64}) \*(?<file>.+)$')
    if (-not $match.Success) {
        throw "Invalid SHA256SUMS line: $line"
    }
    $declaredHashes[$match.Groups['file'].Value] = $match.Groups['hash'].Value.ToUpperInvariant()
}
$hashTargets = @($actualPackageFiles | Where-Object { $_ -ne 'SHA256SUMS' })
if ((($hashTargets | Sort-Object) -join "`n") -cne ((@($declaredHashes.Keys) | Sort-Object) -join "`n")) {
    throw "Candidate SHA256SUMS coverage differs from the package file set."
}
foreach ($file in $hashTargets) {
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $file) -Algorithm SHA256).Hash
    if ($declaredHashes[$file] -ne $actualHash) {
        throw "Candidate SHA256 mismatch: $file"
    }
}

Write-Host "HistoryMinerva $moduleVersion package created: $OutputRoot"
Write-Host "HistoryVulcan $actualHostVersion candidate provenance recorded."
Write-Host "Candidate file boundary and $($hashTargets.Count) SHA256 entries verified."
