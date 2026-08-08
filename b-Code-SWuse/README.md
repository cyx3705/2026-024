# SWuse SolidWorks C# 实验模块

> 状态：V0.1.0 已完成离线 Smoke 与真实 CAD 基线验收；尚未部署。  
> 宿主：OneHistoryStudio / AppShell 服务宿主。  
> 定位：独立顶层窗口中的静态 C# 零件构建实验环境。

SWuse 不是 SolidWorks 的全量二次开发封装，也不是不受限脚本执行器。用户在工作区编写多个 C# 类，
仅引用 `SWuse.Api`；Worker 在独立 x64 STA 进程中编译代码，通过受控 API 调用 SolidWorks COM 创建
`.SLDPRT`。V0.1 先覆盖零件、前视基准面草图、线/圆/矩形、凸台拉伸、切除拉伸和保存。

窗口是**自持顶层窗口**：AppShell 桌面 Shell 侧弃权，无窗服务宿主侧创建 `swuse` 窗口。它不嵌入 OHS 主窗口、
不使用停靠布局，并按普通桌面窗口显示在任务栏；未来可以替换宿主适配层而独立发布。

文档与实验记录只存放在本模块 `docs/`，不进入 OHS `b-Office/evidence`。

## 工程布局

```text
src/SWuse.Api          用户代码唯一可引用的受控建模 API
src/SWuse.Contracts    UI 与 Worker 的请求、结果和 JSON 协议
src/SWuse.Worker       x64 STA 编译与 SolidWorks COM 执行进程
src/SWuse              自持窗口、工作区编辑器和模块入口
tests/SWuse.Smoke      不启动 CAD 的 API、编译与模块生命周期 Smoke
tools/SWuse.CadGate    真实 CAD 生成、重开、哈希与进程清理门禁
build/                 唯一版本源
```

## V0.1 安全边界

代码只在 Worker 中执行，不在 OHS / AppShell 宿主中执行；编译引用只加入 .NET 运行库和 `SWuse.Api`。
这限制了模块依赖面，但**不是运行不可信代码的安全沙箱**：工作区代码仍以当前 Windows 用户权限执行。V0.1
只适用于用户自己编写、审阅的本地代码，不能打开来源不明的工作区。

## 当前文档

- [当前构建合同](docs/00-当前构建合同.md)
- [V0.1 架构与 API 设计](docs/01-V0.1-架构与API设计.md)
- [V0.1 实施与验收](docs/02-V0.1-实施与验收.md)
- [V0.1 发布收口](docs/03-V0.1-发布收口.md)

## 构建与发布前门禁

```powershell
.\eng\Publish-SWuse.ps1
```

脚本只构建、同步清单版本、运行离线 Smoke 并核验清单产物；它不会执行 `tool.sync`。部署到 OHS 正式槽需要明确授权后再单独进行。
