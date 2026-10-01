using HistoryVulcan.Core.Commands;
using System.Windows.Controls;

namespace HistoryMinerva;

public partial class HistoryMinervaWorkspaceView : UserControl, IDisposable
{
    public HistoryMinervaWorkspaceView()
        : this(MappingRuntimePaths.Unattached(), null)
    {
    }

    internal HistoryMinervaWorkspaceView(MappingRuntimePaths runtimePaths, ICommandBus? commandBus)
    {
        InitializeComponent();
        UnifiedPage = new AssemblyView(runtimePaths, commandBus);
        PageHost.Children.Add(UnifiedPage);
    }

    internal AssemblyView UnifiedPage { get; }

    public void Dispose()
        => UnifiedPage.Dispose();
}
