# HistoryMinerva

> CAD 转换模块：Solid Edge 转 SolidWorks、属性整备改名与零件采购打包

![OneHistory Logo](./Logo.png)

## 定位

HistoryMinerva 是注册到 HistoryVulcan 的单一 CAD 转换模块，在 Aurora 中央页面 `Minerva` 上提供五种转换：

- `.par → .SLDPRT`、`.asm → .SLDASM`；
- `.SLDASM` 特征整备（导出 XT → 特征识别 → 按源层级重建）；
- `.SLDASM` 属性整备（按装配层级改名、写属性）；
- `.SLDASM` 整体打包：在总装旁生成 `<前缀> 零件采购/`，含两张 BOM、同名 PNG 与机加件 DWG / PDF / STEP。

转换由单一 `HistoryMinerva.Worker.exe`（x64 STA）执行。源 CAD 文件保持只读，产物写入来源目录下的 `XT/`、`SW/` 与 `<前缀> 零件采购/`。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-024` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `minerva`（身份唯一权威源 `HistoryMinervaIdentity`） |
| 界面 | Aurora 描述化页面 `Minerva` |
| MCP 投影 | `readonly`；`minerva.conversion.*` 只从前端执行 |
| 版本与宿主下限 | [`HistoryMinerva.Version.props`](./b-Code-HistoryMinerva/build/HistoryMinerva.Version.props) |

## 能力

| 面 | 入口 | 用途 |
| --- | --- | --- |
| 代码面 | `HistoryMinerva.Contracts.dll` | 图号、改名计划、打包计划、路径布局、Worker 协议等纯函数与 DTO |
| 总线面 | `minerva.plan.package` / `rename` | 无状态入口：给装配体路径，拿回完整打包计划 / 改名计划 |
| 总线面 | `minerva.conversion.probe` / `run` / `cancel` | 解析装配体、执行当前转换、取消 |
| 总线面 | `minerva.ui.content` / `source` / `options` | 页面状态：转换内容、来源、选项 |
| MCP | `minerva.worker.status` / `path` / `capabilities` / `show` / `hide` | Worker 就绪查询 |

跨模块与 AI 消费优先走无状态的 `plan.*`。完整能力总表见 [模块 API](./b-Office/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office/current/验证合同.md) | 验证层级、命令与证据 |
| [模块 API](./b-Office/package/模块API.md) | 跨模块消费合同 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryMinerva/` | 生产模块：前端、Worker、Contracts 与 `eng/` 门禁脚本，见 [README](./b-Code-HistoryMinerva/README.md) |
| `b-Code-HistoryMinerva-Tests/` | Smoke、UiSmoke 与三个 CAD 真机门禁，见 [README](./b-Code-HistoryMinerva-Tests/README.md) |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet restore .\HistoryMinerva.sln --locked-mode -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-HistoryMinerva\eng\Test-QualityGate.ps1
dotnet build .\HistoryMinerva.sln -c Release --no-restore -warnaserror -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj -c Release -p:NuGetAudit=false
```

推送或提交 PR 时 [`historyminerva-gate.yml`](./.github/workflows/historyminerva-gate.yml) 在 GitHub Actions 上复验。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布。

## 要点

- 真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。
- 打包的 BOM 不经过 Worker，SolidWorks 起不来时清单照样能出；外购件品牌经 HistoryApollo 联网查询，查不到写 N/A。
- 内部程序集、命名空间与 JSON 协议均为 `HistoryMinerva.*`，不保留 `mapping.*` / `swuse.*` 兼容别名。

---

作者：Pinavia
