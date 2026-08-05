# Opus 5 答复：Solid Edge 2020 `.par -> .x_t` COM 导出验证报告

回应 [03-给Opus5的SolidEdge-COM问题单.md](03-给Opus5的SolidEdge-COM问题单.md)。

- 验证日期：2026-07-27
- 验证机：Windows 10 IoT Enterprise LTSC 2021 (19044)，x64
- Solid Edge：2020，`SEInstallData.GetVersion()` = `220.00.00.104 x64`，
  `GetParasolidVersion()` = `31.1`
- 样件：`b-Code-SE2SW\1-蜀牛低型烧杯150ml.par`（306,176 B，
  SHA-256 `515848B9…FBD5CF`，`Models.Count = 1`）
- 探针：[tools/SolidEdgeExportProbe](../tools/SolidEdgeExportProbe)
- 原始 JSON 结果：本次运行落在会话临时目录，关键字段已抄录进下文表格

**证据分级**：本文每条结论都标注来源。`[实测]` = 本机跑出来的；`[SDK]` = `sesdk.chm` 中的原文主题；
`[样例]` = Siemens 安装目录里的官方样例源码；`[未验证]` = 没有证据闭环，已列入风险表。

---

## 1. 结论摘要

1. `.par -> .x_t` 通路成立且稳定。10 次连续转换 10/10 成功，无残留 Solid Edge 进程，
   源文件哈希不变，单次端到端约 6.9–7.3 秒（其中启动 Solid Edge 约 5.0 秒，实际导出 5–22 毫秒）。
2. **推荐用 `SolidEdgePart.PartDocument.SaveBody(...)`，而不是官方 Batch 样例的
   `SolidEdgeDocument.SaveAs(...)`。** `SaveAs` 的所有可选参数在 SDK 中被明确写成
   "Not currently supported"，无法指定文本/二进制和 Parasolid 版本；`SaveBody` 三者都能指定，
   还能选择导出哪几个 Model（多实体控制点）。
3. 文本 Parasolid：`SaveAs` 靠扩展名（`.x_t` 文本 / `.x_b` 二进制，已实测）；`SaveBody` 靠
   `SaveBodyConstants` 显式指定，与扩展名无关。**判定依据应是文件头的 `FORMAT=` 字段，不是扩展名。**
4. Parasolid 版本只有两种选择：当前版本（31.1）或降到 ≤13.0。`ParasolidVersionConstants`
   最高只到 `seParasolidVersion130`，中间版本（如 20/26）无法指定。首版应使用当前版本。
5. `dotnet build` **无法**构建带 `<COMReference>` 的工程（MSB4803）。必须用 Visual Studio 的
   `MSBuild.exe`。这是本次最影响工程结构的硬约束。
6. Siemens 在 SDK 中明确写明：Program 目录下的 interop 程序集"仅供内部应用使用"，
   客户必须自己生成 interop。所以不能引用 `Program\interop.SolidEdge*Lib.dll`。
7. 用户已有的 Solid Edge 会话不会被复用。本机在用户会话（PID 29216）常驻的情况下，
   `CoCreateInstance` 每次都新建了独立的 `Edge.exe`；探针只退出自己创建的那个 PID，
   全程约 25 次运行，用户会话始终存活。
8. 多实体、曲面体、空零件、无许可环境这四类场景**没有**在本次验证中闭环，已列入风险表。

---

## 2. 逐题答复

### 2.1 推荐导出 API

#### Q1.1 `Document.SaveAs(target.x_t)` 是否是 Siemens 推荐且完整的 COM 路径？

**是"可用且官方在用"，但不是"完整"。**

- `[样例]` `C:\Program Files\Siemens\Solid Edge 2020\Custom\Batch\Batch_frm.vb:488`
  在 `optConvertTo` 分支直接 `objDoc.SaveAs(NewFilename)`，格式列表在 `:409` 把 Parasolid 写成
  `x_t`。这证明 Siemens 自己用这条路。
- `[SDK]` 主题 `SolidEdgeFramework~SolidEdgeDocument~SaveAs.html`：
  - Description："Saves the referenced document to a new name, directory, or format."
  - 9 个参数中，`NewName` 之外 **8 个参数的说明都是 "Not currently supported"**，
    唯一例外是 `FileFormat`，而它的说明写死了只对 PDF 有意义
    （"When saving as PDF, a value of True saves a 3D PDF…This argument is unsupported for other file types."）。

结论：`SaveAs` 能出文件，但**没有任何 Parasolid 相关的可调参数**。作为生产化路径不完整。

#### Q1.2 是否存在更适合无人值守批处理的专用 Parasolid 导出接口？

**存在。**

```
SolidEdgePart.PartDocument.SaveBody(
    FileName          As String,        ' 目标文件名
    NewDocumentType   As Variant,       ' SolidEdgeConstants.SaveBodyConstants
    ParasolidVersion  As Variant,       ' SolidEdgeConstants.ParasolidVersionConstants
    NumModelsToBeSaved As Variant,      ' 要导出的 Model 数
    ModelsToBeSaved   As Variant)       ' 要导出的 Model 名称
```

- `[SDK]` 主题 `SolidEdgePart~PartDocument~SaveBody.html`：
  Description "Saves the input bodies of the input models into the given format"，
  上述 VB 签名与参数说明逐条对应。
- `[SDK]` 主题 `SolidEdgeConstants~SaveBodyConstants.html`（"Format in which the Model is to be saved in"）：

  | 成员 | 值 | 说明 |
  |---|---:|---|
  | `seSaveBodyAsPartDocument` | 0 | Save the model in SolidEdge Part Document |
  | `seSaveBodyAsSheetMetalDocument` | 1 | Save the model in SolidEdge SheetMetal Document |
  | `seSaveBodyAsParasolidText` | 2 | Save the model as a Parasolid Text file |
  | `seSaveBodyAsParasolidBinary` | 3 | Save the model as a Parasolid Binary file |

- `[SDK]` 主题 `SolidEdgeConstants~ParasolidVersionConstants.html`（"Parasolid Versions Supported"）：
  `seParasolidCurrentVersion=0`、`70/71/80/90/91/100/101/110/111/120/121/130`。

**类型库归属**（构建时按 GUID 从注册表解析，不用装 SDK 也不用硬编码路径）：

| 类型库 | 文件 | TypeLib GUID | 来源 |
|---|---|---|---|
| `SolidEdgeFramework` | `Program\framewrk.tlb` | `{8A7EFA3A-F000-11D1-BDFC-080036B4D502}` | `[样例]` `Custom\Batch\Batch.vbproj:171` |
| `SolidEdgeConstants` | `Program\constant.tlb` | `{C467A6F5-27ED-11D2-BE30-080036B4D502}` | `[样例]` `Custom\Batch\Batch.vbproj:153` |
| `SolidEdgePart` | `Program\Part.tlb` | `{8A7EFA42-F000-11D1-BDFC-080036B4D502}` | `[样例]` `Custom\DynAttrib\DynAttrib.vbproj:139` |
| `SEInstallDataLib` | `Program\InstallData.tlb` | `{42E04299-18A0-11D5-BBB2-00C04F79BEA5}` | `[样例]` `Custom\OpenSave\*.vbproj` |

**相对 `SaveAs` 的优势**（全部实测过）：

| 能力 | `SaveAs` | `SaveBody` |
|---|---|---|
| 文本/二进制 | 只能靠扩展名推断 | `NewDocumentType` 显式指定 |
| Parasolid 版本 | 不可控（永远当前版本） | `ParasolidVersion` 显式指定 |
| 只导出部分实体 | 不支持 | `NumModelsToBeSaved` / `ModelsToBeSaved` |
| 语义 | 文档级"另存为" | 实体级"导出几何" |

同名方法也存在于 `Model`、`SheetMetalDocument`、`WeldmentDocument`、`ConstructionModel`、
`FlatPatternModel`、`SimplifiedModel`、`WeldBeadModel`、`WeldPartModel`
（`[SDK]` CHM 目录中有对应 `SolidEdgePart~*~SaveBody.html` 主题），首版只用 `PartDocument` 这一条。

#### Q1.3 导出后文档身份、脏状态是否改变？

`[实测]` 对 `.x_t` / `.x_b` 目标，两种方法都**不改变**文档身份：

| 方法 | 导出前 FullName | 导出后 FullName | 导出后 Dirty | Documents.Count |
|---|---|---|---|---|
| `SaveBody` | `…\beaker150.par` | `…\beaker150.par`（未变） | False | 1 |
| `SaveAs` | `…\beaker150.par` | `…\beaker150.par`（未变） | False | 1 |

但 `[SDK]` `SaveAs` 主题的 Remarks 明确警告了另一种情形：

> "When used to copy a source document to a new file name, SaveAs assigns property set values to
> the new document to give the document a unique identity, changes the name of the document, and
> updates the window titles and most recently used menu items."

即"另存为原生格式"会改身份，"导出为 Parasolid"实测不会。

**关闭策略建议**：不要依赖这个差异。无论用哪种方法，导出后一律 `Close(False)`，
再 `DoIdle()`，然后按固定顺序释放 RCW。探针就是这么做的，且每次都核对源文件 SHA-256 未变。

---

### 2.2 Parasolid 输出参数

#### Q2.1 如何明确选择文本 Parasolid？只靠 `.x_t` 扩展名够不够？

- 用 `SaveAs`：**扩展名就是唯一开关，且确实生效**。`[实测]`

  | 方法 | 目标扩展名 | 头部 `FORMAT=` | 字节数 |
  |---|---|---|---|
  | `SaveAs` | `.x_t` | `text` | 10,612 |
  | `SaveAs` | `.x_b` | `binary` | 9,180 |
  | `SaveBody` + `seSaveBodyAsParasolidText` | `.x_t` | `text` | 10,614 |
  | `SaveBody` + `seSaveBodyAsParasolidBinary` | `.x_b` | `binary` | 9,180 |

- 用 `SaveBody`：由 `NewDocumentType` 决定，扩展名不参与判断。这是无歧义的写法，推荐首版采用。
- **验收判据**：读文件头的 `FORMAT=` 字段（探针已实现），不要用扩展名当证据。

#### Q2.2 如何控制 Parasolid 版本？2020 默认导出哪个版本？

- 默认（`seParasolidCurrentVersion`，以及 `SaveAs` 的唯一行为）= **Parasolid 31.1**。`[实测]`
  - `SEInstallData.GetParasolidVersion()` 返回 `31.1`
  - 文件头 `TRANSMIT FILE created by modeller version 310125523 SCH_3101255_31100_1300`
- 降版通过 `SaveBody` 的 `ParasolidVersion` 参数，`[实测]` 生效：

  | 参数 | 文件头 schema | 字节数 |
  |---|---|---|
  | `0`（当前） | `SCH_3101255_31100_1300` | 10,614 |
  | `130` | `SCH_1300000_130060` | 10,052 |
  | `100` | `SCH_1000000_100040` | 9,983 |

- **限制**：枚举最高只到 `seParasolidVersion130`（Parasolid 13.0）。无法要求 20.x / 26.x 等中间版本。
  也就是说，可选项实际只有"当前 31.1"和"老到 ≤13.0"两档。
  本机 SolidWorks 2025 SP5 读 31.1 不成问题，首版**应使用默认当前版本**，不要降版。

⚠️ 解析提示：文件头里另有一处 `SCH_3101255_31100`（当前建模器的模式引用），与请求的导出版本无关。
只能用 `TRANSMIT FILE created by modeller version` 那一行判定版本。探针已按此实现。

#### Q2.3 版本/单位/容差设置来自哪里？

- **版本**：来自 `SaveBody` 的方法参数。`[实测 + SDK]`
- **不来自全局参数**：`[实测]` 遍历 `SolidEdgeConstants.ApplicationGlobalConstants` 全部成员，
  没有任何 Parasolid / 导出版本 / 文本二进制相关项（匹配 `Parasolid|Version|Text|Binary|Translat`
  的命中项全是 `TextProfile*`、`LookAheadVersion`、`PropertyTextError` 等无关项）。
- **单位与容差**：Parasolid 传输文件本身是 SI（米）+ 建模器自身容差，`SaveAs`/`SaveBody` 都没有
  暴露单位或容差参数。首版不提供该配置项。
- 结论：`.par -> .x_t` 阶段**不需要读写任何注册表或用户配置**。

#### Q2.4 如何保存并恢复被修改的会话级设置？

由于 Q2.3，本阶段唯一需要改的会话级设置是 `Application.DisplayAlerts`。探针的做法：

```csharp
originalDisplayAlerts = app.DisplayAlerts;   // 读旧值
app.DisplayAlerts = false;
// … finally 中 …
if (originalDisplayAlerts is bool alerts) { app.DisplayAlerts = alerts; }
```

`Visible` 只在"确认是自己新建的实例"时才设为 false；复用到用户会话时一个字都不改。

如果将来确实需要改全局参数，官方样例给出了配对写法：
`[样例]` `Batch_frm.vb:520` 在 `EndProc` 用
`objApp.Application.SetGlobalParameter(seApplicationGlobalSessionDraftOpenInactive, DftInactive)`
把之前读到的旧值写回。对应 API 是
`Application.GetGlobalParameter(ApplicationGlobalConstants, out object)` /
`SetGlobalParameter(ApplicationGlobalConstants, object)`。

#### Q2.5 多实体、曲面体、空零件的导出行为？会不会弹框？

**部分回答，核心未验证。**

- `[样例]` Siemens 自己在 `Batch_frm.vb:250-252` 留了这段注释和代码：

  > `'PR#9090402 - Massive translation with batch.exe, Many times process is stopped by a popup that warns about multiple bodies.`
  > `'Since it is a batch processing so disabling all kind of pop for all the supported format.`
  > `objApp.DisplayAlerts = False`

  这是"多实体确实会弹框，且 `DisplayAlerts=False` 是官方对策"的一手证据。

- `SaveBody` 的 `NumModelsToBeSaved` / `ModelsToBeSaved` 就是多实体的显式控制点：
  可以只导出指定的 Model，从根上避开"多实体如何合并"的歧义。
- `[实测]` 本次样件 `Models.Count = 1`，属于单实体实体零件。
- `[未验证]` 多实体 `.par`、纯曲面 `.par`、空零件 `.par` 的实际导出结果与弹框行为。
  手头没有这三类样件，也不应凭空断言。**已列入风险表，进入正式 UI 开发前必须补齐基准样件集。**

---

### 2.3 无人值守与错误处理

#### Q3.1 `DisplayAlerts = false` 能抑制什么、不能抑制什么？

`[SDK]` 主题 `SolidEdgeFramework~Application~DisplayAlerts.html`：
Description "Enables or disables the display of alerts and messages."
Remarks "If this property is True, the application displays alerts and messages
(often requiring user interaction)."

**能抑制**：
- Solid Edge 自身的告警/消息框，含多实体导出告警 `[样例 PR#9090402]`
- `[实测]` 打开损坏文件时不弹框，直接以 `0x80004005 E_FAIL` 从 `Documents.Open` 抛出

**不能抑制**（依据 SDK 措辞与官方样例的其他注释，属于推断，标注为需要观察项）：
- 非 Solid Edge 组件弹出的模态框。`[样例]` `Batch_frm.vb:241-243` 的
  `PR 7470844` 注释说明 `.psm` 处理时会打开 Excel 的 gage 表，需要额外靠关掉 DisplayAlerts 规避——
  说明第三方组件的窗口不在这个开关的保证范围内。
- 许可、崩溃恢复、Windows 系统级对话框。
- 首版只处理 `.par`，`.psm` 的 Excel 问题不在范围内。

**兜底**：不能把"没弹框"当成设计前提。工作进程必须有单文件超时（探针的 `--stage-timeout-ms`），
超时后由父进程隔离终止该工作进程，而不是等人来点。

#### Q3.2 除 `DoIdle()` 和 `IOleMessageFilter` 外还需要什么？

- **`DoIdle()` 必须调用，且有明确理由**。`[SDK]` 主题 `SolidEdgeFramework~Application~DoIdle.html` Remarks：

  > "One of the most important tasks that DoIdle() performs is to ensure that a SolidEdgeDocument
  > gets fully released after SolidEdgeDocument.Close is called… It is recommended that DoIdle()
  > be called after any document is closed if the client that closed the document is going to do
  > more processing before returning control to Solid Edge."

  这正好解释了"关闭文档后是否残留"的问题：`Close` 之后必须 `DoIdle`。
- **`IOleMessageFilter` 必须注册**。`[SDK]` 主题 `OleMessageFilterUsage.html` /
  `OleMessageFilterCS.html` / `OleMessageFilterVB.html`；`ConnectingToSolidEdge.html` 也明确
  "See Handling 'Application is Busy' and 'Call was Rejected By Callee' errors for information
  regarding the use of OleMessageFilter."
  `[实测]` 不是可选项：每次运行都有 1–6 次调用被拒绝并重试成功。
- **线程必须是 STA**（`[STAThread]`）。
- `[实测]` 控制台 STA 进程**不需要**额外的消息泵、事件等待或其他全局参数。
  25 次运行没有出现需要 `Application.DoEvents` 才能推进的情况。
  （官方 WinForms 样例里的 `System.Windows.Forms.Application.DoEvents()` 是为了让它自己的
  "停止"按钮响应，不是 COM 的要求。）

#### Q3.3 `SaveAs` / `SaveBody` 如何报告失败？

**只通过 COM 异常 / HRESULT。** 两者在类型库中都是 `void`（VB 的 `Sub`），
`[SDK]` 两个主题的签名都是 `Public Sub`，没有返回值、没有 out 参数、没有状态码、没有事件。

所以：**调用没抛异常 ≠ 导出成功**，必须回头检查输出文件。这也是问题单第 10 条要求的由来。

#### Q3.4 如何区分"导出完成""导出失败""COM 已返回但文件仍在写"？

探针的判定顺序（`VerifyOutput()`）：

| 观察 | 判定 |
|---|---|
| 调用抛 COM 异常 | 失败，按 HRESULT 分类 |
| 无异常但文件不存在 | 失败（`ExportFailed`） |
| 文件存在但长度为 0 | 失败（`OutputEmpty`） |
| 文件被独占（`IOException`）或长度还在变 | 仍在写入，继续轮询 |
| 连续 3 次采样（间隔 100 ms）长度不变且 > 0 | 写入稳定，判成功 |
| 超过 `--stage-timeout-ms` 仍不稳定 | 失败（`OutputUnstable`） |

`[实测]` 本机稳定判定耗时一致为 321–334 ms（即导出返回后文件已经写完，3 次采样是纯保险）。

另一条实测事实：**输出不是逐字节可复现的**。10 次连续导出的字节数都是 10,614，
但 SHA-256 有 10 个不同值——因为头部内嵌了 `DATE=`、`KEY=`、`FILE=`（含完整路径）。
所以几何回归只能比几何指标，不能比文件哈希。输出路径长度不同还会让字节数微变（10,602 / 10,604 / 10,656 均出现过）。

#### Q3.5 `RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_RETRYLATER` 的重试与取消策略

官方样例 `[样例]` `Custom\Batch\MessageFilter.vb` 对 `SERVERCALL_RETRYLATER` **无条件**返回 99 ms 重试，
没有任何上限。无人值守场景下这等于永久挂起，**不能直接抄**。

探针的策略（`OleMessageFilter.cs`）：

```csharp
if (dwRejectType != SERVERCALL_RETRYLATER) return CANCEL_CALL;   // 明确拒绝，重试无意义
if (cancellation.IsCancellationRequested || dwTickCount > retryBudgetMs) return CANCEL_CALL;
return 99;                                                        // 99 ms 后重试
```

- `dwTickCount` 由 COM 运行时维护（本次调用已等待的毫秒数），天然就是重试预算的计量口径。
- 建议默认预算 **60 秒**，再叠加单文件阶段超时 180 秒。
- 取消时返回 `CANCEL_CALL`，调用方看到的是 `RPC_E_CALL_REJECTED (0x80010001)`；
  **必须把它翻译回"已取消"**，否则会误报成"CAD 忙"。探针已按此实现，`[实测]` 三次不同时点取消
  都正确归类为 `Cancelled`。

#### Q3.6 如何检测"COM 已注册但许可 / Parasolid translator 不可用"？

分三层，**其中第 3 层未验证**：

1. **ProgID 层**：`Type.GetTypeFromProgID("SolidEdge.Application", false)` 返回 null → 未注册。
   `[实测]` 本机返回 CLSID `{DED89DB0-45B6-11CE-B307-0800363A1E02}`，
   `LocalServer32` = `C:\PROGRA~1\Siemens\SOLIDE~1\Program\Edge.exe /automation`。
2. **安装信息层（不启动 CAD、不占许可）**：`SEInstallDataLib.SEInstallData` 是进程内 ActiveX DLL。
   `[SDK]` 主题 `SEInstallDataLib~SEInstallData~GetParasolidVersion.html`（"Gets Parasolid Version"）。
   `[实测]` `GetVersion()` = `220.00.00.104 x64`，`GetParasolidVersion()` = `31.1`。
   这是启动 CAD 之前最便宜的环境判据，探针已在 `ComRegistration` 阶段执行。
3. **许可层**：`[未验证]` 本机是有许可环境，无法制造无许可失败。
   预期表现为 `Activator.CreateInstance` 抛 `CO_E_SERVER_EXEC_FAILURE (0x80080005)`
   或 `Edge.exe` 启动后立刻退出。探针已把 `0x80080005` 归到 `AppLaunchFailed`、
   `0x80070005` 归到 `LicenseUnavailable`，但**这两条映射本身没有实测支撑**，
   正式实现应在无许可机器上补测后再定稿。

#### 错误分类表

`ErrorClass` 枚举（`SolidEdgeExporter.cs`），标注每一类的验证状态：

| 分类 | 触发条件 | 典型 HRESULT | 阶段 | 状态 |
|---|---|---|---|---|
| `ComNotRegistered` | ProgID 解析失败 | `0x80040154` `REGDB_E_CLASSNOTREG` | ComRegistration | 未实测（本机已注册） |
| `AppLaunchFailed` | 创建实例失败 | `0x80080005` `CO_E_SERVER_EXEC_FAILURE` | AppLaunch | 未实测 |
| `LicenseUnavailable` | 启动被许可拒绝 | `0x80070005` `E_ACCESSDENIED` | AppLaunch | 未实测 |
| `InputMissing` | 源文件不存在 | — | Preflight | ✅ 实测 |
| `InputInvalid` | 非 `.par`；或打开失败 | `0x80004005` `E_FAIL` | Preflight / DocumentOpen | ✅ 实测 |
| `InputLocked` | 源文件被独占 | — | Preflight | 逻辑已实现，未造锁实测 |
| `OutputExists` | 目标已存在 | — | Preflight | ✅ 实测 |
| `OutputNotWritable` | 目录不可写 / 输入输出同路径 | — | Preflight | ✅ 实测 |
| `OutputEmpty` | 输出存在但 0 字节 | — | OutputVerify | 逻辑已实现，未实测 |
| `OutputUnstable` | 超时仍未写稳 | — | OutputVerify | 逻辑已实现，未实测 |
| `CallRejected` | CAD 忙且超出重试预算 | `0x80010001` / `0x8001010A` | 任意 | 拒绝已实测（重试成功）；超预算未实测 |
| `Timeout` | 阶段超时 | — | 任意 | 未实测 |
| `Cancelled` | 用户取消 | 取消时表现为 `0x80010001` | 任意 | ✅ 实测（3 个时点） |
| `ExportFailed` | 调用成功但无输出 | — | Export / OutputVerify | 逻辑已实现，未实测 |
| `Unknown` | 其他 | 原样上报 | 任意 | — |

"用户提示框阻塞"没有单独分类：它在探针里表现为阶段超时（`Timeout`），
因为从 COM 客户端侧无法区分"对方在弹框"和"对方在算"。这是设计选择，不是遗漏。

---

### 2.4 .NET 8、Interop 与 COM 生命周期

#### Q4.1 `net8.0-windows` x64 引用官方 Interop 的推荐方式

**用 `<COMReference>`（按 TypeLib GUID 从注册表解析 + tlbimp + `EmbedInteropTypes`），
但必须用 Visual Studio 的 `MSBuild.exe` 构建。**

`[实测]` 这是本次最重要的工程约束，`dotnet build` 会硬失败：

```
error MSB4803: The task "ResolveComReference" is not supported on the .NET Core version of MSBuild.
Please use the .NET Framework version of MSBuild.
```

可用的构建命令（本机已验证成功）：

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" SolidEdgeExportProbe.csproj /t:Restore;Build /p:Configuration=Release
```

对 SE2SW 的影响：SE 工作进程这个子项目**不能进纯 `dotnet build` 的 CI**。
两个选择——(a) 构建机装 VS Build Tools；(b) 事先用 `tlbimp.exe` 生成 interop 程序集并入库，
改成普通 `<Reference>`。Siemens 的 `InteropAssemblies.html` 主题描述的正是 (b) 的做法。

#### Q4.2 最小程序集集合

| 程序集 | 是否必需 | 理由 |
|---|---|---|
| `SolidEdgeFramework` | 必需 | `Application`、`Documents`、`SolidEdgeDocument` |
| `SolidEdgePart` | 必需 | `PartDocument.SaveBody`、`Models` |
| `SolidEdgeConstants` | 必需 | `SolidEdgeDocument.Type` 的返回类型 `DocumentTypeConstants` 定义在此；`SaveBodyConstants` / `ParasolidVersionConstants` 也在此 |
| `SEInstallDataLib` | 可选，建议要 | 不启动 CAD 就能读版本与 Parasolid 版本 |
| `SolidEdgeFrameworkSupport` | 不需要 | `.par -> .x_t` 用不到 |
| `SolidEdgeDraft` / `SolidEdgeAssembly` | 不需要 | 首版不处理 `.dft` / `.asm` |

即：问题单里猜的 `SolidEdgeFramework` + `SolidEdgeConstants` + `SolidEdgePart` **是够的**，
`SEInstallDataLib` 是划算的追加项。

#### Q4.3 `EmbedInteropTypes`、CopyLocal、目标平台

`EmbedInteropTypes=true`（只把用到的类型嵌进本程序集，不分发 Siemens 的 PIA，
也就没有 CopyLocal 问题），`Isolated=false`，`WrapperTool=tlbimp`，平台固定 x64。

完整 `.csproj` 见 [tools/SolidEdgeExportProbe/SolidEdgeExportProbe.csproj](../tools/SolidEdgeExportProbe/SolidEdgeExportProbe.csproj)，核心部分：

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
  <Platforms>x64</Platforms>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>false</SelfContained>
</PropertyGroup>

<ItemGroup>
  <COMReference Include="SolidEdgeFramework">
    <Guid>{8A7EFA3A-F000-11D1-BDFC-080036B4D502}</Guid>
    <VersionMajor>1</VersionMajor><VersionMinor>0</VersionMinor><Lcid>0</Lcid>
    <WrapperTool>tlbimp</WrapperTool>
    <Isolated>false</Isolated>
    <EmbedInteropTypes>true</EmbedInteropTypes>
  </COMReference>
  <!-- SolidEdgeConstants / SEInstallDataLib / SolidEdgePart 同构 -->
</ItemGroup>
```

#### Q4.4 早绑定 / `dynamic` 晚绑定 / 自行生成 Interop，选哪个？

**Siemens 在 SDK 里已经给了答案。** `[SDK]` 主题 `InteropAssemblies.html` 原文：

> "Solid Edge does not provide any interop assemblies that should be used by customers.
> The interop assemblies found in the Solid Edge Program folder are for internal applications only.
> If you need an interop assembly for your application, you will need to generate and use your own
> interop assembly."

同一主题还区分了场景：

> "Out-of-process Applications … you can load any version of your interop assembly without worry of
> collision … Typically, 'Add Reference' from Visual Studio will suffice."

结论：
1. **推荐：自行生成 interop + 早绑定**。`<COMReference>` 就是"构建时调 tlbimp 自行生成"，
   与 Siemens 的建议一致，且拿到编译期类型检查。SE2SW 的 COM 工作进程正是"out-of-process
   application"，落在 Siemens 说"Add Reference 就够了"的那一档。
2. **禁止**：引用 `Program\interop.SolidEdge*Lib.dll`（Siemens 明说仅供内部使用）。
3. `dynamic` 晚绑定只作为降级方案（比如构建机没有 VS）。代价是 API 形状错误要到运行时才暴露，
   与问题单"不用 dynamic 吞掉 API 形状"的立场一致。

#### Q4.5 释放顺序与 `FinalReleaseComObject`

固定顺序，探针在 `finally` 中实现：

```
Document.Close(false)
Application.DoIdle()                      // SDK 明确要求：确保 Document 被真正释放
Marshal.FinalReleaseComObject(document)
Marshal.FinalReleaseComObject(documents)
恢复 DisplayAlerts 原值
Application.DoIdle()
Application.Quit()                        // 仅当是自己创建的实例
Marshal.FinalReleaseComObject(application)
OleMessageFilter.Revoke()                 // 还原线程原有过滤器
```

每个 RCW 都调 `FinalReleaseComObject`：因为这些 RCW 全部只在本方法内持有、不外传，
生命周期完全可控，确定性释放优于等 GC。

#### Q4.6 需要两轮 `GC.Collect` / `WaitForPendingFinalizers` 吗？

**不需要。**

那套写法解决的是"RCW 被终结器线程延迟释放，导致 CAD 进程不退出"。
本设计从根上避免了这个问题：(a) 所有 RCW 显式 `FinalReleaseComObject`；
(b) COM 全部关在一次批处理一个的工作进程里，进程退出时操作系统兜底。

`[实测]` 10 次连续转换后，探针创建的 `Edge.exe` 残留数为 **0**；
探针另外主动 `WaitForExit(ownedPid, 30s)` 核对进程真的退出了，10 次全部通过。
比"撒两轮 GC 然后祈祷"可靠得多。

#### Q4.7 如何保证只退出自己创建的实例？

**PID 快照法**（`SolidEdgeExporter.Run()`）：

```
before = Edge.exe 的 PID 集合
app    = Activator.CreateInstance(SolidEdge.Application)
after  = Edge.exe 的 PID 集合
new    = after - before
CreatedNewInstance        = new.Length > 0
AttachedToExistingSession = new.Length == 0 && before.Length > 0
只有 CreatedNewInstance 为真时才调用 Quit()
```

`[实测]` 本机用户会话 PID 29216 全程常驻。25 次左右的运行中，
`CoCreateInstance` **每一次**都新建了独立的 `Edge.exe`（如 5608 / 17384 / 24968 …），
`AttachedToExistingSession` 一次都没为真，用户会话从未被关闭。

另外**绝不**用 `Marshal.GetActiveObject` / `GetObject`——SDK 的 `ConnectingToSolidEdge.html`
给的就是这个连接到已有实例的写法，正是我们要避开的。
（顺带一条 .NET 8 事实：`Marshal.GetActiveObject` 在 .NET Core / .NET 5+ 中已不存在，
要用得自己 P/Invoke `oleaut32!GetActiveObject`。我们不需要。）

---

## 3. 推荐 API 调用链

```csharp
// 线程：[STAThread]，进程：x64，一次批处理一个独立工作进程

// 0. 前置检查（不碰 COM）：输入存在/扩展名/输入≠输出/输出不存在/输出目录可写/源未被占用
// 1. 环境探测（不启动 CAD）
Type.GetTypeFromProgID("SolidEdge.Application", throwOnError: false)
new SEInstallDataLib.SEInstallData().GetVersion() / .GetParasolidVersion()

// 2. COM 会话
OleMessageFilter.Register(retryBudgetMs, cancellationToken)   // 有预算、可取消
int[] before = Edge.exe PIDs
var app = (SolidEdgeFramework.Application)Activator.CreateInstance(progIdType);
int[] newPids = Edge.exe PIDs - before                        // 判断是否自己新建

var oldAlerts = app.DisplayAlerts;
app.DisplayAlerts = false;
if (isOurInstance) app.Visible = false;
app.DoIdle();

var documents = app.Documents;
var document  = (SolidEdgeFramework.SolidEdgeDocument)documents.Open(inputPath);
app.DoIdle();

// 3. 导出（首选）
var part = (SolidEdgePart.PartDocument)document;
part.SaveBody(
    outputPath,
    (int)SolidEdgeConstants.SaveBodyConstants.seSaveBodyAsParasolidText,   // = 2
    (int)SolidEdgeConstants.ParasolidVersionConstants.seParasolidCurrentVersion, // = 0
    Type.Missing,   // NumModelsToBeSaved：全部
    Type.Missing);  // ModelsToBeSaved
app.DoIdle();

// 备选（无参数可调，仅供对照）：document.SaveAs(outputPath);   // 扩展名 .x_t = 文本

// 4. 验收：文件存在 + 非空 + 连续 3 次采样长度不变 + 头部 FORMAT=text
// 5. 清理：Close(false) -> DoIdle -> FinalReleaseComObject(doc, docs) ->
//          还原 DisplayAlerts -> DoIdle -> Quit(仅自己的实例) ->
//          FinalReleaseComObject(app) -> 过滤器 Revoke -> 核对 PID 已退出
```

---

## 4. 交付的探针

[tools/SolidEdgeExportProbe/](../tools/SolidEdgeExportProbe)（可直接编译，无伪代码）：

| 文件 | 内容 |
|---|---|
| `SolidEdgeExportProbe.csproj` | net8.0-windows / x64 / 4 个按 GUID 解析的 `COMReference` |
| `Program.cs` | `[STAThread]` 入口、参数解析、Ctrl+C 取消、JSON 输出、退出码 |
| `OleMessageFilter.cs` | `IOleMessageFilter`，带重试预算与取消，退出时还原原过滤器 |
| `SolidEdgeExporter.cs` | 前置检查、环境探测、导出、输出验收、确定性清理、错误分类 |
| `README.md` | 构建/运行命令与行为约定 |

对照问题单的 10 条探针要求：

| # | 要求 | 状态 |
|---|---|---|
| 1 | net8.0-windows / x64 / STA | ✅ |
| 2 | `--input` / `--output` | ✅（另加 method、版本、超时、取消等） |
| 3 | 拒绝同路径、拒绝覆盖 | ✅ 实测 |
| 4 | 用 ProgID 创建自己的实例 | ✅ 实测每次都是新进程 |
| 5 | 过滤器注册/撤销、有时限、可取消 | ✅ 实测 |
| 6 | 无提示选项并在退出前还原 | ✅（`DisplayAlerts` 存旧值还原；`Visible` 只在自有实例上改） |
| 7 | 打开→导出→关闭→退出自有实例 | ✅ |
| 8 | 全失败路径确定性清理，不按进程名 kill | ✅（只对已知 PID 做退出核对，从不 `Kill`） |
| 9 | 结构化 JSON：成功/阶段/耗时/HRESULT/分类/大小/SE 版本/Parasolid 版本 | ✅ 全部包含 |
| 10 | 成功判据不止"没抛异常" | ✅ 存在 + 非空 + 写入稳定 + 头部解析 |

---

## 5. 实测命令与结果

构建：

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\OneHistory\OneHistory-Projects\2026-019-Studio工具箱\b-Code-SE2SW\tools\SolidEdgeExportProbe\SolidEdgeExportProbe.csproj" /t:Restore;Build /p:Configuration=Release
```

单次运行：

```bash
SolidEdgeExportProbe.exe --input "<临时目录>\beaker150.par" --output "<临时目录>\out.x_t" --method savebody
```

样件先复制到会话临时目录再处理，原件 `1-蜀牛低型烧杯150ml.par` 全程未被打开或修改。

### 5.1 验证矩阵

| 编号 | 场景 | 预期 | 实测 | 结果 |
|---|---|---|---|---|
| SE-01 | 正常简单零件 | 非空 `.x_t`，源哈希不变 | `SaveBody` 10,614 B / `SaveAs` 10,612 B，`FORMAT=text`，`SourceUnchanged=true` | ✅ |
| SE-02 | 中文文件名与路径 | 成功导出，名称不乱码 | `1-蜀牛低型烧杯150ml-savebody.x_t` 10,656 B、`-saveas.x_t` 10,652 B，磁盘上文件名正确 | ✅ |
| SE-03 | 输出已存在 | 拒绝覆盖，不启动导出 | `Preflight` / `OutputExists`，未启动 Solid Edge | ✅ |
| SE-04 | 输出目录不可写 | 前置失败，不打开源文件 | `Preflight` / `OutputNotWritable`（canary 写入被拒） | ✅ |
| SE-05 | 源文件不存在 | 明确分类为输入错误 | `Preflight` / `InputMissing` | ✅ |
| SE-06 | 损坏/非 `.par` 文件 | 打开失败，清理完整 | 4KB 随机数据改名 `.par` → `DocumentOpen` / `InputInvalid` / `0x80004005`，无弹框、无残留进程 | ✅ |
| SE-07 | 连续转换 10 次 | 无残留探针创建的进程 | 10/10 成功，残留 0，源哈希不变，单次 6,882–7,302 ms | ✅ |
| SE-08 | Solid Edge 忙/调用被拒绝 | 有界重试，最终成功或明确超时 | 两个探针并发：均成功，被拒次数 1 / 4，`GaveUp=false`，无残留 | ✅ 部分 |
| SE-09 | 用户已有会话 | 不复用、不关闭 | 用户会话 PID 29216 全程存活；每次都新建独立实例 | ✅ |
| SE-10 | 导出中取消 | 状态明确，进程可退出 | 2500/5300/5900 ms 三点取消 → 全部 `Cancelled`，自有实例已退出，无残留，无半成品文件 | ✅ |

SE-08 记为"部分"：只制造了并发场景下的自然拒绝并全部重试成功，
**没有**制造"超出重试预算后放弃"的极端情况。

### 5.2 Parasolid 输出参数矩阵

| 方法 | 参数 | 扩展名 | `FORMAT=` | schema | 字节数 |
|---|---|---|---|---|---|
| `SaveBody` | Text，版本 0 | `.x_t` | `text` | `SCH_3101255_31100_1300` | 10,614 |
| `SaveBody` | Binary，版本 0 | `.x_b` | `binary` | `SCH_3101255_31100_1300` | 9,180 |
| `SaveBody` | Text，版本 130 | `.x_t` | `text` | `SCH_1300000_130060` | 10,052 |
| `SaveBody` | Text，版本 100 | `.x_t` | `text` | `SCH_1000000_100040` | 9,983 |
| `SaveAs` | 无参数 | `.x_t` | `text` | `SCH_3101255_31100_1300` | 10,612 |
| `SaveAs` | 无参数 | `.x_b` | `binary` | `SCH_3101255_31100_1300` | 9,180 |

### 5.3 阶段耗时（典型单次，毫秒）

| 阶段 | 耗时 |
|---|---:|
| ComRegistration（含 SEInstallData） | 0 |
| AppLaunch（启动 Edge.exe 并拿到 Application） | 4,920–5,733 |
| AppConfigure | 3–6 |
| DocumentOpen | 704–832 |
| **Export** | **5–22** |
| OutputVerify（写入稳定判定） | 321–334 |
| DocumentClose | 329–384 |

**启动 Solid Edge 占了单次总耗时的 70% 以上，实际导出只有几毫秒。**
这对批处理架构有直接含义：**一个工作进程内应连续转换整批文件，而不是每个文件起停一次 CAD**。
问题单里"一次批处理一个独立 x64 STA 工作进程"的设计正好符合，无需修改，但要确保
"一批"是真的一批而不是一个文件。

---

## 6. 尚未解决的风险

以下**没有**证据闭环，不能当成已验证事实：

| # | 风险 | 影响 | 建议动作 |
|---|---|---|---|
| R1 | 多实体 `.par` 的导出结果与弹框行为未实测 | 批次可能被弹框卡死，或几何丢失 | 造一个多实体样件；验证 `DisplayAlerts=False` 是否足够；确定 `ModelsToBeSaved` 的传参形式（SafeArray of BSTR？未验证） |
| R2 | 纯曲面零件、空零件未实测 | 可能导出空文件或失败 | 补两个样件，确认落到 `OutputEmpty` 还是异常 |
| R3 | 无许可 / translator 不可用的表现未实测 | 错误分类表中 3 行是推断 | 在无许可机器上补测，再定稿 HRESULT 映射 |
| R4 | 未验证生成的 `.x_t` 能被 SolidWorks 正确读入 | 本阶段"成功"不等于链路成功 | 属于问题单范围外，但必须在 SW 阶段前做，且要比几何指标不是文件哈希 |
| R5 | `dotnet build` 不支持 `COMReference` | CI/构建机约束 | 决策：装 VS Build Tools，还是预生成 interop 入库 |
| R6 | `SaveAs` 文档身份的实测结论只覆盖 `.x_t`/`.x_b` | 换目标格式可能不同 | 不依赖此行为，一律 `Close(false)` |
| R7 | 重试预算耗尽后的行为未实测 | 极端忙场景下的表现未知 | 用长时间占用 CAD 的操作制造，验证 `GaveUp` 与 `CallRejected` |
| R8 | Parasolid 头部含 ANSI 中文日期（本机实测 `HeaderIsPureAscii=false`，出现 GBK 字节 `D6 DC`…） | 任何按 UTF-8/ASCII 解析头部的代码会出错 | 头部只按 ASCII 逐字节解析已知字段，不要整体解码 |
| R9 | 输出字节不可复现（哈希每次不同、路径长度影响字节数） | 用哈希做回归会全部误报 | 几何回归改用实体数/体积/包围盒 |
| R10 | Windows PowerShell 5.1 传中文参数会乱码（实测） | 若用 PS 脚本调度会假性失败 | 正式实现用 `ProcessStartInfo.ArgumentList` 启动工作进程 |

---

## 7. 对现有 `src/SE2SW.Worker` 的具体修改建议

验证期间 `src/SE2SW.Worker/SolidEdgeExporter.cs` 已经写好，按本次结论逐条对照：

| # | 现状 | 结论 | 建议 |
|---|---|---|---|
| W1 | `finally` 中无条件 `((dynamic)applicationObject).Quit()` | **最严重的一条。** 没有任何判据证明这个 Application 是自己创建的 | 加 PID 快照守卫。虽然本机实测 `CoCreateInstance` 每次都新建进程，但代码里没有这个保证，一旦哪天复用到用户会话就会直接关掉用户没保存的工作。这与预研计划 §6 的通过门槛第 6 条冲突 |
| W2 | `document.SaveAs(job.XtPath)` | 可用，但无法指定文本/二进制与 Parasolid 版本 | 改 `((SolidEdgePart.PartDocument)doc).SaveBody(path, 2, 0, Type.Missing, Type.Missing)`，并把注释里"等待专项探针结论"替换为本报告 §2.2 |
| W3 | `DoIdle()` 在 `Close` **之前**调用，`Close` 之后没有 | `[SDK]` DoIdle 主题明确说 DoIdle 的首要作用就是确保 `Close` 之后文档被真正释放 | 在 `document.Close(false)` 之后补一次 `application.DoIdle()` |
| W4 | `application.Visible = false; application.DisplayAlerts = false;` 未存旧值 | 复用会话时会污染用户环境 | 存旧值并在 `finally` 还原；`Visible` 只在确认是自有实例时才改 |
| W5 | 全程 `dynamic` 晚绑定 | 能跑，但 API 形状错误只在运行时暴露 | 决策题：改 `<COMReference>` 早绑定就必须放弃 `dotnet build`（MSB4803，见 §2.4 Q4.1）。若要保住 `dotnet build`，就保留 `dynamic`，但至少把 `SaveBody` 的常量写成带名字的 `const int` 并注释枚举出处 |
| W6 | `Type.GetTypeFromProgID(..., throwOnError: true) ?? throw` | `throwOnError: true` 时不会返回 null，`??` 分支是死代码 | 改成 `throwOnError: false` 再判 null，这样"未注册"能落到明确的错误分类而不是裸 `COMException` |
| W7 | 失败时 `TryDelete(job.XtPath)` | 方向正确 | 保留。另建议先导出到同目录临时名再原子改名，避免下游看到半成品 |
| W8 | 未见按几何指标验收 | 只判了文件稳定非空 | 至少解析头部 `FORMAT=`，确认真的是 text 而不是意外的 binary |

已经做对、不用改的：`[STAThread]`、`OleMessageFilter` 有 30 秒重试预算并可取消、
`FileProbe.WaitForStableNonEmptyFile` 的稳定性判定、单文件失败不阻断后续文件。

另外一条架构提醒（§5.3）：启动 Solid Edge 要 5 秒，实际导出只要 5–22 毫秒。
`SolidEdgeExporter.Export` 目前已经是"一个进程处理整批"，这是对的，**不要**改成一个文件起停一次 CAD。

---

## 附：本次引用的 SDK 主题名

`sesdk.chm` 中的主题（用 7-Zip 从 CHM 解出验证，`hh.exe -decompile` 在本机不产出文件）：

- `SolidEdgeFramework~SolidEdgeDocument~SaveAs.html`
- `SolidEdgePart~PartDocument~SaveBody.html`
- `SolidEdgeConstants~SaveBodyConstants.html`
- `SolidEdgeConstants~ParasolidVersionConstants.html`
- `SolidEdgeFramework~Application~DisplayAlerts.html`
- `SolidEdgeFramework~Application~DoIdle.html`
- `SolidEdgeFramework~Documents~Open.html`
- `SEInstallDataLib~SEInstallData~GetParasolidVersion.html`
- `ConnectingToSolidEdge.html`
- `InteropAssemblies.html`
- `OleMessageFilterUsage.html` / `OleMessageFilterCS.html` / `OleMessageFilterVB.html`

官方样例：

- `Custom\Batch\Batch_frm.vb`（`:252` DisplayAlerts 与 PR#9090402 注释、`:409` 格式表、
  `:488` SaveAs、`:520` SetGlobalParameter 还原）
- `Custom\Batch\MessageFilter.vb`（无上限 99 ms 重试）
- `Custom\Batch\Batch.vbproj` / `Custom\DynAttrib\DynAttrib.vbproj` / `Custom\OpenSave\*.vbproj`（TypeLib GUID）
