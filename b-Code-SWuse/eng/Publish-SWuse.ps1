[CmdletBinding()]
param(
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $moduleRoot
$uiProject = Join-Path $moduleRoot 'src\SWuse\SWuse.csproj'
$smokeProject = Join-Path $moduleRoot 'tests\SWuse.Smoke\SWuse.Smoke.csproj'
$manifestPath = Join-Path $projectRoot 'z-SWuse\module.manifest.json'

& dotnet build $uiProject -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw "SWuse Release 构建失败，退出码：$LASTEXITCODE"
}

& (Join-Path $PSScriptRoot 'Update-SWuseManifest.ps1') -ManifestPath $manifestPath

if (-not $SkipSmoke) {
    & dotnet run --project $smokeProject -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "SWuse Smoke 失败，退出码：$LASTEXITCODE"
    }
}

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.name -ne 'SWuse') {
    throw "SWuse 清单名称错误：$($manifest.name)"
}
$artifacts = @($manifest.artifact, $manifest.docs) + @($manifest.deps)
foreach ($artifact in $artifacts) {
    $path = Join-Path $projectRoot $artifact
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "清单声明的发布产物不存在：$artifact"
    }
}

$versionProps = Join-Path $moduleRoot 'build\SWuse.Version.props'
[xml]$versionDocument = Get-Content -Raw -Encoding UTF8 -LiteralPath $versionProps
$version = @($versionDocument.Project.PropertyGroup | ForEach-Object { $_.SWuseVersion } | Where-Object { $_ })[0]
if ($manifest.version -ne $version) {
    throw "清单版本与唯一版本源不一致：manifest=$($manifest.version), props=$version"
}

Write-Host "SWuse pre-publish gate passed: version=$version, artifacts=$($artifacts.Count)."
Write-Host 'Next step requires explicit deployment approval: tool.scan; tool.sync name=SWuse; module.list'
