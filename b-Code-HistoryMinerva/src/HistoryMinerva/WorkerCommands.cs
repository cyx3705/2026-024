using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>后台 Worker 状态命令。转换执行走前端 <c>minerva.conversion.*</c>。</summary>
public sealed class WorkerCommands : IModuleContextAware
{
    private MappingRuntimePaths _runtimePaths = MappingRuntimePaths.CreateAppShellFallback();
    private bool _attached;

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_attached)
            throw new InvalidOperationException("HistoryMinerva worker command context has already been attached.");

        _runtimePaths = new MappingRuntimePaths(
            context.DataDirectory,
            context.Settings.Get("module.dir"));
        _attached = true;
        context.RegisterCommands(registry =>
        {
            registry.Register(ReadOnly(Command("show"), "报告 Minerva 工作区入口状态", Show));
            registry.Register(ReadOnly(Command("hide"), "报告 Minerva 工作区隐藏状态", Hide));
            registry.Register(ReadOnly(Command("status"), "查询 Minerva Worker 是否就绪", Status));
            registry.Register(ReadOnly(Command("path"), "查询 Minerva Worker 的实际定位路径", () => Path));
            registry.Register(ReadOnly(Command("capabilities"), "查询 Minerva Worker 支持的协议能力", Capabilities));
        });
    }

    private static string Command(string method)
        => $"{HistoryMinervaIdentity.CommandRoot}.worker.{method}";

    private static CommandDescriptor ReadOnly(string name, string summary, Func<string> handler)
        => new()
        {
            Name = name,
            Domain = HistoryMinervaIdentity.CommandRoot,
            CommandClass = "worker",
            Summary = summary,
            Example = name,
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(handler())),
        };

    public string Show()
        => $"{HistoryMinervaIdentity.WindowTitle} 工作区由 Vulcan 中央页面承载；Worker 通过命令总线提供状态。";

    public string Hide()
        => $"{HistoryMinervaIdentity.WindowTitle} 没有独立窗口可隐藏。";

    public string Status()
    {
        var workerPath = WorkerPath;
        return File.Exists(workerPath)
            ? $"{HistoryMinervaIdentity.Name}: Worker 就绪 ({workerPath})。"
            : $"{HistoryMinervaIdentity.Name}: 未找到 {HistoryMinervaIdentity.WorkerFileName}，请重新部署模块。";
    }

    public string Path => WorkerPath;

    public string Capabilities()
        => "worker.protocol=assembly-probe,batch-convert,part-convert,assembly-rename,cancel; runtime=single-process-worker";

    private string WorkerPath => WorkerLocator.Locate(_runtimePaths);
}
