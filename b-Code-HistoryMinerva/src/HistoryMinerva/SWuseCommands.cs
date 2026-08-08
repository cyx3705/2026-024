using System.IO;
using HistoryVulcan.Core.Modules;
using SE2SW.Contracts;

namespace SWuse;

/// <summary>HistoryMinerva.show/hide/status：SWuse 窗口移除后的占位指令。</summary>
public sealed class SWuseCommands
{
    /// <summary>SWuse 独立窗口已在 4.2.0 移除，返回现状说明。</summary>
    [ModuleCommand(CommandClass = "worker")]
    public string Show() => $"SWuse 独立窗口已在 4.2.0 移除（界面待打磨）；映射转换请使用 {HistoryMinervaIdentity.WindowTitle} 停靠页，建模构建保留在 Worker 协议层。";

    /// <summary>无独立窗口可隐藏。</summary>
    [ModuleCommand(CommandClass = "worker")]
    public string Hide() => "SWuse 独立窗口已在 4.2.0 移除，无可隐藏窗口。";

    /// <summary>读取 HistoryMinerva Worker 状态。</summary>
    [ModuleCommand(Readonly = true, CommandClass = "worker")]
    public string Status()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, HistoryMinervaIdentity.WorkerFileName);
        return File.Exists(workerPath)
            ? $"{HistoryMinervaIdentity.Name}：Worker 就绪（{workerPath}）。"
            : $"{HistoryMinervaIdentity.Name}：未找到 {HistoryMinervaIdentity.WorkerFileName}，请重新部署模块。";
    }
}
