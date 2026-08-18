# HistoryMinerva 测试与 CAD 门禁

本目录独立维护 HistoryMinerva 的自动测试、UI Smoke 与真实 CAD 生产门禁。生产实现位于
[`../b-Code-HistoryMinerva`](../b-Code-HistoryMinerva/README.md)，测试仅通过显式 `ProjectReference` 消费它。

```text
tests/HistoryMinerva.Smoke    转换协议、装配规划、命令面、特征整备合同
tests/HistoryMinerva.UiSmoke  WPF 停靠页显示与截图入口
tools/                        三个可重复执行的真实 CAD 端到端门禁
```

从项目根目录执行离线验证：

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
.\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --compact --capture .\artifacts\historyminerva-ui.png
```

上述解决方案只纳入生产工程、Smoke 与 UI Smoke；`tools/` 下需要 CAD 授权和桌面会话的三个真机门禁保持独立运行：

- `AssemblyProductionGate`：Solid Edge 装配探查、转换、重开与矩阵/配合验收；
- `FeatureWorksBatchSmoke`：Solid Edge 零件批量转换、FeatureWorks 和进程收束；
- `SolidWorksSelfPipelineGate`：SolidWorks 装配自整备、特征/草图、位置和源文件不变性。

一次性 COM 探索工具与历史 SWuse 建模门禁已删除。真实 CAD 门禁只在专用样件和受控 CAD 会话中执行。
