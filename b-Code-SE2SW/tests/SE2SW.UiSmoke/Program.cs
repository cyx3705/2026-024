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
        var expandOptions = args.Contains("--expand-options", StringComparer.OrdinalIgnoreCase);
        var openSourcePicker = args.Contains("--open-source-picker", StringComparer.OrdinalIgnoreCase);
        var workspace = new SE2SWWorkspaceView();
        if (folder is not null)
            workspace.UnifiedPage.ViewModel.SetPartDirectory(folder);
        else if (assemblyPath is not null)
            workspace.UnifiedPage.ViewModel.SetAssemblySource(assemblyPath);
        if (openSourcePicker)
            workspace.UnifiedPage.ShowSourcePickerForSmoke();
        var window = new Window
        {
            Title = folder is not null
                ? "SE2SW UI Smoke · 零件文件夹"
                : showAssembly ? "SE2SW UI Smoke · 装配体" : "SE2SW UI Smoke · 选择来源",
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
            if (FindVisualChildren<TabControl>(workspace).Any())
                throw new InvalidOperationException("V3.6 单页不应再包含模式页签。");
            if (openSourcePicker && !FindVisualChildren<TextBlock>(workspace).Any(textBlock =>
                    string.Equals(textBlock.Text, "选择转换来源", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("来源选择面板未显示。");
            }
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
