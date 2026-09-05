using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HistoryMinerva;

namespace HistoryMinerva.UiSmoke;

/// <summary>
/// Minerva 页面的布局门禁。**不需要人看着，也永远不会停下来等人。**
///
/// 这一版重写之前，本工具有三个结构性毛病，三个都咬过人：
///
/// <list type="number">
///   <item>全部断言写在 <c>if (--capture 存在)</c> 里。忘了传 <c>--capture</c> 就一条都不跑，
///         而是掉到 <c>Application.Run(window)</c>——开一个 <c>WindowStyle.None</c>
///         （没有标题栏、没有关闭按钮）的窗口，阻塞到有人手动杀掉为止。
///         一个验证工具不允许有任何一条会等人的路径。</item>
///   <item>320 / 760 / 1200 三档宽度靠人手工跑三遍。验证合同写着「覆盖三档」，
///         却没有任何东西强制它——少跑一档没人看得出来。</item>
///   <item><c>OutputType</c> 是 <c>WinExe</c>，成功时一个字都不打印。
///         「跑过了」与「根本没跑」在终端上长得一模一样。</item>
/// </list>
///
/// 现在：不带参数跑完整矩阵，窗口开在屏幕外、逐个即开即关，收尾打印一行 PASS。
/// <c>--capture &lt;目录&gt;</c> 只决定要不要顺手留 PNG，不决定跑不跑检查。
/// 另有一道看门狗：整轮超时就带原因退出，绝不挂住终端。
/// </summary>
internal static class Program
{
    /// <summary>整轮预算。WPF 真卡住时靠它兜底，不让终端挂在那儿。</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 布局矩阵。**这是验证合同 VERIFY-UI 的可执行副本**，不再依赖人记得跑几遍。
    /// 三档宽度覆盖窄/中/宽，深浅主题各至少一次，另加一个运行态。
    /// </summary>
    private static readonly UiCase[] Matrix =
    [
        new("320-dark", 320, 680, Dark: true, Busy: false, RenderScale: 1.5),
        new("760-dark", 760, 680, Dark: true, Busy: false, RenderScale: 1.0),
        new("1200-light", 1200, 820, Dark: false, Busy: false, RenderScale: 1.0),
        new("1200-busy", 1200, 820, Dark: false, Busy: true, RenderScale: 1.0),
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        StartWatchdog();
        try
        {
            var captureDirectory = ReadCaptureDirectory(args);
            CheckNoHardCodedColors();

            // OnLastWindowClose（默认）会在关掉第一个用例的窗口时触发应用关停，
            // 之后每个窗口都再也不排版，量到的宽高全是 0（重写时实测）。
            // 这个工具自己决定什么时候结束，不由窗口数决定。
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var testCase in Matrix)
                RunCase(application, testCase, captureDirectory);
            application.Shutdown();

            Console.WriteLine($"HistoryMinerva.UiSmoke: PASS（{Matrix.Length} 个布局用例）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HistoryMinerva.UiSmoke: FAIL — " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 看门狗。<c>Environment.Exit</c> 而不是抛异常：要防的正是「主线程卡在 WPF 里出不来」，
    /// 那种情形下抛什么都没人接得住。后台线程，不阻止正常退出。
    /// </summary>
    private static void StartWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(Watchdog);
            Console.Error.WriteLine(
                $"HistoryMinerva.UiSmoke: FAIL — 整轮超过 {Watchdog.TotalMinutes:0} 分钟仍未结束，已强制退出。");
            Environment.Exit(2);
        })
        {
            IsBackground = true,
            Name = "UiSmoke watchdog",
        };
        watchdog.Start();
    }

    private static void RunCase(Application application, UiCase testCase, string? captureDirectory)
    {
        ApplyTheme(application, testCase.Dark);
        var workspace = new HistoryMinervaWorkspaceView();
        workspace.SetResourceReference(Control.BackgroundProperty, "Aurora.Brush.Canvas");
        if (testCase.Busy)
            workspace.UnifiedPage.DataContext = new BusyPreviewState();

        // 开在屏幕外：布局照常计算，但不会在用户桌面上闪一串窗口。
        var window = new Window
        {
            Title = "HistoryMinerva UI Smoke · " + testCase.Name,
            Width = testCase.Width,
            Height = testCase.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Content = workspace,
        };
        window.SetResourceReference(Window.BackgroundProperty, "Aurora.Brush.Canvas");
        window.SetResourceReference(Control.ForegroundProperty, "Aurora.Brush.TextPrimary");

        try
        {
            window.Show();
            Settle(workspace);
            var page = workspace.UnifiedPage;
            // 排版没发生过时，后面每一条断言量到的都是 0，报出来的会是一句
            // 「顶栏高度不足 32」这样的假指控。先把这种情形说清楚。
            Check(testCase, "排版", () => Require(
                workspace.ActualWidth > 0 && workspace.ActualHeight > 0,
                "窗口未完成排版（宽高为 0），本用例的所有度量都不可信。"));

            Check(testCase, "顶栏", () => CheckTopBar(page, workspace));
            Check(testCase, "运行指示", () => CheckActivityBar(page, testCase));
            Check(testCase, "取消入口", () => CheckCancelEntry(workspace, testCase));
            Check(testCase, "零件表", () => CheckPartsTable(workspace));
            Check(testCase, "装配结构", () => CheckAssemblyTree(workspace, testCase));
            Check(testCase, "操作栏", () => CheckOptionsBar(workspace));
            if (!testCase.Busy)
                Check(testCase, "整体打包", () => CheckPackMode(page, workspace));

            if (captureDirectory is not null)
                Capture(workspace, testCase, captureDirectory);
        }
        finally
        {
            // 无论断言成不成，窗口都必须关掉。这是「永远不留窗口给人手动关」的那道保证。
            window.Close();
            workspace.Dispose();
        }
    }

    /// <summary>
    /// 等这一版排版真的落地。
    ///
    /// <c>Show()</c> 加一次 <c>UpdateLayout()</c> 只够第一个窗口用。同一个进程里开第二个时，
    /// 测量与排列还排在 Dispatcher 队列里没跑，此刻读到的 <c>ActualWidth</c> 是 0——
    /// 于是每一条尺寸断言都会以「顶栏高度不足 32」这种假原因失败（重写时实测）。
    /// 把队列抽到 ContextIdle 再量。
    /// </summary>
    private static void Settle(FrameworkElement element)
    {
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        element.UpdateLayout();
    }

    /// <summary>把失败包上用例名。「哪一条断言炸了」和「在哪一档宽度上炸的」缺一不可。</summary>
    private static void Check(UiCase testCase, string name, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"[{testCase.Name}] {name}：{ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- 逐项检查

    private static void CheckTopBar(AssemblyView page, HistoryMinervaWorkspaceView workspace)
    {
        Require(page.OutputPathBarHeight >= 32, "顶部工具栏高度不得低于宿主 32 px 合同。");
        Require(
            Math.Abs(page.SourceColumnWidth - page.ContentColumnWidth) <= 1,
            "转换来源与转换内容必须保持等宽栏。");

        var contentSelector = Named<ComboBox>(workspace, "MappingContentSelector");
        Require(
            contentSelector.Items.Count == MappingContentOption.Available.Count,
            $"顶栏必须提供 {MappingContentOption.Available.Count} 种转换内容，实得 {contentSelector.Items.Count}。");

        SameRow(workspace, Named<TextBlock>(workspace, "SourceLabelText"),
            Named<TextBlock>(workspace, "SourcePathText"), "转换来源");
        SameRow(workspace, Named<TextBlock>(workspace, "ContentLabelText"),
            contentSelector, "转换内容");
    }

    private static void CheckActivityBar(AssemblyView page, UiCase testCase)
    {
        var expected = testCase.Busy ? Visibility.Visible : Visibility.Collapsed;
        Require(
            page.OutputActivityVisibility == expected,
            testCase.Busy
                ? "运行期间顶部路径条必须显示不定进度条。"
                : "空闲期间顶部路径条运行条必须隐藏。");
    }

    private static void CheckCancelEntry(HistoryMinervaWorkspaceView workspace, UiCase testCase)
    {
        var sourceButton = Named<Button>(workspace, "SourceSelectorButton");
        Require(sourceButton.IsEnabled, "来源段运行期间也必须保持右键菜单可打开。");
        var cancelItem = sourceButton.ContextMenu?.Items.OfType<MenuItem>().SingleOrDefault(item =>
                string.Equals(item.Header as string, "取消当前操作", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("来源段缺少「取消当前操作」右键入口。");

        sourceButton.ContextMenu!.PlacementTarget = sourceButton;
        sourceButton.ContextMenu.IsOpen = true;
        try
        {
            Settle(workspace);
            Require(
                cancelItem.IsEnabled == testCase.Busy,
                "来源段取消入口的启用状态与运行状态不一致。");
        }
        finally
        {
            // 弹出层必须收回去，否则它会挂在窗口关闭之后。
            sourceButton.ContextMenu.IsOpen = false;
        }
    }

    private static void CheckPartsTable(HistoryMinervaWorkspaceView workspace)
    {
        var partsGrid = Named<DataGrid>(workspace, "PartsDataGrid");
        Require(
            partsGrid.ActualWidth >= workspace.ActualWidth * 0.75,
            "零件表必须铺满正文宽度。");
        Require(
            !Descendants<TextBlock>(workspace).Any(text => text.Text is "零件" or "唯一零件"),
            "零件列表上方不得保留独立标题行。");
    }

    private static void CheckAssemblyTree(HistoryMinervaWorkspaceView workspace, UiCase testCase)
    {
        Require(!Descendants<Expander>(workspace).Any(), "装配结构不得再占用正文展开条（DEC-040）。");
        Require(
            Named<Border>(workspace, "AssemblyTreeFlyout").Visibility == Visibility.Collapsed,
            "装配结构必须默认收起（DEC-041）。");

        if (testCase.Busy)
            return;
        var page = workspace.UnifiedPage;
        var original = page.ViewModel.SelectedMappingContent;
        page.ViewModel.SelectedMappingContent =
            page.ViewModel.MappingContents.First(option => option.IsAssemblySource);
        Settle(workspace);
        try
        {
            var openButton = Named<Button>(workspace, "AssemblyTreeOpenButton");
            Require(openButton.IsVisible, "装配转换下必须提供打开装配结构的按钮。");
            Require(
                openButton.TranslatePoint(new Point(0, 0), workspace).X <= workspace.ActualWidth / 2,
                "打开装配结构的按钮必须放在选项区左侧（DEC-041）。");
        }
        finally
        {
            page.ViewModel.SelectedMappingContent = original;
            Settle(workspace);
        }
    }

    private static void CheckOptionsBar(HistoryMinervaWorkspaceView workspace)
    {
        var actionBar = Named<Grid>(workspace, "OptionsActionBar");
        var optionsList = Named<WrapPanel>(actionBar, "OptionsList");
        var optionBoxes = Descendants<CheckBox>(optionsList).ToArray();
        // 三项：识别特征与草图（识别与草图合并）、失败继续、重建装配关系。
        // 合并的原因是横向空间——四项会把「重建装配关系」挤到第二行，用户拿不到那个开关。
        Require(optionBoxes.Length == 3, $"转换选项必须保留三项配置，实得 {optionBoxes.Length}。");
        Require(
            !Descendants<TextBlock>(actionBar).Any(text =>
                string.Equals(text.Name, "StatusText", StringComparison.Ordinal)),
            "操作结果必须显示在 Vulcan 控制台，页面不得保留状态栏（REQ-006）。");

        var row = new FrameworkElement[]
        {
            Named<TextBlock>(optionsList, "OptionsLabelText"),
            optionBoxes[0], optionBoxes[1], optionBoxes[2],
            Named<Button>(actionBar, "ConvertButton"),
        };

        if (workspace.ActualWidth >= 900)
        {
            var baseline = Center(row[0], workspace);
            Require(
                row.All(item => Math.Abs(Center(item, workspace) - baseline) <= 1),
                "宽窗口下转换选项与按钮必须横向排列。");
            return;
        }

        foreach (var item in row)
        {
            var left = item.TranslatePoint(new Point(0, 0), workspace).X;
            Require(
                left >= -1 && left + item.ActualWidth <= workspace.ActualWidth + 1,
                "窄窗口下转换选项不得水平溢出。");
        }
    }

    /// <summary>
    /// V4.10 整体打包不改任何模型，三个转换开关与「特征」「草图」两列都必须消失，
    /// 主按钮改叫「打包」。这三条是页面上唯一能看出「这一档不会动你的文件」的地方。
    /// </summary>
    private static void CheckPackMode(AssemblyView page, HistoryMinervaWorkspaceView workspace)
    {
        var original = page.ViewModel.SelectedMappingContent;
        page.ViewModel.SelectedMappingContent = MappingContentOption.Available
            .Single(option => option.Kind == MappingContent.SolidWorksAssemblyPackage);
        Settle(workspace);
        try
        {
            Require(page.ViewModel.IsPackMode, "选中整体打包后页面必须进入打包模式。");
            Require(!page.ViewModel.ShowConversionOptions, "打包不改模型，三个转换开关必须隐藏。");
            Require(page.ViewModel.PrimaryActionText == "打包", "打包模式的主按钮必须叫「打包」。");

            var columns = Named<DataGrid>(workspace, "PartsDataGrid").Columns;
            foreach (var header in new[] { "特征", "草图" })
            {
                var column = columns.SingleOrDefault(item =>
                    string.Equals(item.Header as string, header, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"零件表缺少「{header}」列。");
                Require(
                    column.Visibility == Visibility.Collapsed,
                    $"打包模式下「{header}」列必须隐藏——它永远不会有值。");
            }
        }
        finally
        {
            page.ViewModel.SelectedMappingContent = original;
            Settle(workspace);
        }
    }

    /// <summary>生产 XAML 不得写死颜色——写死的那一个在另一个主题下必然是错的。</summary>
    private static void CheckNoHardCodedColors()
    {
        var xaml = LocateRepoFile(Path.Combine("b-Code-HistoryMinerva", "src", "HistoryMinerva", "AssemblyView.xaml"));
        Require(
            !Regex.IsMatch(File.ReadAllText(xaml), "#[0-9A-Fa-f]{6,8}", RegexOptions.CultureInvariant),
            "生产 HistoryMinerva XAML 不得残留固定十六进制颜色。");
    }

    // ---------------------------------------------------------------- 工具

    private static void ApplyTheme(Application application, bool dark)
    {
        application.Resources.MergedDictionaries.Clear();
        foreach (var relative in new[]
                 {
                     dark
                         ? "/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml"
                         : "/HistoryAurora;component/Themes/AuroraTokens.xaml",
                     "/HistoryAurora;component/Themes/AuroraControls.xaml",
                 })
        {
            application.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(relative, UriKind.Relative) });
        }
    }

    private static void Capture(HistoryMinervaWorkspaceView workspace, UiCase testCase, string directory)
    {
        Directory.CreateDirectory(directory);
        var scale = testCase.RenderScale;
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(workspace.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(workspace.ActualHeight * scale)),
            96d * scale,
            96d * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(workspace);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(directory, $"historyminerva-ui-{testCase.Name}.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string? ReadCaptureDirectory(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--capture", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[index + 1]);
        }

        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static double Center(FrameworkElement element, UIElement root)
        => element.TranslatePoint(new Point(0, 0), root).Y + (element.ActualHeight / 2);

    /// <summary>标签与控件必须同处一行，且标签在左。两条一起判，因为它们说的是同一件事。</summary>
    private static void SameRow(UIElement root, FrameworkElement label, FrameworkElement control, string what)
    {
        Require(
            Math.Abs(Center(label, root) - Center(control, root)) <= 1,
            $"{what}的标签与选择栏必须同处一行。");
        Require(
            control.TranslatePoint(new Point(0, 0), root).X
                > label.TranslatePoint(new Point(0, 0), root).X,
            $"{what}的选择栏必须排在标签右侧。");
    }

    private static T Named<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => Descendants<T>(root).SingleOrDefault(element =>
                string.Equals(element.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"页面缺少 {typeof(T).Name}「{name}」。");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static string LocateRepoFile(string relativePath)
    {
        var repositoryRoot = Assembly.GetEntryAssembly()!
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "HistoryMinervaRepositoryRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var declared = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            if (File.Exists(declared))
                return declared;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"找不到 Smoke 源文件：{relativePath}");
    }

    /// <param name="RenderScale">PNG 渲染倍率，兼作 DPI 覆盖：1.0 = 100%，1.5 = 150%。</param>
    private sealed record UiCase(
        string Name,
        double Width,
        double Height,
        bool Dark,
        bool Busy,
        double RenderScale);

    /// <summary>
    /// 运行态的假 DataContext。真 ViewModel 的 <c>IsBusy</c> 是私有 set，测试进不去，
    /// 而「运行时进度条要亮、取消要可点」必须有人守。
    ///
    /// **这里的成员必须与 <c>AssemblyView.xaml</c> 的绑定逐个对上。** 少一个只会产生一条
    /// 静默的绑定错误，页面照常渲染，于是这条用例看起来通过了却什么都没验。
    /// 本次清掉了三个早已不存在的成员（CanStrip、CanFullyDefineSketches、PartsPanelTitle）
    /// 并补齐了五个漏掉的绑定。
    /// </summary>
    private sealed class BusyPreviewState
    {
        public bool IsBusy => true;
        public bool CanEdit => false;
        public bool CanCancel => true;
        public bool CanConvert => false;
        public bool ShowConversionOptions => true;
        public bool IsRenameMode => false;
        public bool IsPartDirectoryMode => false;
        // 这三个是 CheckBox 的 IsChecked，默认 TwoWay：只读属性会让 WPF 当场抛
        // 「无法对只读属性进行 TwoWay 绑定」。旧版的假 VM 干脆没有这三个成员，
        // 于是只留下三条静默的绑定错误，这一档用例看起来通过了却什么都没验。
        public bool RecognizeFeatures { get; set; }
        public bool ContinueWhenPartFails { get; set; } = true;
        public bool CanContinueWhenPartFails => false;
        public bool RebuildMates { get; set; } = true;
        public bool CanRebuildMates => false;
        public string RebuildMatesHint => "正在转换装配体。";
        public string DrawingPrefix { get; set; } = string.Empty;
        public IReadOnlyList<MappingContentOption> MappingContents => MappingContentOption.Available;
        public MappingContentOption SelectedMappingContent { get; set; } = MappingContentOption.Available[1];
        public string SourceLabel => "转换来源";
        public string SourcePath => @"C:\very-long-source-path\assembly\Top.asm";
        public string PrimaryActionText => "转换装配体";
        public IReadOnlyList<object> AssemblyTree => [];
        public IReadOnlyList<object> Parts => [];
    }
}
