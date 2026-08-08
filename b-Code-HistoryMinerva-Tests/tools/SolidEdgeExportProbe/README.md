# SolidEdgeExportProbe

Solid Edge 2020 `.par -> .x_t` COM 导出探针。只验证 SE2SW 转换链的第一段，不涉及 SolidWorks、
不涉及 WPF、不涉及 OHS 模块宿主。

## 构建

**必须用 Visual Studio 的 MSBuild.exe 构建，`dotnet build` 会失败。**
`<COMReference>` 依赖 `ResolveComReference` 任务，该任务在 .NET Core 版 MSBuild 中不存在：

```
error MSB4803: The task "ResolveComReference" is not supported on the .NET Core version of MSBuild.
```

构建命令：

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" SolidEdgeExportProbe.csproj /t:Restore;Build /p:Configuration=Release
```

产物：`bin\Release\net8.0-windows\win-x64\SolidEdgeExportProbe.exe`

COM 引用按 TypeLib GUID 从注册表解析，构建机必须已安装并注册 Solid Edge，但不硬编码任何安装路径。

## 运行

```bash
SolidEdgeExportProbe.exe --input "D:\in\part.par" --output "D:\out\part.x_t" --method savebody
```

| 参数 | 说明 |
|---|---|
| `--input <路径>` | 绝对路径的 `.par` 源文件 |
| `--output <路径>` | 绝对路径的目标文件；已存在则拒绝，不覆盖 |
| `--method saveas\|savebody` | 默认 `savebody`（`PartDocument.SaveBody`）；`saveas` 为 `SolidEdgeDocument.SaveAs` |
| `--parasolid-version <n>` | `0`=当前版本（默认），或 `70/71/80/90/91/100/101/110/111/120/121/130` |
| `--binary` | 导出二进制 Parasolid，仅对 `savebody` 生效 |
| `--retry-budget-ms <n>` | 调用被拒绝时的重试总预算，默认 60000 |
| `--stage-timeout-ms <n>` | 单阶段超时，默认 180000 |
| `--self-cancel-after-ms <n>` | 指定毫秒后自动取消，用于复现取消路径 |
| `--keep-app-alive` | 调试用：不退出探针自己创建的 Solid Edge 实例 |

退出码：`0` 成功 / `1` 探针失败 / `2` 参数错误。标准输出是结构化 JSON。

## 行为约定

- 只处理调用方给定的文件，绝不修改源 `.par`（每次运行都回读源文件 SHA-256 并报告 `SourceUnchanged`）。
- 前置检查全部通过后才启动 Solid Edge：输入存在且为 `.par`、输入输出不同路径、输出不存在、
  输出目录真实可写（写一个 canary 文件验证）、源文件未被独占。
- 通过 `SolidEdge.Application` ProgID 创建实例，并用创建前后的 `Edge.exe` PID 快照判断是否
  真的新建了进程。只有新建的进程才会被 `Quit()`；复用到用户会话时绝不退出。
- 注册带总时限和取消支持的 `IOleMessageFilter`，退出前撤销并还原线程原有过滤器。
- `DisplayAlerts` 的原值会被保存并在 `finally` 中还原。
- 释放顺序固定为 Document → Documents → Application，每个 RCW 调用
  `Marshal.FinalReleaseComObject`；不依赖 GC，也不按进程名杀 Solid Edge。
- 成功判据不是"没抛异常"：还要求输出文件存在、非空、连续 3 次采样长度不变（写入已稳定），
  并解析 Parasolid 头部的 `FORMAT=` 与 `TRANSMIT FILE created by modeller version`。

## 注意：中文路径

不要用 Windows PowerShell 5.1 脚本传中文参数。PS 5.1 按控制台代码页转换传给原生 exe 的参数，
中文会变成乱码（本次预研实测踩到）。正式实现应使用
`ProcessStartInfo.ArgumentList`（UTF-16 直传）启动工作进程。探针本身对中文路径没有问题。
