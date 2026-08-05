# AssemblyProductionGate

这是 V3.0 生产 Worker 的真实 CAD 收口门禁，不属于模块运行时。它依次执行
`--probe-assembly` 与 `--assembly`，并独立重开最终 `.SLDASM` 验证组件引用、固定状态、
位置和姿态；同时核验源 `.asm/.par` 哈希、输出哈希和 CAD 进程收束。

普通完整门禁要求传入物理 `.asmdot`，执行期间临时设置用户偏好并在 `finally` 中恢复原值。
V3.0.2 另提供 `--reuse-existing` 安全重试门禁：它不修改默认模板，专门验证生产 Worker 能从虚拟 token
自动回退到官方物理模板，并确认已有 XT/SLDPRT 的哈希不变。

门禁拒绝复用 `.asm` 所在目录中已有的 `XT` / `SW`，请使用干净 fixture：

```powershell
dotnet run --project .\tools\AssemblyProductionGate\AssemblyProductionGate.csproj -c Release -- `
  --se-asm "<绝对.asm>" `
  --worker ".\src\SE2SW.Worker\bin\Release\net8.0-windows\win-x64\SE2SW.Worker.exe" `
  --assembly-template "<探针生成的物理.asmdot>"
```

已有分层产物的安全重试门禁要求目标 SLDASM 尚不存在：

```powershell
dotnet run --project .\tools\AssemblyProductionGate\AssemblyProductionGate.csproj -c Release -- `
  --se-asm "<绝对.asm>" `
  --worker ".\src\SE2SW.Worker\bin\Release\net8.0-windows\win-x64\SE2SW.Worker.exe" `
  --reuse-existing
```
