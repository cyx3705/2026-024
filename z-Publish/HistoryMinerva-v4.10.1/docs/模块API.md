# HistoryMinerva 模块 API

本文件是 HistoryMinerva `4.10.1` 源码与候选 `z-Publish/HistoryMinerva-v4.10.1` 对外消费面的唯一合同；
模块只提供五种 CAD 转换，不再包含历史 SWuse 建模 API。属性整备写入与 `minerva.*`
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
| `minerva.conversion.run` | 仅前端 | 转换当前选择来源；属性整备模式下是改名并写零件属性（图号前缀为空即删图号），整体打包模式下是生成 `STP/`、`DWG/`、`PDF/`、`BOM/` 四个目录；可选 `content` 与页面转换内容对齐；UI 线程、写入 |
| `minerva.conversion.cancel` | 仅前端 | 取消当前转换或探查；UI 线程 |
| `minerva.ui.describe` | Aurora 内部 | 返回描述式页面 JSON；只读、隐藏，不返回 WPF 对象 |
| `minerva.ui.data` | Aurora 内部 | 按 `view=parts|content|materials|surfaces|heats` 提供组件数据；零件行含隐藏 `id`、三个属性列与整体打包的 `quantity` / `hasdrawing` / `category` 三列；后三个 view 是三个属性下拉的候选（材料取本机 SolidWorks 收藏材质，另两个取属性标签模板），行只有 `value` 一列；只读、隐藏 |
| `minerva.ui.actions` | Aurora 内部 | 返回页面动作声明 JSON；只读、隐藏 |
| `minerva.ui.source` | Aurora 页面 | 先套用 `content`（页面当前转换内容），再写入来源。属性整备收 `.SLDASM` 装配体。形态不符时不失败、不改来源、不解析 |
| `minerva.ui.picksource` | Aurora 页面 | 先套用 `content`（页面当前转换内容）：装配体三项打开文件对话框，零件项打开文件夹对话框 |
| `minerva.ui.content` | Aurora 页面 | 设置五种转换内容之一 |
| `minerva.ui.options` | Aurora 页面 | 设置转换开关与图号前缀：`option=recognize|continue|mates|prefix`；`prefix` 允许空值且不失败；UI 线程 |
| `minerva.ui.property` | Aurora 页面 | 属性整备下一键刷满一个属性槽：`field=designer|date|material|surface|heat`，`date` 取系统当日；UI 线程、隐藏 |
| `minerva.ui.cell` | Aurora 页面 | 属性整备下改一行零件的材料 / 表面处理 / 热处理：`field=material|surface|heat` 加 `id`；经 `aurora.ui.dialog kind=choice` 在候选里选；UI 线程、隐藏 |
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

### 属性整备（4.3.9 新增改名，4.7.0 增加写零件属性，4.7.1 改写配置特定槽，4.8.0 三槽改下拉并按材质库应用材料，4.8.1 增加「名称」槽、解析期读回三槽与改名后自动重建索引，4.9.0 删图号并入同一次写入、按第一个空格认既有图号）

第五个转换动词 `--rename-assembly` 消费 `AssemblyRenameRequest`：`drawingPrefix` 为用户手写前缀，
`entries` 为就地改名清单（源与目标必须同目录、同扩展名）。只接受 `sourceFormat = SolidWorks`。
`drawingPrefix` **允许为空**：空前缀不是非法输入，而是「这一轮把图号改成空」。文件名规则只有一条——
`图号 名称.ext`，图号为空时退化成 `名称.ext`。4.9.0 起 `stripBySpace` 字段、Worker 分支 `StripBySpace`
与指令 `minerva.conversion.strip` 一并退役：它算出来的目标文件名与空前缀那一份计划本来就相同。
识别既有图号的唯一口径是**文件名里第一个空格**——前面是图号段、后面是名称，**不校验图号段长什么样**，
不合本项目命名规则的旧号（手写的、别的项目带过来的、带括号的）照样按空格切。
不产生 XT/SW 输出目录。图号规则见现行技术合同 REQ-008。

4.7.0 在同一动词上追加写零件「自定义」属性，字段仍然只追加不插入：

- `AssemblyRenameRequest.writeProperties`（缺省 false）：为 true 时改名收工后逐个打开零件写属性并保存。
- `RenameEntry.properties`（缺省 `null`）：`PartPropertyWrite`——
  `drawingNumber` / `category` / `date` / `designer` / `material` / `surfaceTreatment` / `heatTreatment`，
  末尾追加 `materialDatabase`（4.8.0，缺省空串）与 `name`（4.8.1，缺省空串）。
  **空串表示这一槽本轮不写**，Worker 跳过它，不会把模板里已有的值抹成空。
- `RenameEntry.partName`（4.8.1，末尾追加，缺省空串）：文件名里图号之后的那一段原零件名称，
  与 `targetPath` 的文件名出自同一次计算，落进「名称」槽。它没有界面入口——名称就是改名用的
  那个名称，再让用户填一遍只会制造两份互相矛盾的真话。
- `material` 是**材质名**，不会被当成文本写进「材料」槽。模板里那一槽是 `Mode="SWProperty"` 的链接：
  Worker 先 `IPartDoc.SetMaterialPropertyName2(活动配置, materialDatabase, material)` 把材质应用到零件，
  再把「材料」槽写成链接记号 `SW-Material`。`materialDatabase` 原样取自 SolidWorks 收藏材料，
  库名（`SOLIDWORKS 材料`）与 `.sldmat` 全路径两种都能直接用。只给 `material` 不给
  `materialDatabase` 时 Worker 当场报错停批——传空库名不报错也不生效。

槽名与固定值集中在 `HistoryMinerva.Contracts.PartPropertyNames`：图号、**名称**、**类型选择**、日期、
设计、材料、表面处理、热处理（八个）；类型选择恒为 `机加件`，日期为文本型 `yyyy/MM/dd`。属性只写拿到
图号的 `.SLDPRT`，装配体与未编号内部件不写。前缀为空的那一轮照样写属性：「图号」槽写成空串
（清空，不删槽），其余槽照常写。

槽名的权威来源是属性标签模板 `2026-025-实验室测绘/z-SW模板配置/03 属性模板/精密零件属性.prtprp`
里每个 `<Control>` 的 `PropName`，**不是界面标题**（`Label`），两者可以不同。该模板全部控件为
`ApplyTo="Config"`，因此属性写入**活动配置**的「配置特定」槽，不写文档级「自定义」槽。

不带 `writeProperties` / `properties` 的历史请求 JSON 仍是一次纯改名。

4.8.1 在探查一侧追加零件属性读数，字段同样只追加不插入：

- `AssemblyProbeResult.partProperties`（缺省 `null`）：`PartPropertyReading` 列表，逐个唯一
  `.SLDPRT` 一条，含 `sourcePath` / `material` / `materialDatabase` / `surfaceTreatment` / `heatTreatment`。
  只有 SolidWorks 源会产出；`null` 表示这次没有读属性，**不是**「零件上没有值」。
- 读数走装配里已经载入的组件文档，不为此另开零件；材料取 `GetMaterialPropertyName2` 的名字
  **与材料库**（只有名字的材质应用不回零件）。任何一格读不到就留空串，不把探查判为失败。
- 消费方按 `sourcePath` 对齐即可把它当作属性整备表格的预填值。

`RenameReindex`（4.8.1，`HistoryMinerva.Contracts`）是纯内存的改名后重建索引：`Moved` 按磁盘实况
（目标存在且源已消失）算出真的改成了的那一批，`Remap` 据此产出一份同形但路径已更新的探查结果，
`RemapKeys` 搬「源路径 → 值」的记账。它不碰 CAD，也不改读数、层级、矩阵与装配关系。

### 普通 SolidWorks 零件整备（4.3.11，4.3.17 取消原零件检测，4.3.19 锁定 XT 文本导出）

特征整备不判断源零件有没有特征树。每个 `.SLDPRT` 都先严格导出为交付用 `.x_t`，再对 XT 做 FeatureWorks。
已有 `SW/` 产物一律重做。输出布局创建 `XT/` 目录。识别失败或几何被改坏时保留压平后的导入体，不退回源特征树。
Smoke 禁止生产代码出现「跳过整备」或「源零件已有特征树」；真机门禁要求每个零件都留下非空 XT。

### 整体打包（4.10.0 新增）

Worker 动词 `--package-assembly`，请求体 `PackageRequest`：

```json
{
  "batchId": "…",
  "sourceAssemblyPath": "D:\\设备\\GHLSS-06-00 总装.SLDASM",
  "jobs": [
    { "id": "…", "sourcePath": "…\\GHLSS-06-01 阀体.SLDPRT", "outputPath": "…\\STP\\GHLSS-06-01 阀体.STEP", "artifact": 0 },
    { "id": "…", "sourcePath": "…\\GHLSS-06-01 阀体.SLDDRW", "outputPath": "…\\DWG\\GHLSS-06-01 阀体.DWG", "artifact": 1 },
    { "id": "…", "sourcePath": "…\\GHLSS-06-01 阀体.SLDDRW", "outputPath": "…\\PDF\\GHLSS-06-01 阀体.PDF", "artifact": 2 }
  ],
  "overwrite": true
}
```

`artifact` 是 `PackageArtifact`（按数值序列化）：`0=Step`、`1=Dwg`、`2=Pdf`。同一个零件的三个作业
共用一个 `id`——表里它们本来就是同一行。退出码 `0` 全部成功、`1` 部分失败、`3` 取消、`4` 整批失败；
部分失败不判整批失败，逐条成败按 `PackageExport` / `Failed` 事件回报。

源文档全程只读：优先复用会话里已打开的文档，自己打开的一律 Silent + ReadOnly 并在关闭后比对
SHA256。工程图**不做工作副本**——复制到别的目录会断掉它对零件的引用，导出来的是空图。
PDF 走 `GetExportFileData(swExportPdfData)` + `SetSheets(swExportData_ExportAllSheets)`，
否则多页工程图只导当前页。

探查一侧同步追加外购件读数，字段只追加不插入：

- `AssemblyProbeResult.purchasedParts`（缺省 `null`）：`PurchasedPartReading` 列表，
  含 `sourcePath` 与 `instanceCount`。只有 SolidWorks 源会产出；`null` 表示这次没有采集，
  **不是**「这台设备没有外购件」。数量只在顶层展平那一遍累加，抑制实例不计。
- 4.10.1 起**只收边界那一层**：从总装往下第一个离开装配体目录的组件即为一种外购货，
  它底下的后代一概不出现在 `purchasedParts`，也不出现在 `occurrences`。消费方因此可以
  假定这张表上一行＝一种可下单的货，而不是一堆拆开的零件。

两张 BOM **不经过 Worker**：`PackagePlanner` 算计划，`BomWorkbookWriter` 按嵌在
`HistoryMinerva.dll` 里的现场模板插数据行。机加件写 A 序号 / B 零件图号 / C 零件名称 / D 数量；
外购件写 A 序号 / D 规格 / E 名称 / F 数量。外购件的规格与名称按中文与非中文切分
（`PurchasedPartNaming`），不按第一个空格切。

## 数据目录

转换数据位于 `%AppData%/HistoryVulcan/HistoryMinerva/`（`requests/` 请求、`probes/` 探查结果）。
