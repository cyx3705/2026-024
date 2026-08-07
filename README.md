# Mapping

Mapping 是独立维护的 AppShell 文件映射模块。当前交付以受控的
Solid Edge `.par/.asm` 到 SolidWorks `.SLDPRT/.SLDASM` 转换为起点；对外模块名、窗口、命令域
与 MCP 名称均为 `Mapping` / `mapping`，当前版本为 `4.1.0`。

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
| 生产模块源码 | [`b-Code-SE2SW/README.md`](./b-Code-SE2SW/README.md) |
| 独立测试与 CAD 门禁 | [`b-Code-SE2SW-Tests/README.md`](./b-Code-SE2SW-Tests/README.md) |
| AppShell 模块清单 | [`z-SE2SW/module.manifest.json`](./z-SE2SW/module.manifest.json) |
| NuGet/OHS 发布说明 | [`b-Office/package/复用说明.md`](./b-Office/package/复用说明.md) |

## 快速验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.UiSmoke\SE2SW.UiSmoke.csproj -c Release -p:NuGetAudit=false -- --width 320 --height 680 --dark --capture .\artifacts\mapping-ui.png
```

真实 CAD 门禁需要专用样件和受控 Solid Edge / SolidWorks 会话，不能用离线 Smoke 代替。

作者：Pinavia
