using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed class HistoryMinervaUiModule : IModuleContextAware, IDisposable
{
    private IModuleContext? _context;
    private MappingRuntimePaths? _runtimePaths;
    private HistoryMinervaWorkspaceView? _workspace;

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null)
            throw new InvalidOperationException("Minerva UI 宿主上下文已注入。");

        _context = context;
        _runtimePaths = MappingRuntimePaths.CreateHistoryVulcanDefault();

        // Must register during Attach. Vulcan FinalizeMetas runs before CreateUi
        // injects ShellUi, so gating on _shellUi drops conversion commands after
        // reload and the page reports 未知指令.
        context.RegisterCommands(registry =>
        {
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.probe",
                CommandClass = "conversion",
                Summary = "通过命令总线探查当前选择的 Minerva 装配体",
                Example = "minerva.conversion.probe",
                Readonly = true,
                RequiresUiThread = true,
                Handler = ProbeCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.run",
                CommandClass = "conversion",
                Summary = "通过命令总线执行当前选择的 Minerva 转换",
                Example = "minerva.conversion.run",
                RequiresUiThread = true,
                Handler = ConvertCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.strip",
                CommandClass = "conversion",
                Summary = "通过命令总线按空格洗掉当前装配体的图号",
                Example = "minerva.conversion.strip",
                RequiresUiThread = true,
                Handler = StripCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.cancel",
                CommandClass = "conversion",
                Summary = "取消当前 Minerva 转换或探查",
                Example = "minerva.conversion.cancel",
                RequiresUiThread = true,
                Handler = CommandDescriptor.Sync(CancelCurrent),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.pane",
                CommandClass = "ui",
                Summary = HistoryMinervaIdentity.WindowTitle,
                Readonly = true,
                RequiresUiThread = true,
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ui.window"] = HistoryMinervaIdentity.WindowId,
                    ["ui.side"] = "center",
                    ["ui.title"] = HistoryMinervaIdentity.WindowTitle,
                    ["ui.ratio"] = "0.75",
                },
                Handler = CommandDescriptor.Sync(_ =>
                    CommandResult.Ok("Minerva 页面", GetWorkspace())),
            });
        });
    }

    private HistoryMinervaWorkspaceView GetWorkspace()
    {
        if (_workspace is null)
        {
            if (_context is null || _runtimePaths is null)
                throw new InvalidOperationException("Minerva 模块尚未装配。");
            _workspace = new HistoryMinervaWorkspaceView(_runtimePaths, _context.Bus);
        }

        return _workspace;
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
        _context = null;
        _runtimePaths = null;
    }

    private Task<CommandResult> ProbeCurrentAsync(CommandContext command)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return Task.FromResult(CommandResult.Fail("Minerva 页面尚未创建。"));
        if (!viewModel.CanProbe)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.ProbeAsync(viewModel, command);
    }

    private Task<CommandResult> ConvertCurrentAsync(CommandContext command)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return Task.FromResult(CommandResult.Fail("Minerva 页面尚未创建。"));
        if (!viewModel.CanConvert)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.ConvertAsync(viewModel, command);
    }

    private Task<CommandResult> StripCurrentAsync(CommandContext command)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return Task.FromResult(CommandResult.Fail("Minerva 页面尚未创建。"));
        if (!viewModel.CanStrip)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.StripAsync(viewModel, command);
    }

    private CommandResult CancelCurrent(CommandContext _)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return CommandResult.Fail("Minerva 页面尚未创建。");
        return viewModel.Cancel()
            ? CommandResult.Ok("已请求取消 Minerva 当前操作。")
            : CommandResult.Fail("Minerva 当前没有可取消的操作。");
    }
}
