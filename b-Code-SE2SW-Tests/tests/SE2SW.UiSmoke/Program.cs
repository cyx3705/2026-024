using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using SE2SW;

namespace SE2SW.UiSmoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application();
        var assemblyIndex = Array.FindIndex(args, item =>
            string.Equals(item, "--assembly", StringComparison.OrdinalIgnoreCase));
        var showAssembly = assemblyIndex >= 0;
        var assemblyPath = assemblyIndex >= 0 && assemblyIndex + 1 < args.Length
            && !args[assemblyIndex + 1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[assemblyIndex + 1])
            : null;
        var folderIndex = Array.FindIndex(args, item =>
            string.Equals(item, "--folder", StringComparison.OrdinalIgnoreCase));
        var folder = folderIndex >= 0 && folderIndex + 1 < args.Length
            ? Path.GetFullPath(args[folderIndex + 1])
            : null;
        var compact = args.Contains("--compact", StringComparer.OrdinalIgnoreCase);
        var busy = args.Contains("--busy", StringComparer.OrdinalIgnoreCase);
        var expandOptions = args.Contains("--expand-options", StringComparer.OrdinalIgnoreCase);
        var workspace = new SE2SWWorkspaceView();
        if (folder is not null)
            workspace.UnifiedPage.ViewModel.SetPartDirectory(folder);
        else if (assemblyPath is not null)
            workspace.UnifiedPage.ViewModel.SetAssemblySource(assemblyPath);
        if (busy)
            workspace.UnifiedPage.DataContext = new BusyPreviewState();
        var window = new Window
        {
            Title = folder is not null
                ? "Mapping UI Smoke · 零件文件夹"
                : showAssembly ? "Mapping UI Smoke · 装配体" : "Mapping UI Smoke · 选择来源",
            Width = compact ? 820 : 1280,
            Height = compact ? 620 : 820,
            Content = workspace,
        };
        var captureIndex = Array.FindIndex(args, item =>
            string.Equals(item, "--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            window.Show();
            workspace.UpdateLayout();
            if (Math.Abs(workspace.UnifiedPage.OutputPathBarHeight - 34) > 0.01)
                throw new InvalidOperationException("顶部路径条高度必须稳定为 34 px。");
            if (busy && workspace.UnifiedPage.OutputActivityVisibility != Visibility.Visible)
                throw new InvalidOperationException("运行期间顶部路径条必须显示不定进度条。");
            if (!busy && workspace.UnifiedPage.OutputActivityVisibility != Visibility.Collapsed)
                throw new InvalidOperationException("空闲期间顶部路径条运行条必须隐藏。");
            var sourceButton = FindVisualChildren<Button>(workspace).Single(button =>
                string.Equals(button.Name, "SourceSelectorButton", StringComparison.Ordinal));
            if (!sourceButton.IsEnabled)
                throw new InvalidOperationException("来源段运行期间也必须保持右键菜单可打开。");
            var cancelItem = sourceButton.ContextMenu?.Items.OfType<MenuItem>().SingleOrDefault(item =>
                string.Equals(item.Header as string, "取消当前操作", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("来源段缺少“取消当前操作”右键入口。");
            sourceButton.ContextMenu!.PlacementTarget = sourceButton;
            sourceButton.ContextMenu.IsOpen = true;
            workspace.UpdateLayout();
            if (cancelItem.IsEnabled != busy)
                throw new InvalidOperationException("来源段取消入口的启用状态与运行状态不一致。");
            sourceButton.ContextMenu.IsOpen = false;
            var contentSelector = FindVisualChildren<ComboBox>(workspace).Single(comboBox =>
                string.Equals(comboBox.Name, "MappingContentSelector", StringComparison.Ordinal));
            if (contentSelector.Items.Count != 2)
                throw new InvalidOperationException("顶栏必须只提供两种转换内容。");
            if (FindVisualChildren<Button>(workspace).Any(button =>
                    button.Name is "XtDirectoryButton" or "SolidWorksDirectoryButton"))
            {
                throw new InvalidOperationException("顶栏不得保留 XT 或 SW 输出目录栏。");
            }
            if (FindVisualChildren<TextBlock>(workspace).Any(textBlock =>
                    string.Equals(textBlock.Text, "Solid Edge 转 SolidWorks", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("V3.7 页面内部不应保留重复标题栏。");
            }
            if (FindVisualChildren<Button>(workspace).Any(button =>
                    string.Equals(button.Content as string, "取消", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("V3.7 页面底部不应保留独立取消按钮。");
            }
            if (FindVisualChildren<TabControl>(workspace).Any())
                throw new InvalidOperationException("V3.6 单页不应再包含模式页签。");
            if (FindVisualChildren<Button>(workspace).Any(button =>
                    string.Equals(button.Content as string, "解析装配体", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("装配页不应保留独立的“解析装配体”按钮。");
            }
            if (expandOptions)
            {
                foreach (var expander in FindVisualChildren<Expander>(workspace))
                    expander.IsExpanded = true;
                workspace.UpdateLayout();
            }
            var dpi = VisualTreeHelper.GetDpi(workspace);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(workspace.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(workspace.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(workspace);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(args[captureIndex + 1])))
                encoder.Save(stream);
            window.Close();
            workspace.Dispose();
            return;
        }
        application.Run(window);
    }

    private sealed class BusyPreviewState
    {
        public bool IsBusy => true;
        public bool CanEdit => false;
        public bool CanCancel => true;
        public bool CanConvert => false;
        public bool CanFullyDefineSketches => false;
        public bool CanContinueWhenPartFails => false;
        public bool CanRebuildMates => false;
        public bool CanRestoreXtDirectory => false;
        public bool CanRestoreSolidWorksDirectory => false;
        public bool IsPartDirectoryMode => false;
        public string SourceLabel => "装配体";
        public string SourcePath => @"C:\very-long-source-path\assembly\Top.asm";
        public string XtDirectory => @"C:\very-long-output-path\parasolid\XT";
        public string SolidWorksDirectory => @"C:\very-long-output-path\solidworks\SW";
        public string PartsPanelTitle => "唯一零件";
        public string PrimaryActionText => "转换装配体";
        public IReadOnlyList<object> AssemblyTree => [];
        public IReadOnlyList<object> Parts => [];
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
