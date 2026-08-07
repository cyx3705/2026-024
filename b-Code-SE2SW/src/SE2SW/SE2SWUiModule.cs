using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Modules;

namespace SE2SW;

public sealed class SE2SWUiModule : IUiModule, IShellUiAware, IModuleContextAware
{
    private readonly List<IDisposable> _windows = [];
    private IShellUiRegistrar? _shellUi;
    private IModuleContext? _context;
    private MappingRuntimePaths? _runtimePaths;
    private SE2SWWorkspaceView? _workspace;

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
        context.Log.Info("mapping", $"UI 运行目录已接入 AppShell：{_runtimePaths.ModuleDataDirectory}");

        // 无窗服务宿主不会注入 ShellUi，因此只在前端注册依赖当前页面状态的命令。
        if (_shellUi is not null)
        {
            context.RegisterCommands(registry =>
            {
                registry.Register(new CommandDescriptor
                {
                    Name = "mapping.convert",
                    Summary = "转换 Mapping 页面当前选择的来源",
                    Example = "mapping.convert",
                    Readonly = false,
                    Dangerous = false,
                    RequiresUiThread = true,
                    AllowMcpExecution = false,
                    Handler = ConvertCurrentAsync,
                });
                registry.Register(new CommandDescriptor
                {
                    Name = "mapping.cancel",
                    Summary = "取消 Mapping 页面当前转换或探查",
                    Example = "mapping.cancel",
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

        _workspace = new SE2SWWorkspaceView(_runtimePaths, _context.Bus);
        _windows.Add(_shellUi.RegisterToolWindow(new ToolWindowDescriptor
        {
            Id = "mapping",
            Title = "Mapping",
            DefaultSide = DockSide.Center,
            DefaultRatio = 0.75,
            IsSingleton = true,
            ContentFactory = () => _workspace,
        }, "Mapping"));
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

        _context?.Log.Info("mapping", $"开始执行 {viewModel.SelectedMappingContent.DisplayName}");
        command.Progress?.Report(viewModel.OperationText);
        await viewModel.ConvertAsync(command.Progress);
        _context?.Log.Info("mapping", viewModel.StatusText);
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
