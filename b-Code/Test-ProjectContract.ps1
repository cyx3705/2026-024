[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryMinerva 自身的只读项目合同检查。
# 旧入口曾转发 HistoryDiana 的 OneHistory.ModuleContract.ps1；该脚本属于已删除的
# Diana 发布器。跨项目合同由宿主 vulcan.dev.submit 内的 ProjectContract 执行。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$errors = [Collections.Generic.List[string]]::new()

function Require-File([string]$RelativePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $RelativePath) -PathType Leaf)) {
        $errors.Add("缺少必需文件: $RelativePath")
    }
}

function Require-Directory([string]$RelativePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $RelativePath) -PathType Container)) {
        $errors.Add("缺少必需目录: $RelativePath")
    }
}

try {
    $manifestPath = Join-Path $repoRoot 'project.manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "无法读取 project.manifest.json: $($_.Exception.Message)"
}

foreach ($field in @('id', 'name', 'title', 'status', 'version', 'branch')) {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.project.$field)) {
        $errors.Add("project.manifest.json 缺少 project.$field")
    }
}

Require-File 'AGENTS.md'
Require-File 'README.md'
Require-File 'project.manifest.json'
foreach ($directory in @($manifest.paths.activeRoots)) { Require-Directory $directory }
foreach ($document in @($manifest.documents.PSObject.Properties | ForEach-Object { $_.Value })) {
    if ($document -is [string] -and $document.Length -gt 0) {
        Require-File $document
    }
}

$versionProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryMinerva\build\HistoryMinerva.Version.props') -Raw -Encoding UTF8)
$moduleVersion = @($versionProps.Project.PropertyGroup | ForEach-Object {
    if ($_.PSObject.Properties.Name -contains 'HistoryMinervaVersion') {
        $_.PSObject.Properties['HistoryMinervaVersion'].Value
    }
} | Where-Object { $_ } | Select-Object -First 1)
$moduleManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryMinerva\module.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.project.version -ne $moduleVersion -or $moduleManifest.version -ne $moduleVersion) {
    $errors.Add('项目 manifest、版本 props 与模块 manifest 的版本必须一致')
}
if ($moduleManifest.name -ne 'HistoryMinerva' -or $moduleManifest.type -ne 'HistoryVulcan.Module') {
    $errors.Add('模块 manifest 身份无效')
}

if ($Instantiation) {
    $textFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.md', '.json', '.props', '.csproj') } |
        Where-Object { $_.FullName -notmatch '\\(?:bin|obj|z-Publish)\\' -and $_.Name -ne 'AGENTS.md' }
    foreach ($file in $textFiles) {
        if ((Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8) -match '\{\{.+?\}\}') {
            $errors.Add("实例化后仍有占位符: $($file.FullName)")
        }
    }
}

if ($errors.Count -gt 0) {
    foreach ($item in $errors) { Write-Host $item -ForegroundColor Red }
    exit 1
}

Write-Host 'Project contract passed.'
exit 0
