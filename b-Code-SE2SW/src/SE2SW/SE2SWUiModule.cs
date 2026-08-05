using AppShell.Core.Modules;
using AppShell.Core.Docking;

namespace SE2SW;

public sealed class SE2SWUiModule : IUiModule, IShellUiAware
{
    private readonly List<IDisposable> _windows = [];

    public IShellUiRegistrar ShellUi { private get; set; } = null!;

    public void CreateUi()
    {
        // 无窗服务宿主不注入注册器。此时没有可停靠的宿主窗口，弃权而不是空引用。
        if (ShellUi is null)
            return;
        _windows.Add(ShellUi.RegisterToolWindow(new ToolWindowDescriptor
        {
            Id = "se2sw",
            Title = "SE2SW 转换",
            ContentFactory = static () => new SE2SWWorkspaceView(),
        }, "SE2SW"));
    }

    public void DestroyUi()
    {
        for (var index = _windows.Count - 1; index >= 0; index--)
            _windows[index].Dispose();
        _windows.Clear();
    }
}
