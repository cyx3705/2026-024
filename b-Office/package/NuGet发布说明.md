# SE2SW NuGet 发布说明

## 当前状态

SE2SW 3.6.7 尚未声明 NuGet 包元数据，`project.manifest.json.commands.package` 因此保持 `null`。
当前 OHS 模块发布不等同于 NuGet 发布，不能直接把模块槽目录打成包。

## 可发布边界

首选候选是 `b-Code-SE2SW/src/SE2SW.Contracts`：它包含稳定的请求、事件和纯逻辑合同，不依赖 CAD COM。
UI 模块 `SE2SW` 依赖 AppShell 与独立 Worker，只有在运行时资产布局、宿主兼容范围和 Worker 分发方式形成
明确合同后才能作为独立 NuGet 包。真实 CAD 探针、Smoke、厂商 Interop 和 OHS 模块清单不得进入 Contracts 包。

## 启用 NuGet 前必须完成

- 在候选项目中明确 `PackageId`、作者、描述、许可证、仓库地址、README 和符号包策略。
- 包版本继续从 `b-Code-SE2SW/build/SE2SW.Version.props` 派生，不新增第二版本源。
- 明确公共 API 兼容策略，并增加针对打包后程序集的消费测试。
- 将包输出写入可重建的 `b-Publish/NuGet`，不得提交 `bin/obj`。
- 配置目标 feed、凭据注入和重复版本拒绝规则；密钥不得进入仓库。

## 预发布门禁

元数据实施后，发布流程固定为：严格项目合同、Release 构建、独立 Smoke、`dotnet pack`、在临时消费项目中
从生成的 `.nupkg` 恢复并执行 API Smoke，最后才允许 `dotnet nuget push`。

任何正式推送都需要用户单独明确授权。未完成上述准备前，不设置 manifest 的 `package` 命令，也不生成
看似可发布但缺少兼容合同的包。
