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
            throw new InvalidOperationException("Mapping UI 宿主上下文已注入。");

        _context = context;
        _runtimePaths = new MappingRuntimePaths(
            context.DataDirectory,
            context.Settings.Get("module.dir"));
        context.Log.Info(HistoryMinervaIdentity.WindowId, $"UI 运行目录已接入 AppShell：{_runtimePaths.ModuleDataDirectory}");

        // 无窗服务宿主不会注入 ShellUi，因此只在前端注册依赖当前页面状态的命令。
        if (_shellUi is not null)
        {
            context.RegisterCommands(registry =>
            {
                registry.Register(new CommandDescriptor
                {
                    Name = HistoryMinervaIdentity.CommandDomain + ".convert",
                    CommandClass = "conversion",
                    Summary = $"转换 {HistoryMinervaIdentity.WindowTitle} 页面当前选择的来源",
                    Example = HistoryMinervaIdentity.CommandDomain + ".convert",
                    Readonly = false,
                    Dangerous = false,
                    RequiresUiThread = true,
                    AllowMcpExecution = false,
                    Handler = ConvertCurrentAsync,
                });
                registry.Register(new CommandDescriptor
                {
                    Name = HistoryMinervaIdentity.CommandDomain + ".cancel",
                    CommandClass = "conversion",
                    Summary = $"取消 {HistoryMinervaIdentity.WindowTitle} 页面当前转换或探查",
                    Example = HistoryMinervaIdentity.CommandDomain + ".cancel",
                    Readonly = false,
                    Dangerous = false,
                    RequiresUiThread = true,
                    AllowMcpExecution = false,
                    Handler = CommandDescriptor.Sync(CancelCurrent),
                });
            });
        }
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

    private async Task<CommandResult> ConvertCurrentAsync(CommandContext command)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return CommandResult.Fail("Mapping 页面尚未创建。");
        if (!viewModel.CanConvert)
            return CommandResult.Fail(viewModel.StatusText);

        _context?.Log.Info(HistoryMinervaIdentity.WindowId, $"开始执行 {viewModel.SelectedMappingContent.DisplayName}");
        command.Progress?.Report(viewModel.OperationText);
        await viewModel.ConvertAsync(command.Progress);
        _context?.Log.Info(HistoryMinervaIdentity.WindowId, viewModel.StatusText);
        return CommandResult.Ok(viewModel.StatusText);
    }

    private CommandResult CancelCurrent(CommandContext _)
    {
        var viewModel = _workspace?.UnifiedPage.ViewModel;
        if (viewModel is null)
            return CommandResult.Fail("Mapping 页面尚未创建。");
        return viewModel.Cancel()
            ? CommandResult.Ok("已请求取消 Mapping 当前操作。")
            : CommandResult.Fail("Mapping 当前没有可取消的操作。");
    }
}
