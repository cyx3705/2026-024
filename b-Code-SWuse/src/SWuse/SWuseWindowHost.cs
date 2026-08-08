using System.Windows;
using System.Windows.Threading;

namespace SWuse;

internal static class SWuseWindowHost
{
    private static SWuseWindow? _window;
    private static Dispatcher? _dispatcher;

    internal static bool IsCreated => _window is not null;

    public static void CreateOrShow()
    {
        _dispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Invoke(() =>
        {
            _window ??= new SWuseWindow();
            if (!_window.IsVisible)
                _window.Show();
        });
    }

    public static string Show()
    {
        CreateOrShow();
        return "SWuse 独立窗口已显示。";
    }

    public static string Hide()
    {
        Invoke(() => _window?.Hide());
        return "SWuse 独立窗口已隐藏。";
    }

    public static string Status()
        => _window is null
            ? "SWuse 窗口尚未创建。"
            : _window.IsBuilding
                ? "SWuse 正在构建 SolidWorks 零件。"
                : _window.IsVisible ? "SWuse 窗口已显示。" : "SWuse 窗口已隐藏。";

    public static void Close()
    {
        Invoke(() =>
        {
            _window?.CloseForUnload();
            _window = null;
        });
    }

    private static void Invoke(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
