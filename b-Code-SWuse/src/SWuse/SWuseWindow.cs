using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SWuse.Contracts;

namespace SWuse;

/// <summary>SWuse 自持的轻量 C# 建模工作区；V0.1 不试图替代完整 IDE。</summary>
internal sealed class SWuseWindow : Window
{
    private readonly TextBox _workspace = new();
    private readonly TextBox _output = new();
    private readonly ListBox _files = new();
    private readonly TextBox _editor = new();
    private readonly TextBox _log = new();
    private string? _currentFile;
    private bool _allowClose;

    public SWuseWindow()
    {
        Title = "SWuse · SolidWorks C#";
        Width = 1420;
        Height = 900;
        MinWidth = 980;
        MinHeight = 640;
        // This is a normal, independent desktop tool window. It deliberately has no
        // AppShell docking descriptor, but it must remain discoverable and editable
        // like any other top-level application window.
        ShowInTaskbar = true;
        ShowActivated = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xF8));
        Content = CreateLayout();
        Closing += (_, args) =>
        {
            if (_allowClose)
                return;
            args.Cancel = true;
            Hide();
        };
        _workspace.Text = SWuseWorkspace.DefaultPath;
        RefreshFiles(selectPath: null);
    }

    public bool IsBuilding { get; private set; }

    public void CloseForUnload()
    {
        _allowClose = true;
        Close();
    }

    private UIElement CreateLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "SWuse",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x36, 0x3C)),
            Margin = new Thickness(0, 0, 16, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = "静态 C# → SolidWorks 零件构建实验环境 · 长度单位：米",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.DimGray,
        });
        root.Children.Add(header);

        var controls = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabel(controls, "工作区", 0, 0);
        ConfigurePathBox(_workspace);
        Add(controls, _workspace, 1, 0);
        AddLabel(controls, "输出", 2, 0);
        ConfigurePathBox(_output);
        Add(controls, _output, 3, 0);
        var initialize = Button("初始化示例", (_, _) => InitializeWorkspace());
        initialize.Margin = new Thickness(10, 0, 0, 0);
        Add(controls, initialize, 4, 0);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Button("刷新文件", (_, _) => RefreshFiles(_currentFile)));
        actions.Children.Add(Button("新建帮助类", (_, _) => CreateHelper()));
        actions.Children.Add(Button("保存", (_, _) => SaveEditor()));
        actions.Children.Add(Button("构建验证", async (_, _) => await RunAsync(dryRun: true)));
        actions.Children.Add(Button("生成模型", async (_, _) => await RunAsync(dryRun: false), accent: true));
        Grid.SetColumnSpan(actions, 5);
        Add(controls, actions, 0, 1);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var fileBorder = PanelBorder("工作区源码", _files);
        _files.SelectionChanged += (_, _) => LoadSelectedFile();
        Add(body, fileBorder, 0, 0);
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.FontFamily = new FontFamily("Consolas");
        _editor.FontSize = 14;
        _editor.TextWrapping = TextWrapping.NoWrap;
        _editor.Padding = new Thickness(10);
        Add(body, PanelBorder("C# 代码", _editor), 1, 0, new Thickness(12, 0, 12, 0));
        _log.AcceptsReturn = true;
        _log.IsReadOnly = true;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.TextWrapping = TextWrapping.Wrap;
        _log.FontFamily = new FontFamily("Consolas");
        _log.Padding = new Thickness(10);
        Add(body, PanelBorder("构建日志", _log), 2, 0);
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        return root;
    }

    private async Task RunAsync(bool dryRun)
    {
        try
        {
            SaveEditor();
            var workspace = RequireWorkspace();
            var sources = SWuseWorkspace.SourceFiles(workspace);
            if (sources.Count == 0)
                throw new InvalidOperationException("工作区没有 .cs 文件，请先初始化示例或新建类。");
            var output = string.IsNullOrWhiteSpace(_output.Text)
                ? Path.Combine(workspace, "out", "Demo.SLDPRT")
                : Path.GetFullPath(_output.Text.Trim());
            IsBuilding = true;
            Log(dryRun ? "正在编译验证…" : "正在编译并调用 SolidWorks COM…");
            var result = await SWuseWorkerClient.RunAsync(
                new SWuseBuildRequest(workspace, output, sources, DryRun: dryRun),
                CancellationToken.None);
            Log(result.Summary);
            foreach (var diagnostic in result.Diagnostics)
            {
                var location = diagnostic.FilePath is null ? string.Empty
                    : $" {Path.GetFileName(diagnostic.FilePath)}({diagnostic.Line},{diagnostic.Column})";
                Log($"[{diagnostic.Severity}]{location} {diagnostic.Message}");
            }
        }
        catch (Exception ex)
        {
            Log("[Error] " + ex.Message);
        }
        finally
        {
            IsBuilding = false;
        }
    }

    private void InitializeWorkspace()
    {
        var workspace = RequireWorkspace();
        SWuseWorkspace.Initialize(workspace);
        _output.Text = Path.Combine(workspace, "out", "Demo.SLDPRT");
        RefreshFiles(Path.Combine(workspace, "DemoPart.cs"));
        Log("已初始化示例工作区：" + workspace);
    }

    private void CreateHelper()
    {
        SaveEditor();
        var path = SWuseWorkspace.CreateHelperClass(RequireWorkspace());
        RefreshFiles(path);
        Log("已创建帮助类：" + path);
    }

    private void RefreshFiles(string? selectPath)
    {
        SaveEditor();
        var workspace = _workspace.Text.Trim();
        _files.Items.Clear();
        foreach (var path in SWuseWorkspace.SourceFiles(workspace))
        {
            var item = new ListBoxItem { Content = Path.GetRelativePath(workspace, path), Tag = path };
            _files.Items.Add(item);
            if (selectPath is not null && string.Equals(path, selectPath, StringComparison.OrdinalIgnoreCase))
                _files.SelectedItem = item;
        }
        if (_files.SelectedItem is null && _files.Items.Count > 0)
            _files.SelectedIndex = 0;
    }

    private void LoadSelectedFile()
    {
        SaveEditor();
        if (_files.SelectedItem is not ListBoxItem { Tag: string path })
            return;
        _currentFile = path;
        _editor.Text = File.ReadAllText(path);
    }

    private void SaveEditor()
    {
        if (_currentFile is not null)
            File.WriteAllText(_currentFile, _editor.Text);
    }

    private string RequireWorkspace()
    {
        var workspace = Path.GetFullPath(_workspace.Text.Trim());
        _workspace.Text = workspace;
        return workspace;
    }

    private void Log(string text)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _log.ScrollToEnd();
    }

    private static Button Button(string text, RoutedEventHandler handler, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6),
        };
        if (accent)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x7A, 0x6E));
            button.Foreground = Brushes.White;
        }
        button.Click += handler;
        return button;
    }

    private static Border PanelBorder(string title, UIElement content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 8, 10, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x45, 0x4B)),
        });
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0xDC, 0xDE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.White,
            Child = grid,
        };
    }

    private static void ConfigurePathBox(TextBox box)
    {
        box.Padding = new Thickness(6);
        box.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static void AddLabel(Grid grid, string text, int column, int row)
        => Add(grid, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, column, row);

    private static void Add(Grid grid, UIElement element, int column, int row, Thickness? margin = null)
    {
        if (margin is Thickness value && element is FrameworkElement frameworkElement)
            frameworkElement.Margin = value;
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }
}
