# HistoryMinerva 模块 API

本文件是 HistoryMinerva `4.3.2` 源码与正式 `z-HistoryMinerva` 对外消费面的唯一合同；
SolidWorks 自整备管线与 `minerva.*` 命令面均已正式生效。
构建与部署验收命令见 `../current/验证合同.md`；NuGet 打包暂不开放，OHS 旧宿主已停用。

## 模块身份

全部名称取自 `HistoryMinerva.Contracts` 的 `HistoryMinervaIdentity` 唯一权威源：

| 项 | 值 |
| --- | --- |
| 模块名 / 部署槽 / 数据目录 | `HistoryMinerva` |
| 命令域 / MCP 工具前缀 | `minerva` / `minerva_` |
| 停靠页标题 | `Minerva` |
| 窗口内部标识 / 日志类别 | `historyminerva` |
| Worker 可执行文件 | `HistoryMinerva.Worker.exe` |
| HistoryVulcan 宿主基线 | `3.9.0` 正式快照 |

## HistoryVulcan 命令面

宿主命令表忽略大小写（小写输入照常命中）；MCP 曝光为只读（`mcpExposure=readonly`）。

| 命令 | 位置 | 说明 |
| --- | --- | --- |
| `minerva.conversion.probe` | 仅前端 | 解析当前装配来源；UI 线程、`Readonly=true`、`AllowMcpExecution=false` |
| `minerva.conversion.run` | 仅前端 | 转换当前选择来源；UI 线程、`Readonly=false`、`AllowMcpExecution=false` |
| `minerva.conversion.cancel` | 仅前端 | 取消当前转换或探查；UI 线程、禁止 MCP |
| `minerva.worker.show` | 后台 | 只读报告中央工作区状态，不创建独立窗口 |
| `minerva.worker.hide` | 后台 | 只读报告中央工作区没有可隐藏的独立窗口 |
| `minerva.worker.status` | 后台/MCP | 报告 Worker 就绪状态 |
| `minerva.worker.path` | 后台/MCP | 报告宿主上下文解析出的实际 Worker 路径 |
| `minerva.worker.capabilities` | 后台/MCP | 报告合并 Worker 支持的协议能力 |

页面探查、转换和取消不再直接调用 ViewModel 作为失败回退。Worker 事件通过命令上下文的
`Progress` 进入 Vulcan `cmd:progress:minerva:conversion` 日志与控制台；最终结果由同一命令总线回显。

## Worker 协议

单一 `HistoryMinerva.Worker.exe`（x64 STA）按参数形态路由两条既有协议，JSON 形态与退出码语义不变：

| 协议 | 参数形态 | 用途 |
| --- | --- | --- |
| HistoryMinerva 转换 | `<verb> <json> --cancel <signal>` | `--request` 零件批次 / `--import-part` 单件导入 / `--probe-assembly` 装配探查 / `--assembly` 装配构建 |
| SWuse 构建 | `--request <json>` | Roslyn dry-run 编译 + SolidWorks 零件构建，结果 JSON 写 stdout |

### 源格式（4.3.0 新增）

四个转换请求（`BatchRequest` / `PartImportRequest` / `AssemblyProbeRequest` / `AssemblyBatchRequest`）
末尾追加可选字段 `sourceFormat`：`0 = SolidEdge`（缺省）、`1 = SolidWorks`。动词、事件与结果
JSON 形态、退出码语义都不变；**不带该字段的历史请求仍按 Solid Edge 执行**。

取 `SolidWorks` 时：源为 `.SLDASM` / `.SLDPRT`，不产生 `.x_t`，`ConversionJob.xtPath` 不被读取
（可留空字符串），产物落在输出目录且不得与源文件同路径。装配关系新增 SolidWorks 侧接口名
`SwFixedComponent` / `SwCoincident` / `SwConcentric` / `SwDistance`，未实测的类型以
`SwMateType<n>` 形式如实带进报告。

## SWuse.Api 建模表面

用户 C# 代码以 `SWuse.Api` 为编译引用：`PartProgram` 基类 + `[SwuseEntry]` 入口标记 +
`PartBuilder`（Sketch/Extrude/CutExtrude 等）。默认工作区为 `文档/HistoryMinervaWorkspace`。

## 数据目录

- 转换侧：`%AppData%/HistoryVulcan/HistoryMinerva/`（`requests/` 请求、`probes/` 探查结果）。
- 建模侧：`%LocalAppData%/HistoryMinerva/HistoryMinerva/requests/`（构建请求暂存，执行后自删）。
