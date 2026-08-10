# HistoryMinerva 测试与 CAD 门禁

本目录独立维护 HistoryMinerva 的自动测试、UI Smoke、COM 探针与真实 CAD 生产门禁。生产实现位于
[`../b-Code-HistoryMinerva`](../b-Code-HistoryMinerva/README.md)，测试仅通过显式 `ProjectReference` 消费它；
内部程序集和协议继续使用 HistoryMinerva/SWuse 名称。

```text
tests/HistoryMinerva.Smoke    合并回归：HistoryMinerva 协议/装配/命令/生命周期 + SWuse 建模 API/编译/Worker 协议
tests/HistoryMinerva.UiSmoke  WPF 停靠页显示与截图入口，加载 AppShell 3.1.9 浅/深主题
tools/                        真实 CAD COM 探针与生产门禁（含 SWuse.CadGate）
```

从项目根目录执行离线验证：

```powershell
dotnet build .\b-Code-HistoryMinerva\src\HistoryMinerva\HistoryMinerva.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --compact --capture .\artifacts\historyminerva-ui.png

# 主题与尺寸覆盖
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --dpi 150 --capture .\artifacts\historyminerva-320-dark.png
```

真实 CAD 门禁只在专用样件和受控 CAD 会话中执行；本次结构重构不替代或重跑该门禁。
注意 `tools/SolidEdgeExportProbe` 使用 COM 引用，需用 .NET Framework 版 MSBuild 构建（既有特性）。
