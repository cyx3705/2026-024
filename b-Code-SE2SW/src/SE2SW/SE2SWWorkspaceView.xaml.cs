using System.Windows.Controls;

namespace SE2SW;

public partial class SE2SWWorkspaceView : UserControl, IDisposable
{
    public SE2SWWorkspaceView()
        => InitializeComponent();

    internal AssemblyView UnifiedPage => AssemblyPage;

    public void Dispose()
        => AssemblyPage.Dispose();
}
