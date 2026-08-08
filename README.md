# HistoryMinerva

HistoryMinerva 是注册到 AppShell 宿主的单一 CAD 模块（4.2.0）。前身 Mapping（SE2SW）与 SWuse
两个模块经破坏性重构合并为一个发布单元：纯前端 + 纯后端。

- **前端**：`HistoryMinerva.dll`——中央停靠页 `Minerva`（原 Mapping 页面原样保留：
  `.par → .SLDPRT`、`.asm → .SLDASM`），模块名与指令域统一为 `HistoryMinerva`（全部取自
  `HistoryMinervaIdentity` 唯一权威源）；SWuse 独立窗口已移除（待打磨后回归），
  其占位指令 `HistoryMinerva.show/hide/status` 如实说明现状。
- **后端**：单一 `HistoryMinerva.Worker.exe`（x64 STA）内部按参数形态路由两条既有协议——
  SE2SW 的 `<verb> <json> --cancel <signal>` 四参数链与 SWuse 的 `--request <json>` 双参数链；
  协议与数据目录行为不变。SWuse 的 C# 建模能力（Roslyn 编译 + `SWuse.Api`）保留在协议层。
- **测试**：`b-Code-HistoryMinerva-Tests` 下 Smoke 合一、UiSmoke 与全部 CAD 探针/门禁归拢一处。

内部程序集、命名空间与 JSON 协议继续沿用 `SE2SW.*` / `SWuse.*` 名称（沿用 V4.0.0
“对外改名、内部不动”先例）；对外模块名、指令域与 MCP 名称统一为 `HistoryMinerva`，
发布目录为单一 `z-HistoryMinerva`（清单与运行产物同处）。

用户界面只配置两件事：转换来源与转换内容。内部使用 Parasolid `.x_t` 和独立 Worker 完成
CAD COM 自动化；源 CAD 文件保持只读，产物写入来源目录下的 `XT/` 与 `SW/`。

## 入口

| 内容 | 位置 |
| --- | --- |
| 项目身份、路径和命令 | [`project.manifest.json`](./project.manifest.json) |
| 项目状态 | [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) |
| 需求与架构 | [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) |
| 4.2.0 前后端分离重构 | [`b-Office/current/V4.2.0-前后端分离重构计划.md`](./b-Office/current/V4.2.0-前后端分离重构计划.md) |
| 验证方式 | [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) |
| 生产模块源码 | [`b-Code-HistoryMinerva/README.md`](./b-Code-HistoryMinerva/README.md) |
| 独立测试与 CAD 门禁 | [`b-Code-HistoryMinerva-Tests/README.md`](./b-Code-HistoryMinerva-Tests/README.md) |
| AppShell 模块清单 | [`z-HistoryMinerva/module.manifest.json`](./z-HistoryMinerva/module.manifest.json) |
| NuGet/OHS 发布说明 | [`b-Office/package/复用说明.md`](./b-Office/package/复用说明.md) |
| 模块合并计划（4.1.0） | [`b-Office/history/42-V4.1.0-HistoryMinerva模块合并计划.md`](./b-Office/history/42-V4.1.0-HistoryMinerva模块合并计划.md) |
| SWuse V0.1 设计档案 | [`b-Office/history/swuse-v0.1/docs`](./b-Office/history/swuse-v0.1/docs) |

## 快速验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet build .\b-Code-HistoryMinerva\src\HistoryMinerva\HistoryMinerva.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --capture .\artifacts\historyminerva-ui.png
```

真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。

作者：Pinavia
