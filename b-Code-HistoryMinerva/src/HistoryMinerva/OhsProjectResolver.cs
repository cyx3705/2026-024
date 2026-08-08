using System.IO;

namespace SE2SW;

public static class OhsProjectResolver
{
    private const string SourceDirectoryName = "b-Module-SE";

    public static ProjectLayout Resolve(string projectRoot)
    {
        var selectedDirectory = NormalizeExistingDirectory(projectRoot, "项目目录不存在");
        var root = string.Equals(
            Path.GetFileName(selectedDirectory),
            SourceDirectoryName,
            StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(selectedDirectory)?.FullName
                ?? throw new DirectoryNotFoundException($"无法从 SE 目录定位项目根目录：{selectedDirectory}")
            : selectedDirectory;
        var source = Path.Combine(root, SourceDirectoryName);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"项目中未找到 b-Module-SE：{source}");

        return new ProjectLayout(
            root,
            source,
            Path.Combine(root, "b-Module-GE"),
            Path.Combine(root, "b-Module-SW"),
            Path.Combine(root, "Unused", "b-Module-GE"),
            Path.Combine(root, "Unused", "b-Module-SW"));
    }

    public static IReadOnlyList<DirectoryMove> GetRequiredMoves(ProjectLayout layout)
    {
        var moves = new List<DirectoryMove>(2);
        AddMoveIfRequired(layout.XtDirectory, layout.UnusedXtDirectory, moves);
        AddMoveIfRequired(layout.SolidWorksDirectory, layout.UnusedSolidWorksDirectory, moves);
        return moves;
    }

    public static IReadOnlyList<DirectoryMove> PrepareOutputDirectories(ProjectLayout layout)
    {
        var moves = GetRequiredMoves(layout);
        var completed = new List<DirectoryMove>(moves.Count);
        try
        {
            foreach (var move in moves)
            {
                Directory.Move(move.Source, move.Destination);
                completed.Add(move);
            }
            return completed;
        }
        catch (Exception moveError)
        {
            var rollbackErrors = new List<string>();
            for (var index = completed.Count - 1; index >= 0; index--)
            {
                var move = completed[index];
                try
                {
                    if (Directory.Exists(move.Destination) && !Directory.Exists(move.Source))
                        Directory.Move(move.Destination, move.Source);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add($"{move.Destination}: {rollbackError.Message}");
                }
            }

            var suffix = rollbackErrors.Count == 0
                ? "已回滚本批次先前移动的目录。"
                : "部分目录回滚失败：" + string.Join("；", rollbackErrors);
            throw new IOException($"准备 OHS 输出目录失败：{moveError.Message} {suffix}", moveError);
        }
    }

    private static void AddMoveIfRequired(string destination, string source, ICollection<DirectoryMove> moves)
    {
        if (Directory.Exists(destination))
            return;
        if (File.Exists(destination))
            throw new IOException($"输出目录位置被同名文件占用：{destination}");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"缺少输出目录，Unused 中也没有候选目录：{source}");
        moves.Add(new DirectoryMove(source, destination));
    }

    private static string NormalizeExistingDirectory(string path, string error)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择项目目录。", nameof(path));
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{error}：{fullPath}");
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
