# Mapping AppShell / OHS 模块发布说明

## 当前正式宿主

- AppShell `3.1.9`：`../2026-023-AppShell/z-Package-AppShell`
- 宿主 manifest SHA256：`9D27B06100988DE89547931201E5612487C5ADA3C14C9D7277BFFA1C1450E72A`
- AppShell.Core SHA256：`0EA9D28EEE8AB28A20679042B937A4C94A288177B543602E50C30EDC2115A58B`
- 模块输入清单：`z-SE2SW/module.manifest.json`
- 正式输出：`z-Package-Mapping`

## 构建正式包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-SE2SW\eng\Build-MappingPackage.ps1
```

脚本验证宿主版本、manifest SHA、Core SHA、Release 构建和模块清单版本，生成七个 SE2SW 运行产物、
`module.manifest.json`、`appshell.snapshot.json` 与 `SHA256SUMS`。包内出现 `AppShell*.dll` 即失败。

## 双槽部署

默认仅预览：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-SE2SW\eng\Deploy-Mapping.ps1
```

关闭 AppShell 前端与服务端后显式执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-SE2SW\eng\Deploy-Mapping.ps1 -Apply
```

目标为 `%AppData%\AppShell\Modules\Mapping` 与 `%AppData%\AppShell\service\Modules\Mapping`。脚本先在
Modules 外建立备份和暂存目录，部署后逐文件核验正式包 SHA；两个槽都成功后，才把旧
`%AppData%\OneHistoryStudio\Modules\SE2SW` 移到 `%AppData%\OneHistoryStudio\module-backups`。

部署后由运行中的 AppShell 核验 `module.list` 为 Mapping 4.1.0、`command.list domain=mapping` 完整，MCP 只出现只读查询。
