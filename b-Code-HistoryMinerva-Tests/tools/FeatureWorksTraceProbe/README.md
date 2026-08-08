# FeatureWorksTraceProbe

只读附着现有 SolidWorks，记录手工 FeatureWorks 操作的 App 级命令事件、活动命令、选择集和一级特征树变化。
探针不加载或卸载插件，不保存或关闭文档，也不退出附着的 SolidWorks。

为避免观察器干扰 CAD 生命周期，探针不对 SolidWorks 返回的文档、特征、选择管理器或插件对象调用
`Marshal.FinalReleaseComObject`，也不订阅 PartDoc 事件。文档和特征变化由 App 事件加 100 ms 轮询捕获。

```powershell
FeatureWorksTraceProbe.exe --output trace.jsonl --timeout-seconds 900
```

没有 SolidWorks 时会等待；出现一个实例后按 `SolidWorks_PID_<pid>` ROT 名称附着。多个实例时必须用
`--pid <pid>` 消歧。控制台输出 `READY` 后再开始手工操作；按 Ctrl+C 或等待超时即可结束。
