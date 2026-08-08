# SWuse.CadGate

真实 SolidWorks V0.1 门禁，不属于用户功能，也不随 OHS 模块清单部署。

它创建临时工作区，通过 Release `SWuse.Worker.exe` 运行矩形凸台加圆孔切除，再启动一次新的 SolidWorks COM 会话重开 `.SLDPRT`，验证 `BaseBoss`、`CenterHole`、源代码哈希和进程清理。

```powershell
dotnet run --project .\tools\SWuse.CadGate\SWuse.CadGate.csproj -c Release -p:NuGetAudit=false
```

启动时必须没有 `SLDWORKS` 进程，防止验收工具接触用户现有会话。默认删除临时文件；仅诊断失败时可追加 `--keep`。
