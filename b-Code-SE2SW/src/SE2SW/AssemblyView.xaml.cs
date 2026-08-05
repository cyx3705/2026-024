using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SE2SW;

public partial class AssemblyView : UserControl, IDisposable
{
    private readonly AssemblyViewModel _viewModel = new();

    public AssemblyView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    internal AssemblyViewModel ViewModel => _viewModel;

    private void OnChooseSourceClick(object sender, RoutedEventArgs e)
    {
        SourcePickerOverlay.Visibility = Visibility.Visible;
        SourcePickerOverlay.Focus();
    }

    private async void OnChooseAssemblyClick(object sender, RoutedEventArgs e)
    {
        HideSourcePicker();
        var dialog = new OpenFileDialog
        {
            Title = "选择 Solid Edge 装配体",
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

    private void OnChoosePartDirectoryClick(object sender, RoutedEventArgs e)
    {
        HideSourcePicker();
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Solid Edge 零件文件夹",
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
                "SE2SW",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnConvertClick(object sender, RoutedEventArgs e)
        => await _viewModel.ConvertAsync();

    private void OnOpenXtDirectoryClick(object sender, RoutedEventArgs e)
        => OpenDirectory(_viewModel.XtDirectory, "XT 输出目录");

    private void OnOpenSolidWorksDirectoryClick(object sender, RoutedEventArgs e)
        => OpenDirectory(_viewModel.SolidWorksDirectory, "SW 输出目录");

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => _viewModel.Cancel();

    private void OnCloseSourcePickerClick(object sender, RoutedEventArgs e)
        => HideSourcePicker();

    private void OnSourcePickerBackdropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SourcePickerOverlay)
            HideSourcePicker();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SourcePickerOverlay.Visibility == Visibility.Visible)
        {
            HideSourcePicker();
            e.Handled = true;
        }
    }

    internal void ShowSourcePickerForSmoke()
    {
        SourcePickerOverlay.Visibility = Visibility.Visible;
    }

    private void HideSourcePicker()
        => SourcePickerOverlay.Visibility = Visibility.Collapsed;

    private void OpenDirectory(string directory, string label)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"{label}尚未生成。完成一次转换后可从这里直接打开。\n{directory}",
                "SE2SW",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"无法打开{label}：{exception.Message}",
                "SE2SW",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void Dispose()
        => _viewModel.Dispose();
}
