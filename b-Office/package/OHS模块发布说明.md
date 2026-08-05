# SE2SW OHS 模块发布说明

## 权威来源

- 版本：`b-Code-SE2SW/build/SE2SW.Version.props`
- 生产源码：`b-Code-SE2SW/src`
- 自动验证：`b-Code-SE2SW-Tests`
- 注册清单：`z-SE2SW/module.manifest.json`

## 发布前门禁

从项目根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
New-Item -ItemType Directory -Force .\artifacts | Out-Null
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.UiSmoke\SE2SW.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --capture .\artifacts\se2sw-ui.png
dotnet format .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj --verify-no-changes --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-SE2SW\eng\Update-SE2SWManifest.ps1 -ManifestPath .\z-SE2SW\module.manifest.json
```

清单必须保持 `name=SE2SW`、`ui=true`、`mcpExposure=hidden`，并声明 Release 目录中的主程序集、
XML 文档、Contracts 和 Worker 运行资产。

## 入槽与核验

发布需要显式执行 OHS `tool.scan` 和 `tool.sync name=SE2SW`。完成后用 `tool.list` 核对来源项目与版本，
用 `module.list` 核对 `se2sw` 已加载，再逐文件比较清单声明的 7 个产物与正式槽 SHA-256。

## 回退

正式同步前保留完整模块槽备份。回退时整体替换 `Modules/SE2SW`，不得混合覆盖不同版本文件；随后执行
`module.reload` 并重新核对版本、指令数和来源。远端推送、正式入槽和回退都需要用户明确授权。
