# SolidWorksRecognizeProbe

HistoryMinerva V2.0 可实现性探针：`.x_t` -> 导入 -> FeatureWorks 自动特征识别 -> 每个草图完全定义 -> `.SLDPRT`。

只回答"能不能做、怎么做、代价多大"，不是生产实现。

## 构建

```bash
dotnet build SolidWorksRecognizeProbe.csproj -c Release
```

与 Solid Edge 探针不同，这里 **`dotnet build` 可以直接用**：Dassault 在
`api\redist` 下正式提供可再分发的托管 Interop 程序集，用普通 `<Reference>` 即可，
不需要 `<COMReference>`，也就不受 MSB4803 限制。

安装目录通过 `SwApiRedist` 属性可覆盖：

```bash
dotnet build -c Release -p:SwApiRedist="D:\SW\api\redist"
```

## 运行

```bash
SolidWorksRecognizeProbe.exe --input "D:\out\part.x_t" --output "D:\out\part.SLDPRT" --isolated --options 0x3F
```

| 参数 | 说明 |
|---|---|
| `--input <路径>` | 绝对路径的 `.x_t`；诊断保存/重开差异时也可传 `.SLDPRT` |
| `--output <路径>` | 绝对路径的 `.SLDPRT`；已存在则拒绝 |
| `--visible` | 显示 SolidWorks 窗口（排障用） |
| `--isolated` | 启动自有 SolidWorks 进程，枚举 ROT 并按 `SolidWorks_PID_<pid>` 直接绑定；结束时只关闭该进程 |
| `--attach-pid <pid>` | 附着指定的现有 SolidWorks；只关闭探针打开的文档，结束后恢复原活动文档，不退出该会话 |
| `--options <值>` | FeatureWorks 自动识别位掩码，支持十进制或 `0x` 十六进制 |
| `--advanced-options <值>` | 诊断用 `SetAdvancedOptions` 位掩码 |
| `--performance-options <值>` | 诊断用 `SetPerformanceOptions` 位掩码 |
| `--create-options <值>` | 诊断用 `CreateFeatures` 位掩码 |
| `--selection <策略>` | `none`、`first-mark0` 或 `first-mark8`；仅用于复现实验，生产合同为 `none` |
| `--recognition-passes <1..10>` | 同一掩码的重复识别次数；所有识别调用结束后只执行一次 `CreateFeatures` |
| `--recognition-sequence <列表>` | 诊断用识别掩码序列，例如 `0x3F,0x02`；先累积全部识别调用，最后只建树一次 |
| `--interactive-feature <名称>` | 诊断 `RecognizeFeatureInteractive` 的英文 FeatureType，例如 SDK 示例中的 `Fillet` |
| `--import-diagnosis` | 识别前调用 `IPartDoc.ImportDiagnosis(true,true,true,0)` |
| `--native-recognition-command` | 诊断 `RunCommand(1999/-2)` 原生 PropertyManager 路径；未通过无人值守门禁，不得用于生产 |
| `--skip-recognize` | 跳过特征识别，只做 V1.0 的导入+保存，用于对照 |
| `--skip-fully-define` | 跳过草图完全定义，用于隔离比较识别参数 |
| `--prepare-xt-from <sldprt>` | 先把一个原生零件导出成 `--input` 指定的 `.x_t`，用于构造对照样件 |
| `--inspect <sldprt>` | 独立重开 SLDPRT，打印特征树与草图约束状态 |
| `--expect-core-signature <列表>` | 与 `--inspect` 配合，按顺序精确断言核心类型；不一致时退出码为 1 |
| `--exit-owned <pid>` | 验收清理：仅当系统中唯一 SW PID 与参数一致时调用官方 `ExitApp` |

输出为结构化 JSON，包含识别数量、每个草图的完全定义前后约束状态、各阶段耗时。

`--exit-owned` 不按进程名强杀。PID 数量或值不完全匹配时命令拒绝执行，用于避免误关用户已经打开的 SolidWorks 会话。

## V3.6.2 安全合同

1. 普通零件使用 `0x3F`，其中 `fwVolume=0x02` 必须开启，钣金四位 `0x3C0` 必须关闭。
2. 自动识别前必须清空选择。对任意首面设置 `mark=8` 会让两个真实样件连续返回 0；
   `mark=8` 只适用于厂商示例中已知语义的特定目标面，不能当作通用种子面。
3. FeatureWorks 重建仍可能产生约 `1.42e-5` 的体积偏差，几何门禁使用 `2e-5`
   相对上限，并同时报告实体数、面数和边界盒。
4. 创建后的树只要残留 `BaseBody`、`ImportedBody` 或 `Imported`，就不是手工等价识别，必须丢弃并
   从 XT 干净重导入为哑实体。
5. `消解加热板` 的手工等价自动识别仍未通过。交互式 Volume、创建选项、ImportDiagnosis 和原生
   RunCommand 的排除证据仅用于诊断，不能迁入 Worker。

最终证据和两个样件的特征树见
[FeatureWorks 识别参数历史记录](../../../b-Office/history/38-V3.6.2-FeatureWorks识别参数错误.md)。
