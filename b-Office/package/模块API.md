# HistoryMinerva 4.10.2 模块 API

本文件是 HistoryMinerva 对外消费面的唯一人工合同。运行时命令目录与
`HistoryMinerva.Contracts.dll` 的公开类型是最终真值；源码内部类型、历史文档、
`bin/obj` 与 `AppData` 下的运行态文件都不构成公开 API。

本文档描述 `4.10.2` 源码合同。截至撰写时正式快照仍是
`z-Publish/HistoryMinerva-v4.10.1/`；只有经宿主 `vulcan.dev.submit` / `finish` 发布后，
`4.10.2` 的 manifest 与二进制才会提升到 `z-Publish/HistoryMinerva-v4.10.2/`。
发布前，z 快照自身的 manifest 与 `SHA256SUMS` 仍是正式运行版本的真值。

---

## 0. 这份文档为什么这样组织

一个模块的「对外面」其实是三个不同的面，混在一起写就等于没写：

| 面 | 消费者 | 消费方式 | 前提 |
| --- | --- | --- | --- |
| **A 代码面** | 其它模块的 C# 代码 | 引用 `HistoryMinerva.Contracts.dll`，在自己进程里直接调 | 无。不需要 Minerva 装载、不需要宿主、不需要 SolidWorks |
| **B 总线面** | 其它模块的 C# 代码 | `context.Bus.ExecuteAsync("minerva.…")` 让 Minerva 干活 | Minerva 已接入宿主；多数条目还要 SolidWorks 与 UI 线程 |
| **C AI 面** | AI（MCP `minerva_*`） | 只读工具调用 | 同上；且只有 `Readonly && !Hidden` 的命令会投影过来 |

面 C 是面 B 的**只读子集**，不是另一套能力。面 A 与面 B **不是子集关系**：
面 A 有一堆面 B 根本没有入口的能力（例如「把文件名切成图号和名称」），
面 B 有一堆面 A 做不到的事（例如真的去开 SolidWorks 导 STEP）。

**只说「本模块是干什么的」等于没有 API 文档。** 消费方要的是：我能拿到哪一个具体动作、
签名长什么样、返回里有什么可解析的东西、什么时候会失败、以及它明确不管什么。
下面每一条能力都按这五格写。

---

## 1. 身份与消费入口

全部名称取自 `HistoryMinerva.Contracts.HistoryMinervaIdentity`，不要在消费方另写字面量。

| 项 | 值 | 常量 |
| --- | --- | --- |
| 模块名 / 部署槽 / 数据目录 | `HistoryMinerva` | `HistoryMinervaIdentity.Name` |
| 命令域 / MCP 前缀 | `minerva` / `minerva_` | `HistoryMinervaIdentity.CommandRoot` |
| 停靠页标题 | `Minerva` | `HistoryMinervaIdentity.WindowTitle` |
| 窗口内部标识 / 日志类别 | `historyminerva` | `HistoryMinervaIdentity.WindowId` |
| 模块程序集 | `HistoryMinerva.dll` | manifest `artifact` |
| **公开合同程序集** | `HistoryMinerva.Contracts.dll`（`net8.0`，无 Windows 依赖） | manifest `deps` |
| Worker 可执行文件 | `HistoryMinerva.Worker.exe`（x64 STA） | `HistoryMinervaIdentity.WorkerFileName` |
| 命令来源 | `module:HistoryMinerva` | — |
| UI / MCP | 启用 / `readonly` 只读投影 | manifest `ui` / `mcpExposure` |
| 宿主基线 | HistoryVulcan `5.1.2` 正式快照 | `project.manifest.json` |
| 源码版本 | `4.10.2` | `build/HistoryMinerva.Version.props` |
| 当前正式快照 | `z-Publish/HistoryMinerva-v4.10.1/`（`4.10.2` 尚未发布） | — |

消费方从版本化快照读 `module.manifest.json`、二进制与 `SHA256SUMS`，
从 `z-Publish/HistoryMinerva-vX.Y.Z/docs/` 或 `diana.docs.read domain=minerva file=docs/模块API.md`
读本文件。**不要**从 Minerva 的 `bin/obj`、源码工作树或 `b-Office/history/` 建立依赖。

---

## 2. 能力总表

一行一个具体能力，不是一行一个「功能领域」。

| 编号 | 能力 | 一句话用途 | 面 | 入口 | 要 SW | 要 Minerva 在场 |
| --- | --- | --- | --- | --- | --- | --- |
| A-1 | 图号切分与拼装 | 把 `图号 名称.ext` 拆开、拼回、推断前缀 | A | `DrawingNumber` | 否 | 否 |
| A-2 | 图号层级编号 | 按装配层级算 `前缀-01-02` 这种号 | A | `DrawingNumber.Child` / `RootAssembly` | 否 | 否 |
| A-3 | 改名+写属性计划 | 从一份装配探查结果算出整份改名清单 | A | `PropertyPrepPlanner.Create` | 否 | 否 |
| A-4 | 打包计划与 BOM 分表 | 算出机加件/外购件两张表、数量、要导哪些产物 | A | `PackagePlanner.Create` | 否 | 否 |
| A-5 | 外购件规格/名称切分 | 按中文与非中文把标准件名切成两栏 | A | `PurchasedPartNaming.Split` | 否 | 否 |
| A-6 | 输出目录与文件名布局 | 算 `XT/ SW/ STP/ DWG/ PDF/ BOM/` 与同名工程图路径 | A | `ConversionPathLayout` | 否 | 否 |
| A-7 | 自制件/外购件判据 | 「与总装同级＝自制」这条唯一口径 | A | `ConversionPathLayout.IsOutsideAssemblyDirectory` | 否 | 否 |
| A-8 | 属性槽名与固定值 | 八个槽名、材料链接记号、日期格式、兜底候选 | A | `PartPropertyNames` | 否 | 否 |
| A-9 | 改名后重索引 | 改完名把一份旧探查结果整体挪到新路径上 | A | `RenameReindex` | 否 | 否 |
| A-10 | 配合映射与几何匹配 | SE/SW 装配关系 → SW 配合类型与实体匹配 | A | `MateTypeMapper` / `MateGeometryMatcher` | 否 | 否 |
| A-11 | Worker 协议与全部 DTO | 自己拉起 Worker 或复现请求 JSON | A | `WorkerProtocol` + 各 record | 否 | 否 |
| B-1 | 解析装配体 | 只读走一遍装配树，产出零件表与属性读数 | B | `minerva.conversion.probe` | **是** | 是 |
| B-2 | 执行当前转换 | 按当前内容执行五种转换之一 | B | `minerva.conversion.run` | **是** | 是 |
| B-3 | 取消 | 取消进行中的解析或转换 | B | `minerva.conversion.cancel` | 否 | 是 |
| B-4 | 选转换内容 | 在五种转换之间切换 | B | `minerva.ui.content` | 否 | 是 |
| B-5 | 设来源 | 设定要处理的装配体或零件文件夹 | B | `minerva.ui.source` | 否 | 是 |
| B-6 | 设选项 | 特征识别/容错/配合/图号前缀四个开关 | B | `minerva.ui.options` | 否 | 是 |
| B-7 | 取页面数据 | 读零件表当前内容与三个属性槽候选 | B（隐藏） | `minerva.ui.data` | 否 | 是 |
| B-8 | 批量刷属性槽 | 把一列属性统一到同一个值 | B（隐藏） | `minerva.ui.property` | 否 | 是 |
| B-9 | 改单行属性/名称 | 改一行的材料/表面/热处理/名称 | B（隐藏） | `minerva.ui.cell` | 否 | 是 |
| B-10 | 选来源（弹窗） | 弹系统对话框让人选 | B（隐藏） | `minerva.ui.picksource` | 否 | 是 |
| B-11 | 页面描述/动作 | Aurora 建页用的两份 JSON | B（隐藏） | `minerva.ui.describe` / `.actions` | 否 | 是 |
| B-12 | Worker 就绪查询 | Worker 在不在、在哪、支持什么 | B/C | `minerva.worker.status` / `.path` / `.capabilities` | 否 | 是 |
| B-13 | 工作区状态 | 报告中央工作区承载方式 | B/C | `minerva.worker.show` / `.hide` | 否 | 是 |
| **B-14** | **按路径出打包计划** | 只给一个装配体路径，拿回完整 `PackagePlan` | **B/C** | `minerva.plan.package` | **是** | 是 |
| **B-15** | **按路径出改名计划** | 只给一个装配体路径，拿回完整 `AssemblyRenamePlan` | **B/C** | `minerva.plan.rename` | **是** | 是 |

B-14 / B-15 是 4.10.2 新增的**无状态**入口：入参就是路径，不碰页面状态、可并发、
结果是强类型对象。**跨模块消费与 AI 消费都应当优先走这两条**，
其余 `minerva.ui.*` 与 `minerva.conversion.*` 是给页面用的。

---

## 3. 面 A —— `HistoryMinerva.Contracts.dll`

这是 Minerva 真正意义上「给别的模块用」的那一半：**纯内存、无 CAD、无宿主、无 WPF**，
`net8.0` 目标框架，任何模块都能引用。

引用方式（与宿主快照同一写法，不复制 DLL）：

```xml
<Reference Include="HistoryMinerva.Contracts">
  <HintPath>$(HistoryMinervaPackageRoot)\HistoryMinerva.Contracts.dll</HintPath>
  <Private>false</Private>
</Reference>
```

`$(HistoryMinervaPackageRoot)` 指向当前正式快照目录（撰写时是 `z-Publish/HistoryMinerva-v4.10.1/`）。
`<Private>false</Private>`：运行期由宿主从 Minerva 的运行包加载同一份程序集，
消费方自己再拷一份会出现两个不同身份的同名类型。

> **今天的限制**：包里没有 `HistoryMinerva.Contracts.xml`，所以引用方在 IDE 里看不到
> 这些类型的 XML 注释——而 Minerva 的语义几乎全写在注释里。见第 8 节缺口 G-1。

### A-1 图号切分与拼装 —— `DrawingNumber`

**用途**：本体系里「文件名＝`图号 名称.ext`」这条规则的唯一实现。
**谁会用**：任何需要从 CAD 文件名里认出图号的模块——BOM 工具、归档工具、
按图号找工程图的检索、Janus 侧按图号写提交说明。

```csharp
// 切：这是识别既有图号的唯一口径——按文件名里第一个空格切。
DrawingNumber.SplitFileName("ZS-LHL-01 侧板.SLDPRT", out var token, out var name);
// token = "ZS-LHL-01"，name = "侧板"

// 拼：图号为空时退化成 "名称.ext"，不留前导空格。
var fileName = DrawingNumber.FormatFileName("ZS-LHL-01", "侧板", ".SLDPRT");

// 推前缀：根装配叫 "前缀-00 xxx"，去掉 -00 就是前缀，用来预填输入框。
var prefix = DrawingNumber.InferPrefix("ZS-LHL-00 总装.SLDASM");   // "ZS-LHL"

// 归一化前缀：空前缀合法（＝删图号）；带空格或文件名非法字符抛 InvalidDataException。
var normalized = DrawingNumber.NormalizePrefix(userInput);
```

**语义边界（照抄，不要在消费方另立规则）**：

- **`SplitFileName` 不校验图号段长什么样。** 现场大量旧号是手写的、别的项目带过来的、带括号的；
  拿规则去卡它们会把这些文件判成「没有图号」，于是它们的真名被当作图号留在新名字里。
- 没有空格时图号段为空、整个主名都是名称——那是一个还没编过号的零件。
- `DrawingNumber.Text` 在 `Prefix` 为空时**恒为空串**，不是 `-01`：层级序号只是挂在前缀后面的
  定位符，前缀一空整个图号就该消失。层级仍照算，前缀填回来还是同一个号。
- 空前缀是正常状态，含义是「这一轮把图号改成空」，不是非法输入。

### A-2 图号层级编号 —— `DrawingNumber.RootAssembly` / `.Child`

**用途**：按装配层级派号。根装配 `前缀-00`，其下零件 `前缀-01`，子装配 `前缀-01-00`，
子装配下零件 `前缀-01-02`。小组件（第三层）以下一律按零件编号，不再往深处开层。
**谁会用**：任何要复现 Minerva 编号规则的工具——例如做图号占用检查、或离线校核一份清单。

`AssemblyMarker = 0` 是「这一层是装配体」的标记；`IsRootAssembly` / `IsMajorAssembly` /
`IsMinorAssembly` / `IsAssembly` 四个判定直接可读。

### A-3 改名 + 写属性计划 —— `PropertyPrepPlanner.Create`

**用途**：给一份装配探查结果配上前缀，算出**整份**改名清单和每个零件要写的属性载荷。
纯内存，一个文件都不动、一次 CAD 都不开。
**谁会用**：想在自己界面里预览「改名之后会变成什么样」的模块；
想做批量校核（有没有重名、有没有跨目录）的门禁工具。

```csharp
AssemblyRenamePlan plan = PropertyPrepPlanner.Create(
    probe,                    // AssemblyProbeResult，可来自 A-11 自己跑的 Worker
    drawingPrefix: "ZS-LHL",  // 空串合法 = 删图号
    partNames: null);         // 用户改过的名称，按源文件全路径记账；不给就用文件名里空格后那一段
```

**返回 `AssemblyRenamePlan` 的可解析字段**：

| 字段 | 含义 |
| --- | --- |
| `Entries` | 就地改名清单，每条 `RenameEntry` 含 `Id` / `SourcePath` / `TargetPath` / `DrawingNumber` / `AssignsDrawingNumber` / `ParentSourcePaths` / `Depth` / `PartName` / `Properties` |
| `Unnumbered` | 识别到但本模块不编号的文件（子文件夹外购件、小组件内部件） |
| `BlockingIssues` | 非空即整轮不可执行 |
| `Warnings` | 可执行但要让人看见 |
| `CanRename` | `BlockingIssues` 为空，且至少一条源≠目标 |
| `CanWrite` | **写入 = 改名 + 写属性**，因此不等于 `CanRename`：第二次点写入时文件名早对了，用它当门会把「只改材料再写一次」拒掉 |
| `AssemblyRenamePlan.IsWritablePart(entry)` | 静态判据：只有拿到图号的 `.SLDPRT` 才写属性，装配体与未编号内部件不写 |

**`RenameEntry.Id` 按源文件全路径定死**——重新解析后同一个零件仍是同一行，
消费方可以原地更新而不是整表重画。`PackagePartEntry.Id` 用同一约定，两边可以对齐。

**`PartPropertyWrite` 的空串有两种含义，按槽分**（这一条最容易被消费方写错）：

- **用户填的五槽**（日期、设计、材料、表面处理、热处理）：空串 = 本轮不动这一槽，
  Worker 跳过，不会把模板里已有的值抹成空。
- **计划算出来的三槽**（图号、名称、类型选择）：**恒写**，空串就是真的写成空。
  图号槽尤其如此——删图号就是把图号写成空串，不是删掉整槽。

`PartPropertyWrite.Pairs()` 已经把上述规则展开成「槽名 → 值」，**消费方直接用它，
不要自己按字段拼**；`HasMaterial` / `MaterialIsIncomplete` / `IsEmpty` 三个判定同理。

### A-4 打包计划与两张 BOM 的分表 —— `PackagePlanner.Create`

**用途**：把一次装配探查结果算成完整的交付计划：谁进机加件表、谁进外购件表、
每种几个、要导哪些 STEP、哪些零件有工程图、两张 BOM 叫什么名字。纯内存。
**谁会用**：采购/生产侧的任何工具。**这是 Minerva 目前最值得被别的模块消费的一块**——
它把「一个 SolidWorks 装配等于什么样的一份物料事实」算清楚了，而算这件事不需要 SolidWorks。

```csharp
PackagePlan plan = PackagePlanner.Create(probe);
string prefix = PackagePlanner.ResolveBomNamePrefix(assemblyPath);  // "GHLSS-06-00 总装" → "GHLSS"
```

**`PackagePlan` 的可解析字段**：

| 字段 | 含义 |
| --- | --- |
| `Entries` | 全部唯一零件，每条 `PackagePartEntry` |
| `Machined` / `Purchased` | 按 `PackagePartCategory` 分好的两张表，就是两张 BOM 的内容 |
| `StepTargets` | 要导 STEP 的零件 = **只有机加件**（标准件按规格供货，不需要给模型） |
| `DrawingTargets` | 有同名同目录 `.SLDDRW` 的零件，要导 DWG/PDF |
| `Directories` | `PackageOutputDirectories`：`STP/ DWG/ PDF/ BOM/`，与源装配体同级 |
| `BomNamePrefix` / `MachinedBomFileName` / `PurchasedBomFileName` | 两张 BOM 的文件名 |
| `BlockingIssues` / `Warnings` / `CanPack` | 同 A-3 |

`PackagePartEntry` 每条含 `Id` / `SourcePath` / `DrawingPath`（无图纸为 `null`）/
`DrawingNumber` / `PartName` / `Specification` / `Quantity` / `Category`，
另有 `HasDrawing` 与 `FileName` 两个计算属性。

**数量语义**：整个总装配体里的实例总数，**含嵌套倍数**（子装配用两次、里面三个同款件 = 6），
抑制件不计。
**图号语义**：取自**当前文件名**，不重新编号——BOM 描述的是此刻磁盘上的事实，
重新编号得到的是「改完名之后应该是什么」，而文件还没改。

两张 BOM 的列：机加件写 A 序号 / B 零件图号 / C 零件名称 / D 数量；
外购件写 A 序号 / D 规格 / E 名称 / F 数量。
**BOM 不经过 Worker**：`PackagePlanner` 算计划，`BomWorkbookWriter` 按嵌在 `HistoryMinerva.dll`
里的现场模板插数据行——Worker 起不起得来都不影响采购拿到清单。

### A-5 外购件规格/名称切分 —— `PurchasedPartNaming.Split`

**用途**：把标准件文件名切成「规格」和「名称」两栏。
**谁会用**：任何要按现场 BOM 模板排版标准件的工具。

```csharp
PurchasedPartNaming.Split("GB70 M8x20 内六角螺钉", out var spec, out var name);
// spec = "GB70 M8x20"，name = "内六角螺钉"
```

**判据是字符本身：中文归名称，其余归规格**——不是位置，也不是分隔符。
标准件的中文既可能在前也可能在后（`深沟球轴承 6205` / `油封 TC-25-40-7`），
按第一个空格切会把规格写成「深沟球轴承」。
**已知边界**：`M8x20内六角螺钉GB70` 这种中英夹杂无空格的名字，规格会拼成 `M8x20GB70`；
给文件名留个空格就能避开。

**注意**：这条规则与 A-1 是**两条不同的规则，不要互换**。图号那条按第一个空格切，
只用于自制件；这条按字符类型切，只用于外购件。

### A-6 输出目录与文件名布局 —— `ConversionPathLayout`

**用途**：本模块全部输出路径的唯一算法。**只解析路径，不创建目录、不检查存在性**。
**谁会用**：要去 Minerva 产出的目录里找文件的模块（归档、上传、发给外协）。

```csharp
var pack = ConversionPathLayout.ResolvePackageDirectories(assemblyDir);   // STP/ DWG/ PDF/ BOM/
var conv = ConversionPathLayout.ResolveExternalDirectories(sourceDir);    // XT/ SW/
var dwg  = ConversionPathLayout.ResolveDrawingPath(partPath);             // 同名同目录 .SLDDRW
bool isCad = ConversionPathLayout.IsKnownCadFile(path);
```

六个交付/中转目录**全部平级放在源装配体旁边**，不再套一层「打包」根目录：
交付时用户是逐个目录拖给不同的人（STP 给加工厂、DWG/PDF 给图纸审核、BOM 给采购），
多一层壳只是多一次点击。

工程图**只按「与零件同名、同目录」这一条规则找**，不递归、不跨目录——
递归找同名图会把别的项目的重名图纸打进这一次交付。

`GetSourcePartExtension` / `GetSourceAssemblyExtension` / `UsesParasolidHandoff` /
`HasLegacyFlatLayout` 四个按 `ConversionSourceFormat` 分派的判据也在这里。
其中 `HasLegacyFlatLayout` 对 SolidWorks 返回 `false` 是刻意的：
SW 源的「产物与源同目录同基名」那个位置按构造就是**源零件本身**，
把它当旧产物会让每个零件都被判成「已存在」并整轮阻断。

### A-7 自制件 / 外购件判据 —— `ConversionPathLayout.IsOutsideAssemblyDirectory`

**用途**：**本体系里判断一个零件是不是外购件的唯一口径**——与所选总装配体同级的是自制机加件，
落在子文件夹或别处的是外购件。
**谁会用**：所有要区分这两类的模块。

**不要另立「按属性槽判」或「按有没有工程图判」的第二套规则。** 同一件事有两个判据，
现场就会出现同一个零件在改名管线里算机加件、在打包管线里算外购件。

空路径不算外购件——那种情况留给「未解析引用」诊断，不要悄悄归成一类。

### A-8 属性槽名与固定值 —— `PartPropertyNames`

**用途**：SolidWorks「配置特定」属性的八个槽名与几个固定值。
**谁会用**：任何要读或写同一批零件属性的模块——不要在自己那边硬编码中文槽名。

| 常量 | 值 | 说明 |
| --- | --- | --- |
| `DrawingNumber` / `Name` / `Category` / `Date` / `Designer` / `Material` / `SurfaceTreatment` / `HeatTreatment` | 图号 / 名称 / 类型选择 / 日期 / 设计 / 材料 / 表面处理 / 热处理 | `All` 按写入顺序给出这八个 |
| `MaterialLinkValue` | `SW-Material` | 「材料」槽存的是**链接记号**，不是材质名 |
| `MachinedCategory` | `机加件` | 「类型选择」恒为此值 |
| `DateFormat` / `Today()` | `yyyy/MM/dd` | 文本型日期 |
| `NoWriteOption` | `（不写）` | 界面显示态：这一列没有共同值。**不是一个动作** |
| `FallbackSurfaceTreatments` / `FallbackHeatTreatments` | 见源码 | **兜底副本，不是权威**：正常路径是运行时读属性标签模板 |

**槽名的权威来源是属性标签模板里每个 `<Control>` 的 `PropName`，不是界面标题 `Label`**，
两者可以不同。模板在
`2026-025-实验室测绘/z-SW模板配置/03 属性模板/精密零件属性.prtprp`，
全部控件 `ApplyTo="Config"`，因此属性写的是**活动配置**的「配置特定」槽，不是文档级「自定义」槽。

**材料那一槽是链接，不是文本**：材质由 `SetMaterialPropertyName2(活动配置, 库, 材质名)` 应用到零件，
槽里只写 `SW-Material` 这个记号让属性标签认得那条链接。因此
`PartPropertyWrite.Material` 必须与 `MaterialDatabase` 成对出现——
只给材质名不给库名，`SetMaterialPropertyName2` 不报错但**什么都不做**，
于是变成「跑成功了但材料还是未指定」。`MaterialIsIncomplete` 就是拿来挡这种请求的。
`MaterialDatabase` 原样取自 SolidWorks 收藏材料：库名（`SOLIDWORKS 材料`）
与 `.sldmat` 全路径两种都能直接用，不需要归一化。

### A-9 改名后重索引 —— `RenameReindex`

**用途**：改完名之后，把一份旧的探查结果整体挪到新文件名上。**纯改路径**：
读数、层级、矩阵、装配关系一个字都不动，也不碰 CAD。
**谁会用**：任何在一次改名前后都持有同一份探查结果的模块。

```csharp
var moved = RenameReindex.Moved(plan.Entries);             // 源路径 → 新路径，判据是磁盘实况
var next  = RenameReindex.Remap(probe, moved);             // 同形但路径已更新的探查结果
var book  = RenameReindex.RemapKeys(bySourcePath, moved);  // 搬「源路径 → 值」的记账
```

**`Moved` 的判据是磁盘（目标存在且源已消失），不是 Worker 的退出码**：
部分失败时，改成了的那几个照样要跟上，没改成的那几个必须留在原路径上，
否则表格会指向一个并不存在的新名字。

**为什么必须有这一步**：改名成功的那一刻，上一次解析出来的每一条路径都指向一个不存在的文件。
重新解析要再开一遍 SolidWorks、再走一遍整棵装配树；而且源装配体的内容也变了
（`ReplaceReferencedDocument` 会重写父装配），第二次写入会被「源装配体在解析后发生变化」直接挡住。

> **消费方注意**：`Remap` 目前**不搬运 `AssemblyProbeResult.PurchasedParts`**——
> 重映射之后那一栏是 `null`。改名管线不用它，所以现场没暴露；
> 但如果你在同一份探查结果上先改名再算打包计划，外购件会整批消失。见第 8 节缺口 G-4。

### A-10 配合映射与几何匹配 —— `MateTypeMapper` / `MateGeometryMatcher`

**用途**：把一条装配关系翻译成 SolidWorks 配合类型与对齐方式；把一个关系几何匹配到候选实体上。
纯逻辑，不碰 CAD。
**谁会用**：任何要复现或校核装配关系重建结果的工具。这是最内部的一块，
其它模块通常只需要读 `MateOutcome` 的统计，不必自己调这两个类。

- `MateTypeMapper.Map(relation)` → `MatePlan { Kind, MateType, Align, Distance, Reason }`。
  `MatePlanKind` 四态：`Mate` / `Fix`（接地，落为固定组件）/ `SkipSuppressed` / `Unsupported`。
- 只认实测过的接口名：SE 侧 `GroundRelation3d` / `AxialRelation3d` / `PlanarRelation3d`；
  SW 侧 `SwFixedComponent` / `SwCoincident` / `SwConcentric` / `SwDistance`。
  **没实测过的类型一律 `Unsupported` 并把接口名如实带进报告**（SW 侧未知类型形如 `SwMateType<n>`），
  据此决定下一版补哪个，而不是现在凭猜测写一行映射。
- `MateGeometryMatcher.Match(geometry, candidates)` → `MateMatch`，
  `MateMatchStatus` 直接映射到 `ConversionErrorClass`（未匹配 / 多义）。
  容差是公开常量：`PositionTolerance = 1e-6`、`DirectionTolerance = 1e-9`、
  `MateTypeMapper.OffsetEpsilon = 1e-9`。

`MateOutcome.IsSelfConsistent` 是判据：每条关系都必须有确定去向，不允许凭空消失。

### A-11 Worker 协议与全部 DTO —— `WorkerProtocol` + 各 record

**用途**：自己拉起 `HistoryMinerva.Worker.exe`，或复现/校验请求 JSON。
**谁会用**：需要绕开 UI、在自己的批处理里跑转换的模块或脚本。

```text
HistoryMinerva.Worker.exe <动词> <请求JSON路径> --cancel <取消信号文件>
```

| 动词常量 | 值 | 请求体 | 用途 |
| --- | --- | --- | --- |
| `PartsRequestVerb` | `--request` | `BatchRequest` | 零件批次 |
| `PartImportVerb` | `--import-part` | `PartImportRequest` | 单件导入（隔离 FeatureWorks 的 COM 生命周期） |
| `AssemblyProbeVerb` | `--probe-assembly` | `AssemblyProbeRequest` → `AssemblyProbeResult` 落到 `ResultPath` | 只读装配探查 |
| `AssemblyBuildVerb` | `--assembly` | `AssemblyBatchRequest` | 装配构建 |
| `AssemblyRenameVerb` | `--rename-assembly` | `AssemblyRenameRequest` | 属性整备（改名 + 写属性），只接受 SolidWorks |
| `AssemblyPackageVerb` | `--package-assembly` | `PackageRequest` | 整体打包的 STEP/DWG/PDF 导出 |

`WorkerProtocol.CreateJsonOptions()` 是**唯一正确的序列化配置**（camelCase、大小写不敏感）；
`IsKnownVerb(v)` 判动词。**不要自己 new `JsonSerializerOptions`**——
枚举没有装 `JsonStringEnumConverter`，全部按数值过线，配置一错父子进程就对不上号。

**退出码**：`0` 全部成功、`1` 部分失败、`3` 取消、`4` 整批失败。
**部分失败不判整批失败**，逐条成败按事件回报。

进度以 `WorkerEvent` 逐条回流，其中 `Stage`（`ConversionStage`）、
`ErrorClass`（`ConversionErrorClass`）、以及 `Feature` / `Assembly` / `Mate` 三个可空结果块
是消费方真正要解析的部分。

`PackageRequest` 示例：

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

`artifact` 是 `PackageArtifact`（按数值序列化）：`0=Step`、`1=Dwg`、`2=Pdf`。
**同一个零件的三个作业共用一个 `id`**——表里它们本来就是同一行。
`Overwrite` **缺省 `true`**：打包要的是一份与当前模型一致的交付包，
留着上一轮的旧 STEP 比缺一个文件更危险。这与转换管线默认不覆盖是两件事——
那边保护的是用户手工整备过的产物，这边的产物本来就由本模块整批生成。

PDF 走 `GetExportFileData(swExportPdfData)` + `SetSheets(swExportData_ExportAllSheets)`，
否则多页工程图只导当前页。

**源格式**：四个转换请求（`BatchRequest` / `PartImportRequest` / `AssemblyProbeRequest` /
`AssemblyBatchRequest`）末尾都有可选 `sourceFormat`（`0 = SolidEdge` 缺省、`1 = SolidWorks`）。
**不带该字段的历史请求仍按 Solid Edge 执行。** 取 SolidWorks 时先严格导出交付用 `.x_t` 再导入识别，
`ConversionJob.XtPath` 必填且指向 `XT/` 下的中转件，产物落在 `SW/` 且不得与源文件同路径。
`AssemblyRenameRequest` 只接受 `SourceFormat = SolidWorks`，且不产生 `XT/` `SW/` 输出目录。

**探查结果里三个可空列表的 `null` 语义（务必分清）**：

| 字段 | `null` 的含义 | **不是**什么意思 |
| --- | --- | --- |
| `Documents` | V3.0 格式的旧结果，只能展平 | 没有子装配 |
| `PartProperties` | 本次没有读属性（Solid Edge 源，或读属性整个失败） | 零件上没有值 |
| `PurchasedParts` | 本次没有采集（Solid Edge 源） | 这台设备没有外购件 |

`PartPropertyReading` 走装配里已经载入的组件文档读，不为此另开零件；
材料取 `GetMaterialPropertyName2` 的**名字与材料库**（只有名字的材质应用不回零件）。
任何一格读不到就留空串，不把探查判为失败。消费方按 `sourcePath` 对齐即可当预填值。

`PurchasedParts` **只收边界那一层**：从总装往下第一个离开装配体目录的组件即为一种外购货，
它底下的后代一概不出现在 `PurchasedParts`，也不出现在 `Occurrences`。
消费方因此可以假定**这张表上一行 = 一种可下单的货**，而不是一堆拆开的零件。

---

## 4. 面 B —— 命令总线

### 4.0 调用方式与前提

```csharp
var result = await context.Bus.ExecuteAsync(
    "minerva.conversion.probe", command.Source, command.Cancellation);
if (!result.Success)
    throw new InvalidOperationException(result.Message);
```

命令名忽略大小写。消费方**不得**构造 Minerva 服务、引用 `HistoryMinerva.dll` 的内部类型，
或自行加载 Minerva DLL；通过自己的宿主上下文取得同一个 `CommandBus` 按命令名调用。

**不要在 `Attach` 里调本模块任何命令**——那一刻宿主目录必然不完整。
登记一条 `<你的域>.host.ready`，在整轮装载完成后再调（见 HistoryVulcan 模块 API §2.1）。

### 4.1 两半：无状态的 `plan.*` 与有状态的其余

**`minerva.plan.package` / `minerva.plan.rename` 不在下面这套规矩里**：它们入参就是路径，
不读也不写页面状态，可以并发调、可以重复调、返回强类型对象。跨模块消费从它们开始。

其余 `minerva.*` 命令绑在**页面当前选择**上，不接受「对这个路径做这件事」的一次性调用。
一次完整调用是三步，顺序固定：

```csharp
// 1. 先选转换内容（决定来源是文件还是文件夹、走哪条管线）
await bus.ExecuteAsync("minerva.ui.content content=SolidWorksAssemblyPackage", src, ct);
// 2. 再设来源
await bus.ExecuteAsync(@"minerva.ui.source path=D:\设备\GHLSS-06-00 总装.SLDASM", src, ct);
// 3. 解析，然后执行
await bus.ExecuteAsync("minerva.conversion.probe", src, ct);
await bus.ExecuteAsync("minerva.conversion.run", src, ct);
```

`probe` / `run` / `source` / `picksource` 都接受可选参数 `content=`，**先套用它再干活**——
这是为了让页面下拉与命令在同一拍里对齐，不是一个可以替代第 1 步的独立入参。

**这带来两个消费方必须知道的后果**（只对这一半成立）：

1. **没有并发。** 同一时刻只有一份页面状态。两个消费方同时调，第二个会覆盖第一个的来源。
2. **没有幂等。** 同一条命令在不同页面状态下做的是不同的事。

要并发、要幂等、要「对这个路径做这件事」，用 `minerva.plan.*`。

### 4.2 `CommandResult.Data` 里有什么

| 命令 | `Message` | `Data` |
| --- | --- | --- |
| `minerva.plan.package` | 计划摘要（机加件/外购件种数、待导产物数） | **`PackagePlan`** |
| `minerva.plan.rename` | 计划摘要（前缀、编号数、待改名数） | **`AssemblyRenamePlan`** |
| `minerva.conversion.probe` | 解析结论文本 | **`AssemblyProbeResult`** |
| `minerva.conversion.run` | 转换结论文本 | 属性整备 **`AssemblyRenamePlan`**、整体打包 **`PackagePlan`**、其余三种 `null` |
| `minerva.conversion.cancel` | 是否有可取消的操作 | 无 |
| `minerva.ui.content` / `.source` / `.options` / `.property` / `.cell` | 动作结论文本 | 无 |
| `minerva.ui.picksource` | `已选择来源` / `已取消选择` | 选中的路径字符串 |
| `minerva.ui.data` | 视图名 | `IReadOnlyList<IReadOnlyDictionary<string,string>>` |
| `minerva.ui.describe` / `.actions` | 页面 JSON 文本 | 无 |
| `minerva.worker.*` | 状态文本 | 无 |

`Data` 里的类型就是 `HistoryMinerva.Contracts` 已经公开的那几个 record：消费方引用同一份
合同程序集（面 A）即可强转，不必解析中文文本。跨宿主 HTTP 边界时它表现为 `JsonElement`，
按同一份 record 的字段名反序列化即可。

**失败的命令不带 `Data`。** 消费方可以拿「`Success` 且 `Data` 非空」当作「这一轮真的拿到了事实」。

`minerva.ui.data` 仍然是 Aurora 的内部取数协议（带 `HiddenReason`），返回的是**显示态字符串**，
列的含义随转换内容变。**不要拿它当数据源**——要数据就用 `minerva.plan.*` 或 `conversion.probe`。

### 4.3 命令逐条

#### `minerva.plan.package` — 按路径出整体打包计划（4.10.2 新增）
- **参数**：`path=`（必填，绝对路径的 `.SLDASM`）
- **只读 / 不占 UI 线程 / 不隐藏 → MCP 可见 / 无状态**
- **做什么**：只读走一遍装配树，然后 `PackagePlanner.Create`。**一个文件都不改、一个目录都不建。**
- **`Data`**：`PackagePlan`——机加件表、外购件表、数量、待导 STEP、有工程图的零件、
  四个输出目录、两张 BOM 的文件名、阻断项与警告。字段含义见 A-4。
- **失败**：`path` 缺失 / 不是绝对路径 / 不是 `.SLDASM` / 文件不存在 / Worker 或
  SolidWorks COM 不可用 / 解析被取消。**四道路径与环境校验都排在启动 Worker 之前**——
  路径写错不该先开一次 SolidWorks 再说不行。
- **不做**：不改页面选择，也不读页面选择。页面上正在整备别的装配体时调它，两边互不干扰。
- 探查要开 SolidWorks 走完整棵装配树，几百个零件要几分钟。它是只读的，但不是廉价的。

```
minerva.plan.package path=D:\设备\GHLSS-06-00 总装.SLDASM
```

#### `minerva.plan.rename` — 按路径出改名计划（4.10.2 新增）
- **参数**：`path=`（必填），`prefix=`（可选），`clearnumber=`（可选）
- **只读 / 不占 UI 线程 / 不隐藏 → MCP 可见 / 无状态**
- **做什么**：只读走一遍装配树，然后 `PropertyPrepPlanner.Create`。**不改任何文件名、不写任何属性。**
  真要动文件仍然走页面那条 `minerva.conversion.run`——写入需要用户在场，
  不该被另一个模块顺手调掉。
- **`Data`**：`AssemblyRenamePlan`——逐条 `RenameEntry`（源路径、目标路径、图号、
  是否编号、层级、名称）、未编号清单、阻断项与警告。字段含义见 A-3。
- **`prefix` 省略时按根装配文件名推断**（`DrawingNumber.InferPrefix`）：
  `GHLSS-06-00 总装.SLDASM` → `GHLSS-06`。猜错了传一个 `prefix=` 就是了；
  而猜「没有前缀」会让整份计划看起来像要删图号。
- **`clearnumber=true` 就是空前缀那一份计划**，即删图号，此时忽略 `prefix`。
  它不是第二条管线——空前缀与 `ZS-LHL` 走的是同一个 `PropertyPrepPlanner.Create`，
  只是值不同（DEC-057）。真值判定与 `ui.options` 同一套：只认 `开启` / `开` / `true` / `1`。
- **失败**：同 `plan.package`，另加前缀非法（带空格或文件名非法字符）。

```
minerva.plan.rename path=D:\设备\GHLSS-06-00 总装.SLDASM prefix=ZS-LHL
minerva.plan.rename path=D:\设备\GHLSS-06-00 总装.SLDASM clearnumber=true
```

#### `minerva.conversion.probe` — 解析装配体
- **参数**：`content=`（可选，先套用页面转换内容）
- **只读 / UI 线程 / MCP 可见**
- **做什么**：只读走一遍装配树；解析出零件行；SolidWorks 源同时读回每个零件的
  材料 / 材料库 / 表面处理 / 热处理，以及子文件夹外购件的路径与实例数；
  解析完立刻刷新属性整备表格。
- **只收录与装配体同级的文件**，跳过子文件夹外购件（它们只进 `PurchasedParts` 统计）。
- **`Data`**：`AssemblyProbeResult`（4.10.2 起）。失败时不带 `Data`。
- **失败**：`CanProbe` 为假时 `Fail(StatusText)`；取消时 `Fail("Minerva 解析已取消。")`。
- **不做**：不写任何交付文件。中转结果 JSON 落在 `probes/` 但**在 `finally` 里删掉**，
  消费方不要去那里捞（见 4.5）。
- 它解析的是**页面当前选中**的那一个。要指定路径，用 `minerva.plan.*`。

#### `minerva.conversion.run` — 执行当前转换
- **参数**：`content=`（可选）
- **写入 / UI 线程 / MCP 不可见**
- **做什么**：按当前转换内容执行五种之一。
  - 属性整备：改名 + 写零件属性（图号前缀为空即删图号），改完自动重建索引（A-9）。
  - 整体打包：生成 `STP/` `DWG/` `PDF/` `BOM/` 四个目录，两张 BOM 由前端直接写。
  - 其余三种：按对应管线转换。
- **`Data`**（4.10.2 起）：属性整备 `AssemblyRenamePlan`、整体打包 `PackagePlan`，
  其余三种转换没有对应的公开计划类型，为 `null`。失败时不带 `Data`。
- **失败**：`CanConvert` 为假时按模式返回 `RenameBlockedReason` / `PackBlockedReason` / `StatusText`。
- 返回文本取 `ResultText` 而不是 `StatusText`——后者排在 UI 队列里，这一刻很可能还是
  那句「正在写入……」。

#### `minerva.conversion.cancel` — 取消
- UI 线程。没有进行中的操作时 `Fail("Minerva 当前没有可取消的操作。")`；
  页面还没创建时 `Fail("Minerva 页面尚未创建。")`。

#### `minerva.ui.content` — 选转换内容
- **参数**：`content=`（必填）。三种写法都收：
  - 显示名：`SolidWorks .SLDASM → 整体打包（STP/DWG/PDF/BOM）`
  - 枚举名：`SolidWorksAssemblyPackage`（**推荐消费方用这个**，显示名会随界面改）
  - 序号：`0`–`4`
- 五个枚举名：`SolidEdgePartToSolidWorksPart`、`SolidEdgeAssemblyToSolidWorksAssembly`、
  `SolidWorksAssemblyToSolidWorksAssembly`、`SolidWorksAssemblyPropertyPrep`、
  `SolidWorksAssemblyPackage`。只有第一种收「零件文件夹」，其余四种都收单个装配体文件。
- 缺 `content` 或认不出时 `Fail`。

#### `minerva.ui.source` — 设来源
- **参数**：`path=`（必填），`content=`（可选，先套用）
- **⚠ 这条命令失败时也返回 `Success = true`**，消息形如 `未更改来源：…` / `来源路径为空，未更改。`
  这是刻意的——失败指令会让宿主抢控制台并重排停靠（REQ-002）。
  **消费方不能只看 `Success`**，要么检查 `Message` 前缀，要么随后用 `minerva.ui.data view=parts`
  验证状态。`picksource`、`cell`、`property` 的取消路径同理。
- 形态不符（例如属性整备只收 `.SLDASM`）时不失败、不改来源、不解析。

#### `minerva.ui.options` — 设选项
- **参数**：`option=recognize|continue|mates|prefix`，`value=`
- `prefix` 允许空值且不失败（＝删图号）；改 `prefix` 会顺带刷新表格，
  因为「文件」列显示的就是改完之后的名字。
- **`value` 的真值判定只认 `开启` / `开` / `true` / `1`（不区分大小写），其余一律为假。**
  写 `value=yes` 会静默变成关闭。

#### `minerva.ui.data` — 取页面数据（隐藏）
- **参数**：`view=parts|content|materials|surfaces|heats`
- `parts` 每行的列：`id`（隐藏行身份）、`file`、`drawing`、`name`、`status`、`detail`、
  `features`、`sketches`、`material`、`surface`、`heat`、`quantity`、`hasdrawing`、`category`。
- 属性整备下 `file` 给的是**改完之后**的文件名；打包模式下 `drawing` 一列对机加件是**图号**、
  对外购件是**规格**——两者在各自那张 BOM 上占同一格。
- 三个属性列在「会写属性但还没填」时渲染成占位符 `—` 而不是空串
  （Aurora 的 `cellAction` 不触发空单元格，留空就等于这三列永远点不开）；
  不写属性的装配体行留真空。**消费方解析时要把 `—` 当空值。**
- `materials` / `surfaces` / `heats` 是三个属性下拉的候选，行只有 `value` 一列，
  首项恒为 `（不写）`。材料候选取本机 SolidWorks 收藏材质，另两个取属性标签模板。
- 未知 `view` 时 `Fail`。

#### `minerva.ui.property` — 批量刷一个属性槽（隐藏）
- **参数**：`field=designer|date|material|surface|heat`，`value=`
- `date` 取系统当日，忽略 `value`。
- 返回**改了几行**而不是空口说成功：「刷了 0 行」与「刷完了」必须能分开。
- 计划还没建起来时返回 `请先解析装配体，…`（`Success = true`）。
- `value` 为 `（不写）` 或空串时**什么都不做**——它是显示态「这一列没有共同值」，不是一个动作。
  要清空某一行，用 `minerva.ui.cell` 点那一行。

#### `minerva.ui.cell` — 改一行（隐藏）
- **参数**：`field=name|material|surface|heat`，`id=`（`ui.data view=parts` 里那个隐藏 `id`）
- `material|surface|heat` 经 `aurora.ui.dialog kind=choice` 在候选里选；
  `name` 经 `kind=prompt` 改自由文本，预填当前值。
- 这一行不写属性（装配体或未编号内部件）时返回 `Success = true` + 说明，不改任何东西。
- 用户取消时同样 `Success = true`。

#### `minerva.ui.picksource` / `.describe` / `.actions`（隐藏）
- `picksource`：装配体四项开文件对话框，零件项开文件夹对话框；`Data` 是选中路径。
- `describe` / `actions`：Aurora 建页与动作声明用的 JSON。
  **页面由 Aurora 根据 `describe` 统一创建**；Minerva 不注册 `minerva.ui.pane`，
  也不返回 `System.Windows.UIElement`；页面按钮只绑定 `actions` 里的稳定动作 ID。

#### `minerva.worker.*` — Worker 与工作区状态（MCP 可见）

| 命令 | 返回 |
| --- | --- |
| `minerva.worker.status` | `Worker 就绪 (路径)` 或 `未找到 HistoryMinerva.Worker.exe，请重新部署模块。` |
| `minerva.worker.path` | 宿主上下文解析出的实际 Worker 路径 |
| `minerva.worker.capabilities` | `worker.protocol=assembly-probe,batch-convert,part-convert,assembly-rename,assembly-package,cancel; runtime=single-process-worker` |
| `minerva.worker.show` / `.hide` | 只读报告中央工作区承载方式，**不创建也不隐藏任何窗口** |

### 4.4 进度事件怎么收

Worker 事件通过命令上下文的 `Progress` 进入 Vulcan `cmd:progress:minerva:conversion`
日志与控制台；最终结论由同一命令总线回显。
**入口命令不再抢先报一句进行时**——真正的进行时由操作自己在知道自己是什么之后报（DEC-060）。
消费方要逐条 `WorkerEvent`，只能走面 A 自己跑 Worker（A-11）；总线上拿到的是日志文本。

### 4.5 数据目录不是消费面

`%AppData%\HistoryVulcan\HistoryMinerva\`：`requests/` 放 Worker 请求、`probes/` 放探查结果。
**两者都是运行态中间文件**，探查结果在 `finally` 里连同 `.tmp` 一起删掉。
不要去那里读数据、不要在那里放东西。

---

## 5. 面 C —— AI / MCP

`mcpExposure = readonly`。投影过来的只有 `Readonly && !Hidden` 的命令，4.10.2 起是八条：

| MCP 工具 | AI 能问出什么 |
| --- | --- |
| **`minerva_plan_package`** | **给一个装配体路径，拿回完整打包计划**：有哪些机加件、哪些外购件、各几个、哪些有工程图、两张 BOM 叫什么 |
| **`minerva_plan_rename`** | **给一个装配体路径（可带前缀），拿回完整改名计划**：每个文件现在叫什么、按这个前缀会叫什么、哪些不编号 |
| `minerva_conversion_probe` | 解析**当前页面已选**的装配体，`Data` 里带回 `AssemblyProbeResult` |
| `minerva_worker_status` | Worker 在不在 |
| `minerva_worker_path` | Worker 在哪 |
| `minerva_worker_capabilities` | Worker 支持哪些协议动词 |
| `minerva_worker_show` / `minerva_worker_hide` | 工作区怎么承载的 |

两条 `plan.*` 是**无状态**的：AI 直接给路径，不需要用户先在页面上选好，也不会改动
用户此刻的页面。它们只读，但要开 SolidWorks 走完整棵装配树，几百个零件要几分钟——
是只读的，不是廉价的，别拿它当随手一问。

**AI 仍然问不出来的**（这是实话，不是省略）：

- 上一次转换失败在哪一个零件、什么错误类 —— `WorkerEvent` 不进总线返回值（G-6）。
- 写盘类动作一概不投影：改名、写属性、导出、打包都要用户在页面上按（这是刻意的）。

---

## 6. 本模块明确不提供什么

写清楚边界，消费方才不会去等一个不会来的东西。

- **不提供 NuGet 包。** 只提供 `z-Publish` 版本化快照里的 DLL 引用。
- **不提供无状态的写入命令。** `minerva.plan.*` 无状态但只读；一切写盘仍然绑页面状态、
  由用户在页面上按下（4.1）。别的模块能算出计划，但不能替用户执行它。
- **不提供 SWuse 建模 API。** 历史的 `--request <json>`（Roslyn 编译 + C# 零件构建）双参数形态已删除，不再路由。
- **不注册 WPF 页面。** 不注册 `minerva.ui.pane`，不返回 `UIElement`，不 `new Window`——
  弹窗一律走 `aurora.ui.dialog`。
- **不维护第二套主题。** 页面用 `Aurora.Brush.*` 动态资源跟随宿主。
- **不做工程图的工作副本。** 复制到别的目录会断掉它对零件的引用，导出来的是空图。
- **不递归找同名工程图。** 只认「与零件同名、同目录」。
- **不给外购件导 STEP。** 标准件按规格供货，Toolbox 螺钉螺母一批上百个逐个导只是把打包拖慢。
- **不在 BOM 里重新编号。** BOM 描述的是此刻磁盘上的事实。
- **不判断源零件有没有特征树。** 每个 `.SLDPRT` 都先严格导出 `.x_t` 再做 FeatureWorks，
  已有 `SW/` 产物一律重做。Smoke 禁止生产代码出现「跳过整备」或「源零件已有特征树」；
  真机门禁要求每个零件都留下非空 XT。
- **不把源文档改成可写。** 打包全程只读：优先复用会话里已打开的文档，
  自己打开的一律 Silent + ReadOnly 并在关闭后比对 SHA256。

---

## 7. 面 A 与面 B 的选择建议

| 你要做的事 | 走哪个面 |
| --- | --- |
| 知道一份装配的物料事实（零件、数量、外购件、图号） | **B**：`minerva.plan.package`，读 `Data` |
| 预览一次改名会产生什么 | **B**：`minerva.plan.rename`，读 `Data` |
| 上面两件事，但你已经有一份 `AssemblyProbeResult` 了 | **A**：`PackagePlanner.Create` / `PropertyPrepPlanner.Create`，别再开一次 SolidWorks |
| 上面两件事，但不想依赖 Minerva 在场 | **A**：自己跑 `--probe-assembly`（A-11）再算 |
| 复现 Minerva 的命名/编号/分类规则 | **A**：`DrawingNumber` / `PurchasedPartNaming` / `IsOutsideAssemblyDirectory` |
| 真的去开 SolidWorks 改文件、导 STEP | **B**：先 `ui.content` + `ui.source`，再 `conversion.run`——这一条必须有用户在场 |
| 在自己页面里嵌一段 Minerva 的表 | **B**：`ui.data view=parts`，但要接受它是显示态 |
| 让 AI 回答关于某台设备的问题 | **C**：`minerva_plan_package` / `minerva_plan_rename` |

**一句话**：面 A 是 Minerva 的可复用资产，`minerva.plan.*` 是它对外的只读窗口，
其余面 B 是页面的遥控器。窗口随便看，遥控器只有一个、而且必须先按频道再按播放。

---

## 8. 兼容与变更规则

1. **枚举按数值序列化，新值只能追加在末尾。** `WorkerProtocol` 没有装
   `JsonStringEnumConverter`，插在中间会让父子 Worker 对不上号。
   涉及 `ConversionStage`、`ConversionErrorClass`、`ConversionArtifactKind`、
   `ConversionSourceFormat`、`PackageArtifact`、`PackagePartCategory`。
2. **record 的新字段只追加在末尾，且带缺省值。** 不带该字段的历史请求 JSON 语义必须不变——
   `sourceFormat` 缺省 `SolidEdge` 就是这条规则的样板。
3. **可空列表的 `null` 是「本次没采集」，永远不是「没有」。** 新增可空字段时必须同时写清这一条。
4. **同一件事只允许一个判据。** 自制/外购只有 A-7，图号切分只有 A-1，外购件切分只有 A-5。
   要加第二套规则，先删掉第一套。
5. **删公开面走主版本规则**，与宿主 5.3.0 的做法一致：不能据「接入面冻结」推断历史接口仍存在。
6. **新增命令只加不改。** `minerva.plan.*` 是 4.10.2 追加的新命令类，
   既有 `conversion.*` / `ui.*` / `worker.*` 的名称、参数与行为逐字未动；
   `CommandResult.Data` 从 `null` 变成有值是**只增不减**的兼容变化——
   原来就只读 `Message` 的消费方不受影响。
