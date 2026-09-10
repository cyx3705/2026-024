using HistoryVulcan.Core.Commands;

namespace HistoryMinerva;

/// <summary>
/// 页面按钮到 ViewModel 的三行胶水。
///
/// **这里不再抢先报一句进行时**。V4.8 之前每条命令入口都先
/// <c>command.Progress?.Report(viewModel.OperationText)</c>，而 <c>OperationText</c> 要等
/// <c>StartOperationAsync</c> 置上 <c>IsProbing</c> 之后才说得对——于是点「解析装配体」
/// 时控制台第一行是「正在写入」。真正的进行时由操作自己在知道自己是什么之后报
/// （DEC-060）。
/// </summary>
internal static class ConversionCommandHandlers
{
    internal static async Task<CommandResult> ProbeAsync(
        AssemblyViewModel viewModel,
        CommandContext command)
    {
        try
        {
            await viewModel.ProbeAsync(command.Progress).ConfigureAwait(true);
            // V4.10.2：结论文本之外再带上探查结果本身（G-2）。消费方引用
            // HistoryMinerva.Contracts 就能强转成 AssemblyProbeResult，不必解析中文。
            return viewModel.LastOperationCanceled
                ? CommandResult.Fail("Minerva 解析已取消。")
                : viewModel.LastOperationSucceeded
                    ? CommandResult.Ok(viewModel.StatusText, viewModel.LastProbeResult)
                    : CommandResult.Fail(viewModel.StatusText);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("Minerva 解析已取消。");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    internal static async Task<CommandResult> ConvertAsync(
        AssemblyViewModel viewModel,
        CommandContext command)
    {
        try
        {
            await viewModel.ConvertAsync(command.Progress).ConfigureAwait(true);
            // 读 ResultText 而不是 StatusText：后者排在 UI 队列里，这一刻很可能还是
            // 那句「正在写入……」，而这是给用户看的结论行（DEC-060）。
            // V4.10.2：带上本次实际执行的那份计划（G-2）。属性整备是 AssemblyRenamePlan、
            // 整体打包是 PackagePlan，其余三种转换没有对应的公开计划类型，仍然只有文本。
            return viewModel.LastOperationCanceled
                ? CommandResult.Fail("Minerva 转换已取消。")
                : viewModel.LastOperationSucceeded
                    ? CommandResult.Ok(viewModel.ResultText, viewModel.LastExecutedPlan)
                    : CommandResult.Fail(viewModel.ResultText);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("Minerva 转换已取消。");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }
}
