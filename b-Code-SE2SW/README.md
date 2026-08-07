# Mapping 生产模块工程

> 对外模块：`Mapping` / `mapping`
>
> 当前版本：`4.1.0`
>
> 发布清单：`../z-SE2SW/module.manifest.json`

本目录保留 SE2SW 作为内部程序集、Worker 和 JSON 协议名称，以维持既有 CAD 转换实现的稳定性；
对外显示、窗口、指令与 MCP 均称为 Mapping。模块使用 Solid Edge COM 导出 Parasolid，再由
SolidWorks COM 导入、识别并保存为零件或装配体。

界面只让用户选择转换来源和转换内容：`.par → .SLDPRT` 或 `.asm → .SLDASM`。内部 XT、SW
输出目录沿用受控默认规则，不提供 UI 配置入口。测试、UI Smoke、COM 探针与真实 CAD 门禁由独立的
[`b-Code-SE2SW-Tests`](../b-Code-SE2SW-Tests/README.md) 维护。

## 工程布局

```text
src/SE2SW.Contracts   请求、进度、路径和装配协议
src/SE2SW             WPF UI、扫描、预检、Mapping 模块入口
src/SE2SW.Worker      Solid Edge / SolidWorks COM 工作进程
build/                唯一版本源与 AppShell 3.1.9 路径
eng/                  清单同步、正式包和双槽部署脚本
```

## 构建与离线验证

从项目根目录执行：

```powershell
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW-Tests\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
```

生产工程直接引用 `2026-023-AppShell/z-Package-AppShell/host/AppShell.Core.dll` 3.1.9，宿主 DLL 不随模块包复制。
只修改 `build/SE2SW.Version.props` 后，通过 `eng/Update-SE2SWManifest.ps1` 同步清单版本；正式包由
`eng/Build-MappingPackage.ps1` 生成，部署使用 `eng/Deploy-Mapping.ps1 -Apply`。只读命令域为 `mapping`。
