# HistoryMinerva 模块 API

本文件是 HistoryMinerva `4.2.3` 源码、候选与正式包对外消费面的唯一合同。当前正式
`z-HistoryMinerva` 已发布为 `4.2.3`；旧 `4.2.2` 三命令快照只属于发布历史，不适用本合同。
构建与部署验收命令见 `../current/验证合同.md`；NuGet 打包暂不开放，OHS 旧宿主已停用。

## 模块身份

全部名称取自 `HistoryMinerva.Contracts` 的 `HistoryMinervaIdentity` 唯一权威源：

| 项 | 值 |
| --- | --- |
| 模块名 / 命令域 / 部署槽 / 数据目录 | `HistoryMinerva` |
| 停靠页标题 | `Minerva` |
| 窗口内部标识 / 日志类别 | `historyminerva` |
| Worker 可执行文件 | `HistoryMinerva.Worker.exe` |
| HistoryVulcan 宿主基线 | `3.4.0` 正式快照 |

## HistoryVulcan 命令面

宿主命令表忽略大小写（小写输入照常命中）；MCP 曝光为只读（`mcpExposure=readonly`）。

| 命令 | 位置 | 说明 |
| --- | --- | --- |
| `HistoryMinerva.convert` | 仅前端 | 转换当前选择来源；UI 线程、`Readonly=false`、`AllowMcpExecution=false` |
| `HistoryMinerva.cancel` | 仅前端 | 取消当前转换或探查；元数据同上 |
| `HistoryMinerva.show` | 双槽 | 占位：SWuse 独立窗口已于 4.2.1 移除，如实说明现状 |
| `HistoryMinerva.hide` | 双槽 | 占位：无独立窗口可隐藏 |
| `HistoryMinerva.status` | 双槽 | 报告 `HistoryMinerva.Worker.exe` 是否就绪 |

## Worker 协议

单一 `HistoryMinerva.Worker.exe`（x64 STA）按参数形态路由两条既有协议，JSON 形态与退出码语义不变：

| 协议 | 参数形态 | 用途 |
| --- | --- | --- |
| HistoryMinerva 转换 | `<verb> <json> --cancel <signal>` | `--request` 零件批次 / `--import-part` 单件导入 / `--probe-assembly` 装配探查 / `--assembly` 装配构建 |
| SWuse 构建 | `--request <json>` | Roslyn dry-run 编译 + SolidWorks 零件构建，结果 JSON 写 stdout |

## SWuse.Api 建模表面

用户 C# 代码以 `SWuse.Api` 为编译引用：`PartProgram` 基类 + `[SwuseEntry]` 入口标记 +
`PartBuilder`（Sketch/Extrude/CutExtrude 等）。默认工作区为 `文档/HistoryMinervaWorkspace`。

## 数据目录

- 转换侧：`%AppData%/HistoryVulcan/HistoryMinerva/`（`requests/` 请求、`probes/` 探查结果）。
- 建模侧：`%LocalAppData%/HistoryMinerva/HistoryMinerva/requests/`（构建请求暂存，执行后自删）。
