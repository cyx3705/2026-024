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
    Join-Path $projectsRoot '2026-023-HistoryVulcan\z-Publish'
}
else {
    [IO.Path]::GetFullPath($HistoryVulcanPackageRoot)
}
$historyVulcanManifestPath = Join-Path $historyVulcanPackageRoot 'manifest.json'
$historyVulcanCorePath = Join-Path $historyVulcanPackageRoot 'host\HistoryVulcan.Core.dll'
$moduleProject = Join-Path $moduleRoot 'src\HistoryMinerva\HistoryMinerva.csproj'
$moduleManifestPath = Join-Path $moduleRoot 'module.manifest.json'
$packageDocuments = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'b-Office\package') -Filter '*.md' -File)
if ($packageDocuments.Count -eq 0) {
    throw 'b-Office/package must contain at least one Markdown document'
}
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
    & dotnet build $moduleProject -c $Configuration -p:NuGetAudit=false "-p:HistoryVulcanPackageRoot=$historyVulcanPackageRoot"
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
    'HistoryMinerva.Worker.exe',
    'HistoryMinerva.Worker.dll',
    'HistoryMinerva.Worker.deps.json',
    'HistoryMinerva.Worker.runtimeconfig.json'
)
# deps 只列宿主要装进 ALC 的托管程序集。Worker.exe / json 是进程载荷，
# 写进 deps 会被 Vulcan LoadFromStream 成「Bad IL format」，整个模块被跳过。
$manifestDeps = @($moduleManifest.deps | ForEach-Object { [string]$_ })
foreach ($dep in $manifestDeps) {
    if ($dep -notmatch '\.dll$') {
        throw "module.manifest.json deps must be managed assemblies, not payload files: $dep"
    }
    if ($dep -notin $runtimeFiles) {
        throw "module.manifest.json dep is not in the package contract: $dep"
    }
}
if ('HistoryMinerva.Contracts.dll' -notin $manifestDeps) {
    throw 'module.manifest.json deps must include HistoryMinerva.Contracts.dll'
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
    $OutputRoot = Join-Path $projectRoot 'z-Publish'
}
$candidateRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$projectPrefix = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not $candidateRoot.StartsWith($projectPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay inside the HistoryMinerva project: $candidateRoot"
}
$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) ('HistoryMinerva.Package.' + [Guid]::NewGuid().ToString('N'))
$OutputRoot = Join-Path $transactionRoot 'candidate'
$candidateBackup = Join-Path $transactionRoot 'previous'
$null = New-Item -ItemType Directory -Force -Path $OutputRoot, $candidateBackup

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
    historyVulcanSource = '../2026-023-HistoryVulcan/z-Publish'
}
$snapshotJson = $snapshot | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'historyvulcan.snapshot.json'),
    $snapshotJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$docsRoot = Join-Path $OutputRoot 'docs'
$null = New-Item -ItemType Directory -Force -Path $docsRoot
foreach ($document in $packageDocuments) {
    Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsRoot $document.Name)
}

# Git stores package JSON/XML with LF. Normalize before hashing so a clean checkout
# preserves exactly the bytes declared by SHA256SUMS.
foreach ($textFile in Get-ChildItem -LiteralPath $OutputRoot -File -Recurse |
             Where-Object { $_.Extension -in @('.json', '.xml') }) {
    $text = [System.IO.File]::ReadAllText($textFile.FullName, [System.Text.UTF8Encoding]::new($false))
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    [System.IO.File]::WriteAllText($textFile.FullName, $text, [System.Text.UTF8Encoding]::new($false))
}

$privateHostDlls = @(Get-ChildItem -LiteralPath $OutputRoot -Filter 'HistoryVulcan*.dll' -File)
if ($privateHostDlls.Count -ne 0) {
    throw "HistoryMinerva package must not carry HistoryVulcan DLLs: $($privateHostDlls.Name -join ', ')"
}

$outputPrefix = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\'
$hashLines = Get-ChildItem -LiteralPath $OutputRoot -File -Recurse |
    Where-Object { $_.Name -ne 'SHA256SUMS' } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($outputPrefix.Length).Replace('\', '/')
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
    }
[System.IO.File]::WriteAllLines(
    (Join-Path $OutputRoot 'SHA256SUMS'),
    $hashLines,
    [System.Text.UTF8Encoding]::new($false))

$expectedPackageFiles = @($runtimeFiles + @(
    'module.manifest.json',
    'historyvulcan.snapshot.json',
    'SHA256SUMS'
) + @($packageDocuments | ForEach-Object { "docs/$($_.Name)" }) | Sort-Object)
$actualPackageFiles = @(Get-ChildItem -LiteralPath $OutputRoot -File -Recurse | ForEach-Object {
    $_.FullName.Substring($outputPrefix.Length).Replace('\', '/')
} | Sort-Object)
if ((($expectedPackageFiles | Sort-Object) -join "`n") -cne (($actualPackageFiles | Sort-Object) -join "`n")) {
    throw "Candidate package file boundary mismatch. Expected=[$($expectedPackageFiles -join ', ')] Actual=[$($actualPackageFiles -join ', ')]"
}

$declaredHashes = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $OutputRoot 'SHA256SUMS'))) {
    $match = [regex]::Match($line, '^(?<hash>[A-Fa-f0-9]{64})  (?<file>.+)$')
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
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $file.Replace('/', '\')) -Algorithm SHA256).Hash
    if ($declaredHashes[$file] -ne $actualHash) {
        throw "Candidate SHA256 mismatch: $file"
    }
}

$movedPrevious = [Collections.Generic.List[string]]::new()
$movedCandidate = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $candidateRoot | Out-Null
try {
    foreach ($item in @(Get-ChildItem -LiteralPath $candidateRoot -Force |
            Where-Object { $_.Name -ne 'history' })) {
        Move-Item -LiteralPath $item.FullName -Destination $candidateBackup
        $movedPrevious.Add($item.Name)
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $OutputRoot -Force)) {
        Move-Item -LiteralPath $item.FullName -Destination $candidateRoot
        $movedCandidate.Add($item.Name)
    }
}
catch {
    foreach ($name in $movedCandidate) {
        $path = Join-Path $candidateRoot $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    foreach ($name in $movedPrevious) {
        $path = Join-Path $candidateBackup $name
        if (Test-Path -LiteralPath $path) {
            Move-Item -LiteralPath $path -Destination $candidateRoot
        }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "HistoryMinerva $moduleVersion package created: $candidateRoot"
Write-Host "HistoryVulcan $actualHostVersion candidate provenance recorded."
Write-Host "Candidate file boundary and $($hashTargets.Count) SHA256 entries verified."
