# SE2SW

SE2SW 是独立维护的 OneHistoryStudio / AppShell CAD 转换模块，将 Solid Edge `.par/.asm`
通过 Parasolid `.x_t` 转换为 SolidWorks `.SLDPRT/.SLDASM`。当前发布线为 `3.6.7`。

模块面向本机受控 CAD 环境：UI 负责来源选择、计划和状态呈现，独立 x64 STA Worker 负责
Solid Edge 与 SolidWorks COM 自动化。源 CAD 文件保持只读，转换产物写入源目录下的 `XT/`
与 `SW/`。

## 入口

| 内容 | 位置 |
| --- | --- |
| 项目身份、路径和命令 | [`project.manifest.json`](./project.manifest.json) |
| AI 工作约定 | [`AGENTS.md`](./AGENTS.md) |
| 项目状态 | [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) |
| 需求与架构 | [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) |
| 验证方式 | [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) |
| 模块源码 | [`b-Code-SE2SW/README.md`](./b-Code-SE2SW/README.md) |
| 测试与 CAD 门禁 | [`b-Code-SE2SW-Tests/README.md`](./b-Code-SE2SW-Tests/README.md) |
| OHS 模块清单 | [`z-SE2SW/module.manifest.json`](./z-SE2SW/module.manifest.json) |
| 发布说明 | [`b-Office/package`](./b-Office/package/复用说明.md) |

## 快速验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
```

真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。

作者：Pinavia
