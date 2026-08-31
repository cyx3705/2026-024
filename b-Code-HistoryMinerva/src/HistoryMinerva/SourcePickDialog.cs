using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HistoryMinerva;

/// <summary>
/// 来源对话框。装配体转换打开文件，零件转换打开文件夹，两套不得混用。
/// 每次从桌面起步，不沿用宿主上次 CAD 目录。
/// </summary>
internal static class SourcePickDialog
{
    internal static readonly Guid FileClientId = new("8b3c1e6a-2d47-4f91-9a05-6e4c8b17d2f0");
    internal static readonly Guid FolderClientId = new("c4d9a2b7-6e18-4c3f-8b40-1a7d5e9f3c21");

    internal static readonly Guid ClientId = FileClientId;

    public static OpenFileDialog Create() => CreateFile();

    public static OpenFileDialog CreateFile()
    {
        var start = StartDirectory();
        return new OpenFileDialog
        {
            Title = "选择装配体文件",
            RestoreDirectory = true,
            DereferenceLinks = false,
            CheckFileExists = false,
            CheckPathExists = false,
            AddExtension = false,
            AddToRecent = false,
            ValidateNames = false,
            Multiselect = false,
            ClientGuid = FileClientId,
            DefaultDirectory = start,
            InitialDirectory = start,
        };
    }

    public static OpenFolderDialog CreateFolder()
    {
        var start = StartDirectory();
        return new OpenFolderDialog
        {
            Title = "选择零件文件夹",
            Multiselect = false,
            AddToRecent = false,
            ClientGuid = FolderClientId,
            DefaultDirectory = start,
            InitialDirectory = start,
        };
    }

    public static string? Show(Window? owner) => ShowFile(owner);

    public static string? ShowFile(Window? owner)
    {
        var dialog = CreateFile();
        return ShowAndRestore(dialog.InitialDirectory, () =>
        {
            var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return ok == true && !string.IsNullOrWhiteSpace(dialog.FileName)
                ? dialog.FileName
                : null;
        });
    }

    public static string? ShowFolder(Window? owner)
    {
        var dialog = CreateFolder();
        return ShowAndRestore(dialog.InitialDirectory, () =>
        {
            var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return ok == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
                ? dialog.FolderName
                : null;
        });
    }

    private static string StartDirectory()
    {
        var start = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return start;
    }

    private static string? ShowAndRestore(string start, Func<string?> show)
    {
        var cwd = Environment.CurrentDirectory;
        try
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(start))
                    Environment.CurrentDirectory = start;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return show();
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
