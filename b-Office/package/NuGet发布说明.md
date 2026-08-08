# HistoryMinerva NuGet 发布说明

## 当前状态

HistoryMinerva 4.2.0 尚未声明 NuGet 包元数据。`project.manifest.json.commands.package` 当前只生成
AppShell 模块正式发布目录 `z-HistoryMinerva`，不代表 NuGet 打包或推送能力已经开放；部署槽和
`z-HistoryMinerva` 也不能直接改造成 `.nupkg`。

## 可发布边界

首选候选为 `b-Code-HistoryMinerva/src/HistoryMinerva.Contracts`：它包含稳定的请求、事件、身份常量
和纯逻辑合同，不依赖 CAD COM。UI 模块直接消费 AppShell 3.1.9 正式宿主快照，只有明确运行时资产、
宿主兼容范围和 Worker 分发合同后，才能作为独立 NuGet 包。真实 CAD 探针、Smoke、厂商 Interop 和
模块部署清单不得进入 Contracts 包。

## 启用前门禁

- 声明 `PackageId`、作者、描述、许可证、仓库、README 和符号包策略。
- 版本继续从 `b-Code-HistoryMinerva/build/HistoryMinerva.Version.props` 派生，不新增版本真值。
- 增加打包后临时消费工程的 API Smoke，证明包不意外携带 AppShell 或厂商 DLL。
- 配置独立输出、目标 feed、凭据注入和重复版本拒绝规则；密钥不得进入仓库。

任何 NuGet 推送都需要用户单独明确授权。未完成上述准备前，不生成容易被误认为可发布的 `.nupkg`；
AppShell 模块发布和 NuGet 包必须使用不同命令与输出目录。
