using AppShell.Core.Commands;
using System.Windows.Controls;

namespace SE2SW;

public partial class SE2SWWorkspaceView : UserControl, IDisposable
{
    public SE2SWWorkspaceView()
        : this(MappingRuntimePaths.CreateAppShellFallback(), null)
    {
    }

    internal SE2SWWorkspaceView(MappingRuntimePaths runtimePaths, CommandBus? commandBus)
    {
        InitializeComponent();
        UnifiedPage = new AssemblyView(runtimePaths, commandBus);
        PageHost.Children.Add(UnifiedPage);
    }

    internal AssemblyView UnifiedPage { get; }

    public void Dispose()
        => UnifiedPage.Dispose();
}
