using Microsoft.Win32;
using HistoryVulcan.Core.Commands;
using HistoryMinerva.Contracts;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
        // DataGridColumn 不参与可视树，Binding 解析不到 DataContext（实测列头会变空白），
        // 列头只能在这里跟着源格式更新。
        SourcePartColumn.Header = _viewModel.SourcePartColumnHeader;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssemblyViewModel.SourcePartColumnHeader) or null)
            SourcePartColumn.Header = _viewModel.SourcePartColumnHeader;
        if (e.PropertyName is nameof(AssemblyViewModel.SelectedMappingContent)
            or nameof(AssemblyViewModel.IsPartDirectoryMode)
            or null)
            CloseAssemblyTreeFlyout();
    }

    internal AssemblyViewModel ViewModel => _viewModel;
    internal double OutputPathBarHeight => OutputPathBar.ActualHeight;
    internal Visibility OutputActivityVisibility => OutputActivityBar.Visibility;
    internal double SourceColumnWidth => SourceColumn.ActualWidth;
    internal double ContentColumnWidth => ContentColumn.ActualWidth;

    private bool _mappingContentUserPicking;

    private void OnMappingContentDropDownOpened(object sender, EventArgs e)
        => _mappingContentUserPicking = true;

    private void OnMappingContentDropDownClosed(object sender, EventArgs e)
    {
        _mappingContentUserPicking = false;
        if (MappingContentSelector.SelectedItem is MappingContentOption option)
            _viewModel.SelectedMappingContent = option;
    }

    private void OnMappingContentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mappingContentUserPicking || MappingContentSelector.IsDropDownOpen)
            return;
        if (MappingContentSelector.SelectedItem is MappingContentOption option
            && EqualityComparer<MappingContentOption>.Default.Equals(option, _viewModel.SelectedMappingContent))
            return;
        MappingContentSelector.SelectedItem = _viewModel.SelectedMappingContent;
    }

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
            _viewModel.SetSourcePath(dialog.FileName);
            if (_commandBus is null || !_viewModel.CanProbe)
                return;
            // 等文件对话框把嵌套消息泵彻底收掉，再探查。同一拍里跑 COM 查询
            // 或开始灌零件表，整窗会停在对话框刚关上的那一帧。
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (_commandBus is not null && _viewModel.CanProbe)
                await _commandBus.ExecuteAsync(HistoryMinervaIdentity.CommandRoot + ".conversion.probe", HistoryMinervaIdentity.Name + ":UI");
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

        _viewModel.SetPartDirectory(dialog.FolderName);
    }

    private async void OnConvertClick(object sender, RoutedEventArgs e)
    {
        if (_commandBus is not null)
            await _commandBus.ExecuteAsync(HistoryMinervaIdentity.CommandRoot + ".conversion.run", HistoryMinervaIdentity.Name + ":UI");
    }

    private async void OnStripClick(object sender, RoutedEventArgs e)
    {
        if (_commandBus is not null)
            await _commandBus.ExecuteAsync(HistoryMinervaIdentity.CommandRoot + ".conversion.strip", HistoryMinervaIdentity.Name + ":UI");
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_commandBus is not null)
            await _commandBus.ExecuteAsync(HistoryMinervaIdentity.CommandRoot + ".conversion.cancel", HistoryMinervaIdentity.Name + ":UI");
    }

    private void OnAssemblyTreeOpenClick(object sender, RoutedEventArgs e)
    {
        if (AssemblyTreeFlyout.Visibility == Visibility.Visible)
            CloseAssemblyTreeFlyout();
        else
            OpenAssemblyTreeFlyout();
    }

    private void OpenAssemblyTreeFlyout()
    {
        AssemblyTreeFlyout.Visibility = Visibility.Visible;
        AssemblyTreeOpenButton.Content = "收起";
    }

    private void CloseAssemblyTreeFlyout()
    {
        AssemblyTreeFlyout.Visibility = Visibility.Collapsed;
        AssemblyTreeOpenButton.Content = "打开";
    }

    public void Dispose()
        => _viewModel.Dispose();
}
