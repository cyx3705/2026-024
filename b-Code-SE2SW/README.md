# SE2SW 模块工程

> 当前版本：3.6.7  
> 宿主：OneHistoryStudio / AppShell  
> 发布清单：`../z-SE2SW/module.manifest.json`

SE2SW 使用 Solid Edge COM 导出 Parasolid，再由 SolidWorks COM 导入、识别并保存为 SolidWorks
零件或装配体。模块保留单页来源自适应 UI、独立 x64 STA Worker、离线 Smoke 和真实 CAD 门禁。

## 工程布局

```text
src/SE2SW.Contracts   请求、进度、路径和装配协议
src/SE2SW             WPF UI、扫描、预检和模块入口
src/SE2SW.Worker      Solid Edge / SolidWorks COM 工作进程
tests/SE2SW.Smoke     不启动 CAD 的自动化回归
tests/SE2SW.UiSmoke   窗口渲染冒烟壳
tools/                COM 探针和真实 CAD 生产门禁
build/                唯一版本源
eng/                  模块清单版本同步脚本
```

## 当前边界

- 文件夹来源只扫描顶层 `.par`；装配来源选择单个 `.asm` 并保留嵌套层级。
- 外界模式输出到源目录的 `XT/` 和 `SW/`；探查阶段不创建目录。
- 可选 FeatureWorks 识别带语义与几何守卫，失败时降级为几何完整的哑实体。
- 可选装配关系重建逐条校验位置，超差即回滚并恢复组件姿态。
- 源 CAD 文件只读；模块只回收自己明确创建的 CAD 进程。
- MCP 暴露为 `hidden`，转换只能由本机 UI 明确触发。

完整现行合同、决策、验证和下一版本计划位于 [`../b-Office/current`](../b-Office/current/项目概览.md)。
版本研究与验收记录位于 [`../b-Office/history/SE2SW`](../b-Office/history/SE2SW/)。

## 构建与离线验证

从项目根目录执行：

```powershell
dotnet build .\b-Code-SE2SW\src\SE2SW\SE2SW.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-SE2SW\tests\SE2SW.Smoke\SE2SW.Smoke.csproj -c Release -p:NuGetAudit=false
```

更新唯一版本源后，通过 `eng/Update-SE2SWManifest.ps1` 同步清单版本。正式入槽必须显式执行
OHS `tool.sync name=SE2SW`。
