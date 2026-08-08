# HistoryMinerva AppShell / OHS 模块发布说明

## 当前正式宿主

- AppShell `3.1.9`：`../2026-023-AppShell/z-Package-AppShell`
- 宿主 manifest / AppShell.Core 的 SHA256 钉定值：以
  `b-Code-HistoryMinerva/build/HistoryMinerva.Version.props` 为唯一真源，文档不保留哈希副本
- 发布目录（清单 + 运行产物 + SHA256SUMS + appshell.snapshot）：`z-HistoryMinerva`

## 构建正式发布

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-HistoryMinerva\eng\Build-HistoryMinervaPackage.ps1
```

脚本验证宿主版本、manifest SHA、Core SHA、Release 构建和模块清单版本，重建 `z-HistoryMinerva`：
十个运行产物（含合并 Worker 与 Roslyn）、`module.manifest.json`、`appshell.snapshot.json` 与
`SHA256SUMS`。包内出现 `AppShell*.dll` 即失败。

## 双槽部署

默认仅预览：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-HistoryMinerva\eng\Deploy-HistoryMinerva.ps1
```

关闭 AppShell 前端与服务端后显式执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-HistoryMinerva\eng\Deploy-HistoryMinerva.ps1 -Apply
```

目标为 `%AppData%\AppShell\Modules\HistoryMinerva` 与 `%AppData%\AppShell\service\Modules\HistoryMinerva`。
脚本先在 Modules 外建立备份和暂存目录，部署后逐文件核验发布目录 SHA；两个槽都成功后，退役全部旧槽
（`Mapping`、`SWuse` 双槽与 `%AppData%\OneHistoryStudio\Modules\SE2SW`/`SWuse`），备份移到对应
`module-backups` 区，均位于 Modules 目录之外。

部署后由运行中的 AppShell 核验 `module.list` 为 HistoryMinerva 4.2.0、`command.list domain=HistoryMinerva`
完整（convert/cancel + show/hide/status），MCP 不出现 convert/cancel 两个前端写命令。
