# HistoryMinerva 测试与 CAD 门禁

本目录独立维护 HistoryMinerva 的自动测试、UI Smoke 与真实 CAD 生产门禁。生产实现位于
[`../b-Code-HistoryMinerva`](../b-Code-HistoryMinerva/README.md)，测试仅通过显式 `ProjectReference` 消费它；
内部程序集和协议继续使用 HistoryMinerva/SWuse 名称。

```text
tests/HistoryMinerva.Smoke    合并回归：HistoryMinerva 协议/装配/命令/生命周期 + SWuse 建模 API/编译/Worker 协议
tests/HistoryMinerva.UiSmoke  WPF 停靠页显示与截图入口，加载 AppShell 3.1.9 浅/深主题
tools/                        四个可重复执行的真实 CAD 端到端门禁
```

从项目根目录执行离线验证：

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
.\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --compact --capture .\artifacts\historyminerva-ui.png

# 主题与尺寸覆盖
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --dpi 150 --capture .\artifacts\historyminerva-320-dark.png
```

上述解决方案只纳入生产工程、Smoke 与 UI Smoke；`tools/` 下需要 CAD 授权和桌面会话的四个真机门禁保持独立运行：

- `AssemblyProductionGate`：Solid Edge 装配探查、转换、重开与矩阵/配合验收；
- `FeatureWorksBatchSmoke`：Solid Edge 零件批量转换、FeatureWorks 和进程收束；
- `SolidWorksSelfPipelineGate`：SolidWorks 装配自整备、特征/草图、位置和源文件不变性；
- `SWuse.CadGate`：SWuse C# 建模端到端验收。

一次性 COM 探索工具已删除，其已确认的矩阵、配合、FeatureWorks 和会话边界由生产实现、离线 Smoke 与上述
端到端门禁承接，不再维护第二套实现。真实 CAD 门禁只在专用样件和受控 CAD 会话中执行。
