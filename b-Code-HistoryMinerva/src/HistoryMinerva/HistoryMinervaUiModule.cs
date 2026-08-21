using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed class HistoryMinervaUiModule : IUiModule, IShellUiAware, IModuleContextAware
{
    private readonly List<IDisposable> _windows = [];
    private IShellUiRegistrar? _shellUi;
    private IModuleContext? _context;
    private MappingRuntimePaths? _runtimePaths;
    private HistoryMinervaWorkspaceView? _workspace;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null)
            throw new InvalidOperationException("Minerva UI 宿主上下文已注入。");

        _context = context;
        _runtimePaths = new MappingRuntimePaths(context.DataDirectory, context.Settings.Get("module.dir"));
        context.Log.Info(HistoryMinervaIdentity.WindowId, $"Minerva UI 数据根：{_runtimePaths.ModuleDataDirectory}");

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
                AllowMcpExecution = false,
                Handler = ProbeCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.run",
                CommandClass = "conversion",
                Summary = "通过命令总线执行当前选择的 Minerva 转换",
                Example = "minerva.conversion.run",
                RequiresUiThread = true,
                AllowMcpExecution = false,
                Handler = ConvertCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.strip",
                CommandClass = "conversion",
                Summary = "通过命令总线按空格洗掉当前装配体的图号",
                Example = "minerva.conversion.strip",
                RequiresUiThread = true,
                AllowMcpExecution = false,
                Handler = StripCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.cancel",
                CommandClass = "conversion",
                Summary = "取消当前 Minerva 转换或探查",
                Example = "minerva.conversion.cancel",
                RequiresUiThread = true,
                AllowMcpExecution = false,
                Handler = CommandDescriptor.Sync(CancelCurrent),
            });
        });
    }

    public void CreateUi()
    {
        if (_shellUi is null || _context is null || _runtimePaths is null || _windows.Count != 0)
            return;

        _workspace = new HistoryMinervaWorkspaceView(_runtimePaths, _context.Bus);
        _windows.Add(_shellUi.RegisterToolWindow(new ToolWindowDescriptor
        {
            Id = HistoryMinervaIdentity.WindowId,
            Title = HistoryMinervaIdentity.WindowTitle,
            DefaultSide = DockSide.Center,
            DefaultRatio = 0.75,
            IsSingleton = true,
            ContentFactory = () => _workspace,
        }, HistoryMinervaIdentity.Name));
    }

    public void DestroyUi()
    {
        for (var index = _windows.Count - 1; index >= 0; index--)
            _windows[index].Dispose();
        _windows.Clear();
        _workspace?.Dispose();
        _workspace = null;
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
