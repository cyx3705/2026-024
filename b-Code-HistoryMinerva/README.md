# HistoryMinerva 生产模块工程

> 对外模块：`HistoryMinerva` / `minerva`
>
> 当前源码版本：`4.4.5`

中央停靠页只做四种 CAD 转换。历史 SWuse 建模 API、Roslyn 编译器与双参数构建协议已删除。

## 工程布局

```text
src/HistoryMinerva            前端：WPF 停靠页、模块入口、命令
src/HistoryMinerva.Contracts  转换协议
src/HistoryMinerva.Worker     单一 x64 STA 工作进程：只跑转换动词
build/                        唯一版本源（HistoryMinervaVersion）与 HistoryVulcan 3.11.1 最低兼容版本
eng/                          清单同步、正式包脚本
```

## 构建与离线验证

从项目根目录执行：

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
.\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
```

生产工程直接引用 `2026-023-HistoryVulcan/z-Publish/host/HistoryVulcan.Core.dll`，宿主 DLL 不随模块包复制。
只修改 `build/HistoryMinerva.Version.props` 后，通过 `eng/Update-HistoryMinervaManifest.ps1` 同步清单版本；正式包由
`eng/Build-HistoryMinervaPackage.ps1` 生成。

测试、UI Smoke 与三个真实 CAD 端到端门禁由独立的
[`b-Code-HistoryMinerva-Tests`](../b-Code-HistoryMinerva-Tests/README.md) 维护。
