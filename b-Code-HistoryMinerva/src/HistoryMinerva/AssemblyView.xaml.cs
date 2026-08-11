using Microsoft.Win32;
using HistoryVulcan.Core.Commands;
using HistoryMinerva.Contracts;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HistoryMinerva;

public partial class AssemblyView : UserControl, IDisposable
{
    private readonly AssemblyViewModel _viewModel;
    private readonly CommandBus? _commandBus;

    public AssemblyView()
        : this(MappingRuntimePaths.CreateAppShellFallback(), null)
    {
    }

    internal AssemblyView(MappingRuntimePaths runtimePaths, CommandBus? commandBus)
    {
        _viewModel = new AssemblyViewModel(runtimePaths);
        _commandBus = commandBus;
        InitializeComponent();
        DataContext = _viewModel;
    }

    internal AssemblyViewModel ViewModel => _viewModel;
    internal double OutputPathBarHeight => OutputPathBar.ActualHeight;
    internal Visibility OutputActivityVisibility => OutputActivityBar.Visibility;
    internal double SourceColumnWidth => SourceColumn.ActualWidth;
    internal double ContentColumnWidth => ContentColumn.ActualWidth;

    private async void OnChooseSourceClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEdit)
            return;
        if (_viewModel.SelectedMappingContent.IsAssemblySource)
            await ChooseAssemblyAsync();
        else
            ChoosePartDirectory();
    }

    private async Task ChooseAssemblyAsync()
    {
        // 过滤器跟着当前选中的转换内容走：选 SW 自整备时不该再让用户去挑 .asm。
        var isSolidWorksSource =
            _viewModel.SourceFormat == HistoryMinerva.Contracts.ConversionSourceFormat.SolidWorks;
        var dialog = new OpenFileDialog
        {
            Title = isSolidWorksSource ? "选择 SolidWorks 装配体来源" : "选择 Solid Edge 装配体来源",
            Filter = isSolidWorksSource
                ? "SolidWorks 装配体 (*.SLDASM)|*.SLDASM|所有文件 (*.*)|*.*"
                : "Solid Edge 装配体 (*.asm)|*.asm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        var directory = Path.GetDirectoryName(_viewModel.SourceAssemblyPath);
        if (Directory.Exists(directory))
            dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _viewModel.SetSourceFile(dialog.FileName);
            await _viewModel.ProbeAsync();
        }
    }

    private void ChoosePartDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Solid Edge 零件来源文件夹",
            Multiselect = false,
        };
        var currentDirectory = _viewModel.IsPartDirectoryMode
            ? _viewModel.SourcePath
            : Path.GetDirectoryName(_viewModel.SourceAssemblyPath);
        if (Directory.Exists(currentDirectory))
            dialog.InitialDirectory = currentDirectory;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            _viewModel.SetPartDirectory(dialog.FolderName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                exception.Message,
                "Mapping",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnConvertClick(object sender, RoutedEventArgs e)
    {
        // 页面按钮必须有反应：旧宿主前端 EnableCommands=false 时
        // HistoryMinerva.convert 未入本机总线表，ExecuteAsync 会被 RemoteExecutor
        // 转到服务进程（无页面实例）后静默失败。总线成功则沿用；失败且仍可转换时回退本页。
        if (_commandBus is not null)
        {
            var result = await _commandBus.ExecuteAsync(
                HistoryMinervaIdentity.CommandDomain + ".convert",
                HistoryMinervaIdentity.Name + ":UI");
            if (result.Success || !_viewModel.CanConvert)
                return;
        }

        await _viewModel.ConvertAsync();
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_commandBus is not null)
        {
            var result = await _commandBus.ExecuteAsync(
                HistoryMinervaIdentity.CommandDomain + ".cancel",
                HistoryMinervaIdentity.Name + ":UI");
            if (result.Success)
                return;
        }

        _viewModel.Cancel();
    }

    public void Dispose()
        => _viewModel.Dispose();
}
