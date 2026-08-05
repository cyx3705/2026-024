# SE2SW COM 预研与验证计划

> 状态说明（2026-07-27）：本文件保留为预研计划与历史依据。Solid Edge 专项结论见
> [04-Opus5答复-SolidEdge导出验证报告.md](04-Opus5答复-SolidEdge导出验证报告.md)，V1.1 实施与真实
> 端到端结果见 [05-V1.1-工程实施与验收.md](05-V1.1-工程实施与验收.md)。生产实现已由下述
> `SaveAs` 候选切换为 `PartDocument.SaveBody`。

## 1. 已确认事实

### 1.1 Solid Edge 2020

本机 SDK 只直接提供 `Readme.txt` 和 `sesdk.chm`，但安装目录同时包含 Siemens 官方自动化样例。
`Custom\Batch\Batch_frm.vb` 已确认以下调用链：

```text
SolidEdge.Application
  -> Documents.Open(source.par)
  -> Application.DoIdle()
  -> Document.SaveAs(target.x_t)
  -> Document.Close(false)
```

样例的格式列表明确使用 `Parasolid (*.x_t)`，并说明 `SaveAs` 依据目标扩展名决定导出格式。样例还执行：

- `Application.DisplayAlerts = false`，抑制批处理提示；
- 每次打开、关闭后调用 `DoIdle()`；
- 注册 `IOleMessageFilter`，对 `SERVERCALL_RETRYLATER` 返回 99 ms 重试；
- 在失败路径上关闭文档并继续下一项。

因此首个探针不需要猜测专用 Parasolid 导出接口，可从 `Document.SaveAs("*.x_t")` 开始验证。

### 1.2 SolidWorks

本机 COM 注册和程序集检查结果：

| 项目 | 结果 |
|---|---|
| ProgID | `SldWorks.Application` |
| 主程序版本 | `33.5.0.0053`（SolidWorks 2025 SP5） |
| 主程序 | `C:\Program Files\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS\sldworks.exe` |
| Interop | `...\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll` |
| 常量程序集 | `...\SOLIDWORKS\api\redist\SolidWorks.Interop.swconst.dll` |

从本机官方 Interop 反射确认的候选 API：

```csharp
ModelDoc2 ISldWorks.LoadFile4(
    string fileName,
    string argString,
    object importData,
    ref int errors);

object ISldWorks.GetImportFileData(string fileName);

bool IModelDocExtension.SaveAs3(
    string name,
    int version,
    int options,
    object exportData,
    object advancedSaveAsOptions,
    ref int errors,
    ref int warnings);
```

`LoadFile4` 是导入非原生文件的首选候选；`GetImportFileData` 用于取得导入选项对象；成功后使用
`IModelDocExtension.SaveAs3` 保存为 `.SLDPRT`。参数字符串与导入选项必须通过本机样件实验或官方 API
帮助确认，不能在正式实现中凭经验固定。

## 2. 预研探针原则

- 探针放在未来的 `tools/Probe` 或 `tests/ComProbe`，不作为 OHS 命令暴露。
- 目标平台固定 `x64`，入口标记 `[STAThread]`。
- 探针只处理复制出来的测试样件，不修改 019 项目 `Unused` 中的原件。
- 每次只启动一个 CAD 应用，阶段结束后完全释放，再启动另一个 CAD。
- 记录 COM ProgID、产品版本、调用耗时、HRESULT、API 错误码、输出大小和能否重新打开。
- 不用 `dynamic` 吞掉 API 形状；正式实现优先引用官方 Interop，探针可同时验证晚绑定兼容性。
- Interop 引用设置为不复制安装目录 DLL，避免把厂商 PIA 当作模块私有实现随意分发；最终发布策略需结合
  Siemens 和 Dassault 的再分发许可确认。

## 3. Solid Edge 探针

### 3.1 最小成功路径

1. 用 `Type.GetTypeFromProgID("SolidEdge.Application", throwOnError: true)` 验证 COM 注册。
2. 在 STA 线程注册 OLE 消息过滤器。
3. 新建应用实例，设置不可见并关闭显示警告。
4. 打开样件副本 `.par`。
5. 调用 `DoIdle()`。
6. `SaveAs` 到同目录临时 `.x_t`。
7. 关闭文档但不保存对源文件的修改。
8. 检查 `.x_t` 非空，并由 Solid Edge 或 SolidWorks 重新打开。
9. 释放对象并退出本工具创建的 Solid Edge 实例。

### 3.2 必测失败

- ProgID 未注册或许可不可用。
- 源 `.par` 不存在、只读、损坏或被占用。
- 输出目录不可写、目标已存在、路径过长。
- Solid Edge 正忙导致 `RPC_E_CALL_REJECTED`。
- 导出过程出现需要人工确认的提示框。
- 多实体 `.par`、空零件、曲面零件和较大零件。

### 3.3 需要记录

- `SaveAs` 是否会修改当前文档身份或源文件脏状态。
- `DisplayAlerts=false` 是否足够抑制多实体等提示。
- 文本 Parasolid 的实际版本头和编码。
- 关闭文档后是否残留 Solid Edge 进程。

## 4. SolidWorks 探针

### 4.1 最小成功路径

1. 验证 `SldWorks.Application` COM 注册并读取 `RevisionNumber()`。
2. 新建应用实例，设置不可见，关闭非必要用户交互。
3. 调用 `GetImportFileData(xtPath)`，记录返回对象的实际类型；若返回 `null`，验证 `LoadFile4` 是否允许。
4. 分别试验官方帮助指定的 `LoadFile4` 参数字符串，固定能稳定导入 `.x_t` 且不弹窗的一组参数。
5. 检查返回 `ModelDoc2` 非空、文档类型为零件并记录导入错误码。
6. 使用 `ModelDocExtension.SaveAs3` 保存到临时 `.SLDPRT`，记录布尔结果、errors 和 warnings。
7. 关闭文档，再用 SolidWorks 原生打开方式重新打开 `.SLDPRT`。
8. 释放对象并退出本工具创建的 SolidWorks 实例。

`OpenDoc6` 可作为重新打开原生 `.SLDPRT` 的验证 API，不作为导入 `.x_t` 的首选路径。

### 4.2 必测失败

- ProgID 未注册、SolidWorks 启动失败或许可不可用。
- `.x_t` 损坏、版本不兼容、包含曲面或多个实体。
- 保存路径不可写、目标存在、文件被占用。
- 导入成功但保存失败。
- errors 为非零、warnings 非零但返回对象不为空的组合。
- 关闭文档后 SolidWorks 仍认为文档有未保存修改。

### 4.3 需要记录

- `LoadFile4` 的最终 `argString` 和 `importData` 类型。
- 3D Interconnect 开/关对结果的影响，以及首版采用哪一种。
- 多实体导入后的零件类型、实体数和 FeatureManager 结构。
- `SaveAs3` 的 version/options 常量组合。
- 关闭文档和退出应用所需的确切方法与顺序。

## 5. 端到端几何验收

只检查文件存在不足以证明转换成功。探针阶段至少为每个样件记录：

| 指标 | SE 源件 | XT 中间件 | SW 输出件 |
|---|---:|---:|---:|
| 文件可打开 | 是/否 | 是/否 | 是/否 |
| 实体数 | 数值 | 数值 | 数值 |
| 曲面数 | 数值 | 数值 | 数值 |
| 体积 | 数值 | 可选 | 数值 |
| 包围盒 | X/Y/Z | 可选 | X/Y/Z |

体积与包围盒比较采用明确公差，单位统一后再判断。若 Solid Edge COM 读取这些指标的成本过高，首轮可以
人工记录源件，并自动比较 SW 输出；但在批量工具正式验收前必须形成可重复的基准样件集。

建议基准至少包含：简单拉伸、孔/圆角、多实体、曲面体、中文文件名、长路径和一个接近真实规模的大零件。

## 6. 通过门槛

进入正式 UI 开发前，COM 探针必须满足：

1. 同一组样件连续运行 10 批无残留 CAD 进程、无人工弹窗。
2. 每个成功项的 `.x_t` 和 `.SLDPRT` 均可重新打开。
3. 单项失败不阻止后一项，错误码可定位到 SE 导出或 SW 导入阶段。
4. 用户取消后不再开始新文件，当前文件能在约定超时内结束或被隔离进程终止。
5. 源 `.par` 的修改时间和内容哈希保持不变。
6. 两个 CAD 的用户已有会话不会被本工具关闭。
7. 几何基准指标在约定公差内。

## 7. 风险清单

| 风险 | 影响 | 当前对策 |
|---|---|---|
| COM 调用被 CAD 忙状态拒绝 | 随机批次失败 | STA + OLE 消息过滤器 + 有界重试 |
| 自动化弹窗阻塞无人值守批次 | 工作进程挂起 | 关闭警告、探针覆盖提示场景、单项超时 |
| RCW/原生 DLL 阻止 OHS 热卸载 | 模块无法干净重载 | COM 全部隔离到批次工作进程 |
| Parasolid 只保留几何 | 参数化信息丢失 | UI 与报告明确保真边界 |
| 厂商版本差异 | 升级后 API 行为变化 | ProgID/版本探测、基准样件回归、避免硬编码安装路径 |
| 输出重名或半成品 | 覆盖用户数据 | 默认跳过；未来覆盖采用临时文件 + 原子替换 |
| `Unused` 目录只移动一半 | OHS 项目结构不一致 | GE/SW 双目录统一预检后再依次移动 |
| 许可或 Interop 再分发限制 | 无法发布或运行 | 使用本机安装 COM/PIA，发布前单独核对许可 |
