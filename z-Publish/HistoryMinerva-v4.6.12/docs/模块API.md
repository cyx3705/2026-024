# HistoryMinerva 模块 API

本文件是 HistoryMinerva `4.6.12` 源码与候选 `z-Publish/HistoryMinerva-v4.6.12` 对外消费面的唯一合同；
模块只提供四种 CAD 转换，不再包含历史 SWuse 建模 API。属性整备改名/洗图号与 `minerva.*`
命令面均已在源码生效。构建与部署验收命令见 `../current/验证合同.md`；NuGet 打包暂不开放，OHS 旧宿主已停用。

## 模块身份

全部名称取自 `HistoryMinerva.Contracts` 的 `HistoryMinervaIdentity` 唯一权威源：

| 项 | 值 |
| --- | --- |
| 模块名 / 部署槽 / 数据目录 | `HistoryMinerva` |
| 命令域 / MCP 工具前缀 | `minerva` / `minerva_` |
| 停靠页标题 | `Minerva` |
| 窗口内部标识 / 日志类别 | `historyminerva` |
| Worker 可执行文件 | `HistoryMinerva.Worker.exe` |
| HistoryVulcan 宿主基线 | `5.1.2` 正式快照 |

## HistoryVulcan 命令面

宿主命令表忽略大小写（小写输入照常命中）；MCP 曝光为只读（`mcpExposure=readonly`）。

| 命令 | 位置 | 说明 |
| --- | --- | --- |
| `minerva.conversion.probe` | 仅前端 | 解析当前装配来源；只收录与装配体同级的文件，跳过子文件夹外购件；可选 `content` 与页面转换内容对齐；UI 线程、只读 |
| `minerva.conversion.run` | 仅前端 | 转换当前选择来源，或在属性整备模式下按图号改名；可选 `content` 与页面转换内容对齐；UI 线程、写入 |
| `minerva.conversion.strip` | 仅前端 | 属性整备模式下按文件名第一个空格洗掉图号；可选 `content` 与页面转换内容对齐；UI 线程、写入 |
| `minerva.conversion.cancel` | 仅前端 | 取消当前转换或探查；UI 线程 |
| `minerva.ui.describe` | Aurora 内部 | 返回描述式页面 JSON；只读、隐藏，不返回 WPF 对象 |
| `minerva.ui.data` | Aurora 内部 | 按 `view=parts|content` 提供组件数据；只读、隐藏 |
| `minerva.ui.actions` | Aurora 内部 | 返回页面动作声明 JSON；只读、隐藏 |
| `minerva.ui.source` | Aurora 页面 | 先套用 `content`（页面当前转换内容），再写入来源。属性整备收 `.SLDASM` 装配体。形态不符时不失败、不改来源、不解析 |
| `minerva.ui.picksource` | Aurora 页面 | 先套用 `content`（页面当前转换内容）：装配体三项打开文件对话框，零件项打开文件夹对话框 |
| `minerva.ui.content` | Aurora 页面 | 设置四种转换内容之一 |
| `minerva.worker.show` | 后台 | 只读报告中央工作区状态，不创建独立窗口 |
| `minerva.worker.hide` | 后台 | 只读报告中央工作区没有可隐藏的独立窗口 |
| `minerva.worker.status` | 后台/MCP | 报告 Worker 就绪状态 |
| `minerva.worker.path` | 后台/MCP | 报告宿主上下文解析出的实际 Worker 路径 |
| `minerva.worker.capabilities` | 后台/MCP | 报告 Worker 支持的转换协议能力 |

页面探查、转换和取消不再直接调用 ViewModel 作为失败回退。Worker 事件通过命令上下文的
`Progress` 进入 Vulcan `cmd:progress:minerva:conversion` 日志与控制台；最终结果由同一命令总线回显。
页面由 Aurora 根据 `minerva.ui.describe` 统一创建，Minerva 不注册 `minerva.ui.pane`，也不返回
`System.Windows.UIElement`；页面按钮只绑定 `minerva.ui.actions` 中的稳定动作 ID。

## Worker 协议

单一 `HistoryMinerva.Worker.exe`（x64 STA）只接受转换协议，JSON 形态与退出码语义不变：

| 协议 | 参数形态 | 用途 |
| --- | --- | --- |
| HistoryMinerva 转换 | `<verb> <json> --cancel <signal>` | `--request` 零件批次 / `--import-part` 单件导入 / `--probe-assembly` 装配探查 / `--assembly` 装配构建 / `--rename-assembly` SolidWorks 属性整备改名 |

历史 SWuse 双参数 `--request <json>`（Roslyn 编译 + C# 零件构建）已删除，不再路由。

### 源格式（4.3.0 新增）

四个转换请求（`BatchRequest` / `PartImportRequest` / `AssemblyProbeRequest` / `AssemblyBatchRequest`）
末尾追加可选字段 `sourceFormat`：`0 = SolidEdge`（缺省）、`1 = SolidWorks`。属性整备改名是第五个动词
`--rename-assembly`，请求体为 `AssemblyRenameRequest`，只接受 SolidWorks。动词、事件与结果
JSON 形态、退出码语义都不变；**不带该字段的历史请求仍按 Solid Edge 执行**。

取 `SolidWorks` 时：源为 `.SLDASM` / `.SLDPRT`，先导出交付用 `.x_t` 再导入识别，
`ConversionJob.xtPath` 必填且指向 `XT/` 下的中转件，产物落在 `SW/` 且不得与源文件同路径。装配关系新增 SolidWorks 侧接口名
`SwFixedComponent` / `SwCoincident` / `SwConcentric` / `SwDistance`，未实测的类型以
`SwMateType<n>` 形式如实带进报告。

### 属性整备改名（4.3.9 新增，4.3.10 增加洗图号）

第五个转换动词 `--rename-assembly` 消费 `AssemblyRenameRequest`：`drawingPrefix` 为用户手写前缀，
`entries` 为就地改名清单（源与目标必须同目录、同扩展名）。只接受 `sourceFormat = SolidWorks`。
记录末尾追加可选 `stripBySpace`（缺省 false）：为 true 时按文件名第一个空格洗掉图号，不要求前缀。
不产生 XT/SW 输出目录。图号规则见现行技术合同 REQ-008。

### 普通 SolidWorks 零件整备（4.3.11，4.3.17 取消原零件检测，4.3.19 锁定 XT 文本导出）

特征整备不判断源零件有没有特征树。每个 `.SLDPRT` 都先严格导出为交付用 `.x_t`，再对 XT 做 FeatureWorks。
已有 `SW/` 产物一律重做。输出布局创建 `XT/` 目录。识别失败或几何被改坏时保留压平后的导入体，不退回源特征树。
Smoke 禁止生产代码出现「跳过整备」或「源零件已有特征树」；真机门禁要求每个零件都留下非空 XT。

## 数据目录

转换数据位于 `%AppData%/HistoryVulcan/HistoryMinerva/`（`requests/` 请求、`probes/` 探查结果）。
