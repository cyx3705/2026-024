# HistoryMinerva

HistoryMinerva 是独立维护的 AppShell CAD 模块集项目，同仓承载两个注册到 AppShell 宿主的模块：

- **Mapping 4.1.0**：文件映射模块，提供受控的 Solid Edge `.par/.asm` 到 SolidWorks
  `.SLDPRT/.SLDASM` 转换；对外模块名、窗口、命令域与 MCP 名称均为 `Mapping` / `mapping`。
- **SWuse 0.1.1**：SolidWorks C# 静态零件构建实验环境（独立顶层窗口），命令域 `swuse`，
  用户多文件 C# 经受控 `SWuse.Api` 生成 `.SLDPRT`。

两模块共用 Contracts + 独立 x64 STA Worker 骨架，版本、清单和发布链各自独立。

## Mapping

用户界面只配置两件事：转换来源与转换内容。当前可选映射为：

用户界面只配置两件事：转换来源与转换内容。当前可选映射为：

- Solid Edge `.par` → SolidWorks `.SLDPRT`（选择含顶层 `.par` 的文件夹）
- Solid Edge `.asm` → SolidWorks `.SLDASM`（选择单个 `.asm` 文件）

内部仍使用 Parasolid `.x_t` 和独立 x64 STA Worker 完成 CAD COM 自动化；XT 是受控管线细节，
不再作为界面配置项。源 CAD 文件保持只读，产物仍写入来源目录下的 `XT/` 与 `SW/`。

## 入口

| 内容 | 位置 |
| --- | --- |
| 项目身份、路径和命令 | [`project.manifest.json`](./project.manifest.json) |
| 项目状态 | [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) |
| 需求与架构 | [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) |
| Mapping 4.1.0 AppShell 迁移 | [`b-Office/current/V4.1.0-AppShell迁移计划.md`](./b-Office/current/V4.1.0-AppShell迁移计划.md) |
| 验证方式 | [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) |
| 生产模块源码（Mapping） | [`b-Code-SE2SW/README.md`](./b-Code-SE2SW/README.md) |
| 独立测试与 CAD 门禁 | [`b-Code-SE2SW-Tests/README.md`](./b-Code-SE2SW-Tests/README.md) |
| AppShell 模块清单（Mapping） | [`z-SE2SW/module.manifest.json`](./z-SE2SW/module.manifest.json) |
| SWuse 模块源码 | [`b-Code-SWuse/README.md`](./b-Code-SWuse/README.md) |
| AppShell 模块清单（SWuse） | [`z-SWuse/module.manifest.json`](./z-SWuse/module.manifest.json) |
| NuGet/OHS 发布说明 | [`b-Office/package/复用说明.md`](./b-Office/package/复用说明.md) |
| 模块合并计划 | [`b-Office/history/42-V4.1.0-HistoryMinerva模块合并计划.md`](./b-Office/history/42-V4.1.0-HistoryMinerva模块合并计划.md) |

## 快速验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.UiSmoke\SE2SW.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --capture .\artifacts\mapping-ui.png
dotnet build .\b-Code-SWuse\src\SWuse\SWuse.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SWuse\tests\SWuse.Smoke\SWuse.Smoke.csproj -c Release -p:NuGetAudit=false
```

真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。

作者：Pinavia
