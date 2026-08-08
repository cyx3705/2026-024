using Microsoft.Win32;
using HistoryVulcan.Core.Commands;
using SE2SW.Contracts;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SE2SW;

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
        if (_viewModel.SelectedMappingContent.Kind == MappingContent.SolidEdgeAssemblyToSolidWorksAssembly)
            await ChooseAssemblyAsync();
        else
            ChoosePartDirectory();
    }

    private async Task ChooseAssemblyAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Solid Edge 装配体来源",
            Filter = "Solid Edge 装配体 (*.asm)|*.asm|所有文件 (*.*)|*.*",
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
        if (_commandBus is null)
            await _viewModel.ConvertAsync();
        else
            await _commandBus.ExecuteAsync(
                HistoryMinervaIdentity.CommandDomain + ".convert",
                HistoryMinervaIdentity.Name + ":UI");
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_commandBus is null)
            _viewModel.Cancel();
        else
            await _commandBus.ExecuteAsync(
                HistoryMinervaIdentity.CommandDomain + ".cancel",
                HistoryMinervaIdentity.Name + ":UI");
    }

    public void Dispose()
        => _viewModel.Dispose();
}
