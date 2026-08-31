using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HistoryMinerva;

/// <summary>
/// 来源文件对话框。不得沿用宿主上次打开的 CAD 零件库：
/// Windows 会记住那个目录，ShowDialog 里 SolidWorks/Solid Edge 预览处理器
/// 会把整库枚举一遍，主程序当场假死。这与解析装配体、表格刷新无关。
/// </summary>
internal static class SourcePickDialog
{
    /// <summary>
    /// 与宿主 <c>aurora.ui.selectfile</c> 隔离的对话框身份。
    /// 不设这个 GUID，Windows 会把上次 CAD 零件库记成同一组 LastVisited。
    /// </summary>
    internal static readonly Guid ClientId = new("8b3c1e6a-2d47-4f91-9a05-6e4c8b17d2f0");

    public static OpenFileDialog Create()
    {
        var start = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new OpenFileDialog
        {
            Title = "选择转换来源",
            RestoreDirectory = true,
            DereferenceLinks = false,
            CheckFileExists = false,
            CheckPathExists = false,
            AddExtension = false,
            AddToRecent = false,
            ValidateNames = false,
            Multiselect = false,
            ClientGuid = ClientId,
            DefaultDirectory = start,
            InitialDirectory = start,
        };
    }

    public static string? Show(Window? owner)
    {
        var dialog = Create();
        var cwd = Environment.CurrentDirectory;
        try
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dialog.InitialDirectory))
                    Environment.CurrentDirectory = dialog.InitialDirectory;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return ok == true && !string.IsNullOrWhiteSpace(dialog.FileName)
                ? dialog.FileName
                : null;
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
