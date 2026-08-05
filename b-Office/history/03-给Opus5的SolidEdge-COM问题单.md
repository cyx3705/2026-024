# 给 Opus 5 的 Solid Edge COM 问题单

## 任务目标

请针对 **Solid Edge 2020 COM 自动化将 `.par` 稳定导出为文本 Parasolid `.x_t`** 做一次可复现的技术确认，
并交付一个最小、可编译、可运行的 C# 探针。不要实现完整 UI，也不要修改 OHS 主程序。

项目最终转换链为：

```text
Solid Edge .par
  -> Solid Edge 2020 COM
  -> Parasolid .x_t
  -> SolidWorks COM
  -> SolidWorks .SLDPRT
```

你只需要解决并验证其中的 Solid Edge `.par -> .x_t` 阶段。

## 当前环境

- Windows 64 位。
- Solid Edge 2020 已安装。
- SDK 帮助：`C:\Program Files\Siemens\Solid Edge 2020\SDK\sesdk.chm`
- Siemens 官方示例：`C:\Program Files\Siemens\Solid Edge 2020\Custom\Batch`
- 类型库：
  - `C:\Program Files\Siemens\Solid Edge 2020\Program\framewrk.tlb`
  - `C:\Program Files\Siemens\Solid Edge 2020\Program\constant.tlb`
  - `C:\Program Files\Siemens\Solid Edge 2020\Program\Part.tlb`
- 可参考的官方 Interop 位于 Solid Edge 安装目录的 `Custom` 示例子目录中。
- 目标实现技术栈：C#、`net8.0-windows`、x64、STA。

测试样件当前位于：

```text
C:\OneHistory\OneHistory-Projects\2026-019-Studio工具箱\Unused\b-Module-SE\零件.par
```

探针必须先把样件复制到临时测试目录再处理，不能修改、重命名或保存覆盖原件。

## 已确认事实

Siemens 自带的 `Custom\Batch\Batch_frm.vb` 已经使用以下流程进行 Parasolid 导出：

```vb
objApp.DisplayAlerts = False
objDoc = objApp.Documents.Open(sourcePath)
objApp.DoIdle()
objDoc.SaveAs(targetXtPath)
objDoc.Close(False)
objApp.DoIdle()
```

该示例的格式列表明确把 Parasolid 扩展名写为 `.x_t`，并说明 `Document.SaveAs` 根据目标扩展名选择导出器。
同一示例还提供 `IOleMessageFilter`，在 COM 返回 `SERVERCALL_RETRYLATER` 时以 99 ms 间隔重试。

因此问题不是“是否存在基础调用路径”，而是以下生产化细节还没有证据闭环。

## 需要你回答的问题

### 1. 推荐导出 API

1. 对 Solid Edge 2020 的 `.par -> .x_t`，`Document.SaveAs(target.x_t)` 是否是 Siemens 推荐且完整的 COM 路径？
2. 是否存在更适合无人值守批处理的专用 Parasolid translator/export 接口？如果存在，请给出完整接口名、程序集、
   方法签名及相对 `SaveAs` 的优势。
3. `SaveAs` 导出后是否会改变当前文档的 `FullName`、脏状态或活动文档身份？这会影响关闭策略。

请引用 `sesdk.chm` 中的具体主题名，或 Siemens 安装示例中的具体文件和代码位置。不能只给经验结论。

### 2. Parasolid 输出参数

1. 如何明确选择**文本 Parasolid**而不是二进制 Parasolid？仅使用 `.x_t` 扩展名是否足够？
2. 如何控制 Parasolid 版本？Solid Edge 2020 默认导出哪个版本？
3. 版本/单位/几何容差等设置来自 `SaveAs` 参数、全局参数、用户配置还是注册表？
4. 若需要临时修改会话级或用户级设置，如何保存旧值并在 `finally` 中恢复，避免污染用户环境？
5. 多实体零件、曲面体和空零件的导出行为分别是什么？是否会出现必须人工确认的对话框？

请列出实际可调用的常量、枚举、接口和读写方法，不要只描述界面菜单位置。

### 3. 无人值守与错误处理

1. `Application.DisplayAlerts = false` 能抑制哪些导出提示，不能抑制哪些提示？
2. 除 `DoIdle()` 和 `IOleMessageFilter` 外，是否还需要消息泵、事件等待或特定全局参数？
3. `Document.SaveAs` 如何报告失败：返回值、COM 异常、HRESULT、事件，还是仅通过输出文件判断？
4. 如何区分“导出完成”“导出失败”和“COM 返回但文件仍在写入”？
5. 对 `RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_RETRYLATER`，建议的最大重试时间和取消策略是什么？
6. 如何检测 Solid Edge COM 已注册但许可证或 Parasolid translator 不可用？

请给出一份错误分类表，至少包含：COM 未注册、启动失败、许可失败、源文件损坏、源文件被占用、输出不可写、
目标已存在、调用被拒绝、用户提示框阻塞和导出文件为空。

### 4. .NET 8、Interop 与 COM 生命周期

1. `net8.0-windows` x64 引用 Solid Edge 2020 官方 Interop 的推荐方式是什么？
2. 应引用哪些最小程序集：`SolidEdgeFramework`、`SolidEdgeConstants`、`SolidEdgePart` 是否足够？
3. `EmbedInteropTypes`、`Private/CopyLocal` 和目标平台应怎样设置？请给出 `.csproj`。
4. 若官方 PIA 对 .NET 8 不理想，早绑定、`dynamic` 晚绑定和直接生成 Interop 三种方式中推荐哪一种，为什么？
5. 请给出从 Document 到 Documents、Application 的确定释放顺序，以及每个 RCW 是否应调用
   `Marshal.FinalReleaseComObject`。
6. 是否需要两轮 `GC.Collect/WaitForPendingFinalizers`？如需要，请解释它解决什么；如不需要，请给出更可靠做法。
7. 如何保证只退出探针自己创建的 Solid Edge 实例，绝不关闭用户原有会话？

最终工具会把 COM 放在一次批处理一个独立 x64 STA 工作进程中，所以不需要解决 OHS
`AssemblyLoadContext` 内直接持有 RCW 的问题。

## 要求交付的探针

请交付一个最小项目，建议结构：

```text
SolidEdgeExportProbe/
├─ SolidEdgeExportProbe.csproj
├─ Program.cs
├─ OleMessageFilter.cs
├─ SolidEdgeExporter.cs
└─ README.md
```

探针要求：

1. `net8.0-windows`、x64、入口线程为 STA。
2. 命令行参数为 `--input <绝对par路径> --output <绝对x_t路径>`。
3. 拒绝输入路径和输出路径相同，拒绝覆盖既有输出。
4. 通过 `SolidEdge.Application` ProgID 创建本工具所有的独立实例。
5. 注册和撤销 `IOleMessageFilter`，重试必须有总时限且支持取消。
6. 设置合适的不可见/无提示选项，并在退出前恢复任何被修改的用户或会话设置。
7. 打开 `.par`、导出 `.x_t`、关闭文档、退出自己创建的应用实例。
8. 所有失败路径都执行确定性清理，不按进程名结束 Solid Edge。
9. 输出结构化 JSON 结果，至少包含：成功状态、阶段、耗时、HRESULT、错误分类、输出文件大小、
   Solid Edge 版本和实际 Parasolid 版本信息（若能读取）。
10. 成功条件不能只有“SaveAs 未抛异常”，还要检查输出存在、非空、写入已稳定。

请附上完整构建和运行命令。代码必须能直接编译，不要用伪代码代替关键 COM 调用。

## 验证矩阵

至少执行以下测试，并记录结果：

| 编号 | 场景 | 预期 |
|---|---|---|
| SE-01 | 正常简单零件 | 生成非空 `.x_t`，源文件哈希不变 |
| SE-02 | 中文文件名与路径 | 成功导出，名称不乱码 |
| SE-03 | 输出已存在 | 探针拒绝覆盖，不启动导出 |
| SE-04 | 输出目录只读/不可写 | 前置检查失败，不打开源文件 |
| SE-05 | 源文件不存在 | 明确分类为输入错误 |
| SE-06 | 损坏或非 `.par` 文件 | 打开失败，后续清理完整 |
| SE-07 | 连续转换 10 次 | 无残留探针创建的 Solid Edge 进程 |
| SE-08 | Solid Edge 忙/调用被拒绝 | 有界重试，最终成功或明确超时 |
| SE-09 | 用户已有 Solid Edge 会话 | 不复用、不关闭用户会话 |
| SE-10 | 导出中取消 | 不开始新操作，当前状态明确，进程可退出 |

## 回答格式

请按以下顺序回答：

1. 结论摘要。
2. 每个问题的直接答案及依据。
3. 推荐 API 调用链。
4. 完整可编译探针文件。
5. 实测命令和结果表。
6. 尚未解决的风险，不得把猜测写成已验证事实。

## 不在本问题单范围内

- 不实现 SolidWorks `.x_t -> .SLDPRT`。
- 不实现 WPF 界面。
- 不实现 OHS `IUiModule`、`ModuleInfoBase` 或 `module.manifest.json`。
- 不移动项目的 `Unused\b-Module-GE`、`Unused\b-Module-SW`。
- 不覆盖、不修改测试源 `.par`。
- 不扩展到 `.asm`、`.psm`、`.dft`。

