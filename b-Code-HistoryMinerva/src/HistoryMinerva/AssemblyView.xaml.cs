using HistoryVulcan.Core.Commands;
using HistoryMinerva.Contracts;
using System.Collections.Generic;
using System.ComponentModel;
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
        // DataGridColumn 不参与可视树，Binding 解析不到 DataContext（实测列头会变空白），
        // 列头只能在这里跟着源格式更新。
        SourcePartColumn.Header = _viewModel.SourcePartColumnHeader;
        ApplyRenameTableColumns();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssemblyViewModel.SourcePartColumnHeader) or null)
            SourcePartColumn.Header = _viewModel.SourcePartColumnHeader;
        if (e.PropertyName is nameof(AssemblyViewModel.IsRenameMode)
            or nameof(AssemblyViewModel.SelectedMappingContent)
            or null)
            ApplyRenameTableColumns();
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

    private void ApplyRenameTableColumns()
    {
        var rename = _viewModel.IsRenameMode;
        FeatureColumn.Visibility = rename ? Visibility.Collapsed : Visibility.Visible;
        SketchColumn.Visibility = rename ? Visibility.Collapsed : Visibility.Visible;
        ResultColumn.Visibility = rename ? Visibility.Collapsed : Visibility.Visible;
    }

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

    private void OnChooseSourceClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEdit)
            return;
        var owner = Window.GetWindow(this);
        var picked = _viewModel.SelectedMappingContent.IsAssemblySource
            ? SourcePickDialog.ShowFile(owner)
            : SourcePickDialog.ShowFolder(owner);
        if (string.IsNullOrWhiteSpace(picked))
            return;
        _viewModel.SetSourcePath(picked);
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
