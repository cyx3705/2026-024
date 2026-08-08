using AppShell.Core.Modules;

namespace SWuse;

/// <summary>
/// 自持窗口生命周期。桌面 Shell 注入注册器时弃权；无窗服务宿主才拥有顶层 SWuse 窗口。
/// </summary>
public sealed class SWuseUiModule : IUiModule, IShellUiAware
{
    private IShellUiRegistrar? _shellUi;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void CreateUi()
    {
        // Registration only establishes the module lifecycle. The independent window
        // is user-launched through swuse.show and must not appear during module load.
    }

    public void DestroyUi()
    {
        if (_shellUi is null)
            SWuseWindowHost.Close();
    }
}
