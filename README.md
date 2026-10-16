# HistoryMinerva

> CAD 转换模块：Solid Edge 转 SolidWorks、属性整备改名与零件采购打包

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
| 代码面 | `HistoryMinerva.Contracts.dll`（附 `.xml` 注释） | 图号、改名计划、打包计划、路径布局、Worker 协议等纯函数与 DTO |
| 总线面 | `minerva.plan.package` / `rename` | 无状态入口：给装配体路径，拿回完整打包计划 / 改名计划 |
| 总线面 | `minerva.conversion.probe` / `run` / `cancel` | 解析装配体、执行当前转换、取消 |
| 总线面 | `minerva.ui.content` / `source` / `options` | 页面状态：转换内容、来源、选项 |
| MCP | `minerva.worker.status` / `path` / `capabilities` / `show` / `hide` | Worker 就绪查询 |

跨模块与 AI 消费优先走无状态的 `plan.*`。完整命令目录与参数是注册时的自描述：`diana.docs.read domain=minerva`（宿主 6.1.0 起没有消费文档）。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：真相在哪、产品取舍、真机才知道的、已知缺口、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryMinerva/` | 生产模块：前端、Worker、Contracts 与 `eng/` 门禁脚本，见 [README](./b-Code-HistoryMinerva/README.md) |
| `b-Code-HistoryMinerva-Tests/` | Smoke、UiSmoke 与三个 CAD 真机门禁，见 [README](./b-Code-HistoryMinerva-Tests/README.md) |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | `current/现行约定.md`，仅此一份 |
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

## 保留内容

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。