# SE2SW 测试与 CAD 门禁

本目录独立维护 SE2SW 的自动测试、UI Smoke、COM 探针和真实 CAD 生产门禁。生产模块位于
[`../b-Code-SE2SW`](../b-Code-SE2SW/README.md)，测试工程只通过显式 `ProjectReference` 消费它，
不向 OHS 模块槽发布任何测试资产。

## 目录

```text
tests/SE2SW.Smoke     不启动 CAD 的协议、扫描、版本、清单和生命周期回归
tests/SE2SW.UiSmoke   WPF 页面显示与截图入口
tools/                Solid Edge、SolidWorks、FeatureWorks、装配和几何专项门禁
```

## 快速验证

从项目根目录执行：

```powershell
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
```

真实 CAD 工具必须按各自 README 使用专用样件和受控会话。离线 Smoke 不能替代 COM、几何、装配或进程
生命周期验收。
