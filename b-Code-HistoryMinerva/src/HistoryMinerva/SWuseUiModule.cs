using AppShell.Core.Modules;

namespace SWuse;

/// <summary>
/// SWuse 生命周期占位。4.2.0 起独立 SWuse 窗口已移除（待打磨后再回归）；
/// 建模能力保留在 HistoryMinerva.Worker 协议层。映射停靠页由 SE2SWUiModule 注册。
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
        _ = _shellUi;
    }

    public void DestroyUi()
    {
    }
}
