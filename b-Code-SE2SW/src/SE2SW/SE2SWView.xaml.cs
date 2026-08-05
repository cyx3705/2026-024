using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SE2SW.Contracts;

namespace SE2SW;

public partial class SE2SWView : UserControl, IDisposable
{
    private readonly SE2SWViewModel _viewModel = new();

    public SE2SWView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ConversionMode Mode => _viewModel.Mode;

    public event EventHandler? ModeChanged;

    public void SetMode(ConversionMode mode) => _viewModel.SetMode(mode);

    private void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = _viewModel.IsOhsMode ? "选择 OHS 项目" : "选择转换文件夹",
            Multiselect = false,
        };
        if (Directory.Exists(_viewModel.SelectedDirectory))
            dialog.InitialDirectory = _viewModel.SelectedDirectory;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _viewModel.SetDirectory(dialog.FolderName);
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => _viewModel.Scan();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => _viewModel.SelectAll(true);

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => _viewModel.SelectAll(false);

    private async void OnConvertClick(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SE2SWViewModel.Mode))
            ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }
}
