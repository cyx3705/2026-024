# Mapping 测试与 CAD 门禁

本目录独立维护 Mapping 的自动测试、UI Smoke、COM 探针与真实 CAD 生产门禁。生产实现位于
[`../b-Code-SE2SW`](../b-Code-SE2SW/README.md)，测试仅通过显式 `ProjectReference` 消费它；
内部程序集和协议继续使用 SE2SW 名称。

```text
tests/SE2SW.Smoke     不启动 CAD 的协议、映射选项、命令、清单和生命周期回归
tests/SE2SW.UiSmoke   WPF Mapping 页面显示与截图入口，加载 AppShell 3.1.9 浅/深主题
tools/                真实 CAD COM 探针与生产门禁
```

从项目根目录执行离线验证：

```powershell
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.UiSmoke\SE2SW.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --compact --capture .\artifacts\mapping-ui.png

# 主题与尺寸覆盖
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.UiSmoke\SE2SW.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --dpi 150 --capture .\artifacts\mapping-320-dark.png
```

真实 CAD 门禁只在专用样件和受控 CAD 会话中执行；本次 UI 与命名升级不替代或重跑该门禁。
