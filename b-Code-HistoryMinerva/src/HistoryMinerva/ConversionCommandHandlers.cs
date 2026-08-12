using HistoryVulcan.Core.Commands;

namespace HistoryMinerva;

internal static class ConversionCommandHandlers
{
    internal static async Task<CommandResult> ProbeAsync(
        AssemblyViewModel viewModel,
        CommandContext command)
    {
        command.Progress?.Report(viewModel.OperationText);
        try
        {
            await viewModel.ProbeAsync(command.Progress).ConfigureAwait(true);
            return viewModel.LastOperationCanceled
                ? CommandResult.Fail("Minerva 探查已取消。")
                : viewModel.LastOperationSucceeded
                    ? CommandResult.Ok(viewModel.StatusText)
                    : CommandResult.Fail(viewModel.StatusText);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("Minerva 探查已取消。");
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
        command.Progress?.Report(viewModel.OperationText);
        try
        {
            await viewModel.ConvertAsync(command.Progress).ConfigureAwait(true);
            return viewModel.LastOperationCanceled
                ? CommandResult.Fail("Minerva 转换已取消。")
                : viewModel.LastOperationSucceeded
                    ? CommandResult.Ok(viewModel.StatusText)
                    : CommandResult.Fail(viewModel.StatusText);
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
