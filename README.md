# HistoryMinerva

HistoryMinerva 是注册到 HistoryVulcan 宿主的单一 CAD 转换模块。当前源码为 `4.4.2`。

- **前端**：`HistoryMinerva.dll`——中央停靠页 `Minerva`（`.par → .SLDPRT`、`.asm → .SLDASM`、
  SolidWorks 特征整备与属性整备改名），模块名为 `HistoryMinerva`、命令域为 `minerva`（全部取自
  `HistoryMinervaIdentity` 唯一权威源）。Worker 查询通过 `minerva.worker.*` 进入 Vulcan 命令总线与 MCP。
- **后端**：单一 `HistoryMinerva.Worker.exe`（x64 STA）只接受
  `<verb> <json> --cancel <signal>` 转换协议。历史 SWuse 建模链已删除。
- **测试**：`b-Code-HistoryMinerva-Tests` 下 Smoke、UiSmoke 与三个 CAD 真机门禁。

内部程序集、命名空间与 JSON 协议为 `HistoryMinerva.*`，不保留 `mapping.*` / `swuse.*` 兼容别名。
源 CAD 文件保持只读，产物写入来源目录下的 `XT/` 与 `SW/`。

## 入口

| 内容 | 位置 |
| --- | --- |
| 项目身份、路径和命令 | [`project.manifest.json`](./project.manifest.json) |
| 项目状态 | [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) |
| 需求与架构 | [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) |
| 验证方式 | [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) |
| 生产模块源码 | [`b-Code-HistoryMinerva/README.md`](./b-Code-HistoryMinerva/README.md) |
| 独立测试与 CAD 门禁 | [`b-Code-HistoryMinerva-Tests/README.md`](./b-Code-HistoryMinerva-Tests/README.md) |
| 模块对外 API | [`b-Office/package/模块API.md`](./b-Office/package/模块API.md) |
| SWuse V0.1 历史档案 | [`b-Office/history/swuse-v0.1/docs`](./b-Office/history/swuse-v0.1/docs) |

## 快速验证

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --capture .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\bin\Release\historyminerva-ui.png
```

推送或向 `2026-024-HistoryMinerva` 提交 PR 时，`.github/workflows/historyminerva-gate.yml` 会在
Windows 托管机上复验锁定还原、静态质量门禁、Debug/Release 构建与 Smoke、双尺寸 UI Smoke、
格式和候选包边界。

真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。

作者：Pinavia
