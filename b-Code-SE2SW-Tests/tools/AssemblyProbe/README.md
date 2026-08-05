# AssemblyProbe

SE2SW V3.0 的装配 COM 门禁探针。它只用于确认 Solid Edge occurrence 矩阵与 SolidWorks
`MathTransform` 的真实映射，不作为生产转换入口。

```powershell
dotnet run --project .\tools\AssemblyProbe\AssemblyProbe.csproj -c Release -- `
  --se-asm "<绝对 .asm 路径>" `
  --gold-sw "<对应金标准 .SLDASM 路径>" `
  --sw-parts "<对应 .SLDPRT 所在目录>" `
  --build-output "<新的 .SLDASM 输出路径>"
```

探针读取 SE occurrence 的 `GetMatrix`，分别尝试直接/累乘与旋转转置/不转置四种映射，
用已有 SW 金标准确定唯一最小误差布局，再实际执行 `NewDocument`、`AddComponent5`、
`Transform2`、`FixComponent` 和 `SaveAs3`。输出 JSON 包含原始矩阵、金标准矩阵、最终映射、
固定状态和进程收束信息。

2026-08-01 实测结论：

- SE 旋转位于 `0,1,2 / 4,5,6 / 8,9,10`，平移位于 `12..14`；
- SW 旋转位于 `0..8`，平移位于 `9..11`，`12=1`；旋转不转置；
- `SubOccurrence.GetMatrix` 已经是顶层世界矩阵，禁止与父矩阵再次累乘；
- SE 2020 occurrence 没有可持久化 `Suppress` 属性，`Activate` 不是抑制状态；
- 当前工作站默认返回虚拟模板 `~BLANK_ASSY_TEMPLATE.asmdot`，`NewDocument` 不接受它；探针可从
  `--blank-sw` 指定的空 SLDASM 生成物理 `.asmdot` 完成验证。

受控 fixture 开关（互斥）：

- `--fixture-single`：L0 单实例；
- 无开关：L1 重复实例；
- `--fixture-nested`：L2 两层子装配；
- `--fixture-states`：隐藏与 Activate 持久化边界；
- `--fixture-missing`：缺失引用；
- `--fixture-duplicates`：同名不同路径引用。

生产 Worker 的端到端验收由 `tools/AssemblyProductionGate` 完成；它会独立重开最终 SLDASM、核验引用、
固定和矩阵，并临时设置后恢复物理装配模板。

探针拒绝覆盖输出文件。若附着到用户已打开的 Solid Edge 或 SolidWorks 会话，不调用退出命令。
