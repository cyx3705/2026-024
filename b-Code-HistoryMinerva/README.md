# HistoryMinerva 生产模块工程

> 对外模块：`HistoryMinerva` / `historyminerva`
>
> 当前源码、候选与正式 `z-HistoryMinerva` 快照版本：`4.3.2`
>
> 最新正式清单：`../z-HistoryMinerva/module.manifest.json`

4.2.1 破坏性重构：Mapping 与 SWuse 合并为单一模块，纯前后端分离。
内部程序集、Worker 路由协议与命名空间统一为 `HistoryMinerva.*` / `SWuse.*`，对外显示、窗口、
指令与 MCP 均称为 HistoryMinerva。SWuse 独立窗口已移除（待打磨），其 C# 建模能力保留在
合并 Worker 的协议层。

## 工程布局

```text
src/HistoryMinerva            前端：WPF 停靠页（原 Mapping 页）、模块入口、命令
src/HistoryMinerva.Contracts  后端协议：HistoryMinerva 转换协议 + SWuse 构建协议
src/HistoryMinerva.Api        用户建模 API（原 SWuse.Api，供 Worker 编译的用户代码引用）
src/HistoryMinerva.Worker     单一 x64 STA 工作进程：入口按参数形态路由 HistoryMinerva/SWuse 两条协议
build/                        唯一版本源（HistoryMinervaVersion）与 HistoryVulcan 3.9.0 最低兼容版本
eng/                          清单同步、正式包和Z 快照部署脚本
```

## 构建与离线验证

从项目根目录执行：

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
.\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
```

生产工程直接引用 `2026-023-HistoryVulcan/z-HistoryVulcan/host/HistoryVulcan.Core.dll` 3.4.0，宿主 DLL 不随模块包复制。
只修改 `build/HistoryMinerva.Version.props` 后，通过 `eng/Update-HistoryMinervaManifest.ps1` 同步清单版本；正式包由
`eng/Build-HistoryMinervaPackage.ps1` 生成；正式提升和 Diana 消费文档镜像由
`2026-019-HistoryDiana/b-Code/Publish-OneHistoryModule.ps1 -Module HistoryMinerva` 负责。
（双槽安装并退役 Mapping、SWuse 与历史转换模块槽）。只读命令域为 `historyminerva`。

测试、UI Smoke 与四个真实 CAD 端到端门禁由独立的
[`b-Code-HistoryMinerva-Tests`](../b-Code-HistoryMinerva-Tests/README.md) 维护。
