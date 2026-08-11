using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using HistoryMinerva;

namespace HistoryMinerva.UiSmoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application();
        var dark = args.Contains("--dark", StringComparer.OrdinalIgnoreCase);
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                dark
                    ? "/HistoryVulcan.Shell;component/Themes/ShellTokens.Dark.xaml"
                    : "/HistoryVulcan.Shell;component/Themes/ShellTokens.xaml",
                UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HistoryVulcan.Shell;component/Themes/ShellControls.xaml", UriKind.Relative),
        });
        var sourcePath = LocateRepoFile(Path.Combine("b-Code-HistoryMinerva", "src", "HistoryMinerva", "AssemblyView.xaml"));
        if (Regex.IsMatch(File.ReadAllText(sourcePath), "#[0-9A-Fa-f]{6,8}", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("生产 HistoryMinerva XAML 不得残留固定十六进制颜色。");
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
        var workspace = new HistoryMinervaWorkspaceView();
        workspace.SetResourceReference(Control.BackgroundProperty, "Shell.Brush.Canvas");
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
            Width = ReadDoubleArgument(args, "--width") ?? (compact ? 820 : 1280),
            Height = ReadDoubleArgument(args, "--height") ?? (compact ? 620 : 820),
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = workspace,
        };
        window.SetResourceReference(Window.BackgroundProperty, "Shell.Brush.Canvas");
        window.SetResourceReference(Control.ForegroundProperty, "Shell.Brush.TextPrimary");
        var captureIndex = Array.FindIndex(args, item =>
            string.Equals(item, "--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            window.Show();
            workspace.UpdateLayout();
            if (workspace.UnifiedPage.OutputPathBarHeight < 32)
                throw new InvalidOperationException("顶部 Mapping 工具栏高度不得低于宿主 32 px 合同。");
            if (Math.Abs(workspace.UnifiedPage.SourceColumnWidth - workspace.UnifiedPage.ContentColumnWidth) > 1)
                throw new InvalidOperationException("转换来源与转换内容必须保持等宽栏。");
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
            if (contentSelector.Items.Count != 3)
                throw new InvalidOperationException("顶栏必须只提供三种转换内容。");
            var sourceLabel = FindVisualChildren<TextBlock>(workspace).Single(textBlock =>
                string.Equals(textBlock.Name, "SourceLabelText", StringComparison.Ordinal));
            var sourcePathText = FindVisualChildren<TextBlock>(workspace).Single(textBlock =>
                string.Equals(textBlock.Name, "SourcePathText", StringComparison.Ordinal));
            var sourceLabelPosition = sourceLabel.TranslatePoint(new Point(0, 0), workspace);
            var sourcePathPosition = sourcePathText.TranslatePoint(new Point(0, 0), workspace);
            if (Math.Abs(sourceLabelPosition.Y + sourceLabel.ActualHeight / 2 -
                        sourcePathPosition.Y - sourcePathText.ActualHeight / 2) > 1 ||
                sourcePathText.TranslatePoint(new Point(0, 0), workspace).X <=
                    sourceLabel.TranslatePoint(new Point(0, 0), workspace).X)
            {
                throw new InvalidOperationException("转换来源的标签与选择栏必须同处一行。");
            }
            var contentLabel = FindVisualChildren<TextBlock>(workspace).Single(textBlock =>
                string.Equals(textBlock.Name, "ContentLabelText", StringComparison.Ordinal));
            var contentLabelPosition = contentLabel.TranslatePoint(new Point(0, 0), workspace);
            var contentSelectorPosition = contentSelector.TranslatePoint(new Point(0, 0), workspace);
            if (Math.Abs(contentLabelPosition.Y + contentLabel.ActualHeight / 2 -
                        contentSelectorPosition.Y - contentSelector.ActualHeight / 2) > 1 ||
                contentSelector.TranslatePoint(new Point(0, 0), workspace).X <=
                    contentLabel.TranslatePoint(new Point(0, 0), workspace).X)
            {
                throw new InvalidOperationException("转换内容的标签与选择栏必须同处一行。");
            }
            if (FindVisualChildren<Button>(workspace).Any(button =>
                    button.Name is "XtDirectoryButton" or "SolidWorksDirectoryButton"))
            {
                throw new InvalidOperationException("顶栏不得保留 XT 或 SW 输出目录栏。");
            }
            if (FindVisualChildren<TextBlock>(workspace).Any(textBlock =>
                    textBlock.Text is "零件" or "唯一零件"))
            {
                throw new InvalidOperationException("零件列表上方不得保留独立标题行。");
            }
            var optionsActionBar = FindVisualChildren<Grid>(workspace).Single(grid =>
                string.Equals(grid.Name, "OptionsActionBar", StringComparison.Ordinal));
            var optionsList = FindVisualChildren<WrapPanel>(optionsActionBar).Single(panel =>
                string.Equals(panel.Name, "OptionsList", StringComparison.Ordinal));
            var optionBoxes = FindVisualChildren<CheckBox>(optionsList).ToArray();
            if (optionBoxes.Length != 4)
                throw new InvalidOperationException("转换选项必须保留四项配置。");
            var optionsLabel = FindVisualChildren<TextBlock>(optionsList).Single(textBlock =>
                string.Equals(textBlock.Name, "OptionsLabelText", StringComparison.Ordinal));
            var statusText = FindVisualChildren<TextBlock>(optionsActionBar).Single(textBlock =>
                string.Equals(textBlock.Name, "StatusText", StringComparison.Ordinal));
            var convertButton = FindVisualChildren<Button>(optionsActionBar).Single(button =>
                string.Equals(button.Name, "ConvertButton", StringComparison.Ordinal));
            if (workspace.ActualWidth >= 900)
            {
                var actionItems = new FrameworkElement[]
                {
                    optionsLabel, optionBoxes[0], optionBoxes[1], optionBoxes[2], optionBoxes[3],
                    statusText, convertButton,
                };
                var firstActionPosition = actionItems[0].TranslatePoint(new Point(0, 0), workspace);
                var firstActionCenter = firstActionPosition.Y + actionItems[0].ActualHeight / 2;
                if (actionItems.Any(item =>
                {
                    var position = item.TranslatePoint(new Point(0, 0), workspace);
                    return Math.Abs(position.Y + item.ActualHeight / 2 - firstActionCenter) > 1;
                }))
                    throw new InvalidOperationException("宽窗口下转换选项、状态与按钮必须横向排列。");
            }
            else
            {
                foreach (var item in new FrameworkElement[]
                {
                    optionsLabel, optionBoxes[0], optionBoxes[1], optionBoxes[2], optionBoxes[3],
                    statusText, convertButton,
                })
                {
                    var position = item.TranslatePoint(new Point(0, 0), workspace);
                    if (position.X < -1 || position.X + item.ActualWidth > workspace.ActualWidth + 1)
                        throw new InvalidOperationException("窄窗口下转换选项不得水平溢出。");
                }
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
            var requestedDpiPercent = ReadDoubleArgument(args, "--dpi");
            if (requestedDpiPercent is not null && requestedDpiPercent is not (100 or 125 or 150))
                throw new InvalidOperationException("--dpi 只接受 100、125 或 150。");
            var renderScale = requestedDpiPercent is null
                ? dpi.DpiScaleX
                : requestedDpiPercent.Value / 100d;
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(workspace.ActualWidth * renderScale)),
                Math.Max(1, (int)Math.Ceiling(workspace.ActualHeight * renderScale)),
                96d * renderScale,
                96d * renderScale,
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

    private static double? ReadDoubleArgument(IReadOnlyList<string> args, string name)
    {
        var index = Array.FindIndex(args.ToArray(), item =>
            string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count
            && double.TryParse(args[index + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string LocateRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"找不到 Smoke 源文件：{relativePath}");
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
        public bool IsPartDirectoryMode => false;
        public IReadOnlyList<MappingContentOption> MappingContents => MappingContentOption.Available;
        public MappingContentOption SelectedMappingContent { get; set; } = MappingContentOption.Available[1];
        public string SourceLabel => "装配体";
        public string SourcePath => @"C:\very-long-source-path\assembly\Top.asm";
        public string StatusText => "正在转换装配体";
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
