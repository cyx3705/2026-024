using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public static class ExternalOutputLayout
{
    public static ExternalOutputDirectories Resolve(
        string sourceDirectory,
        string? xtDirectory = null,
        string? solidWorksDirectory = null)
    {
        var defaults = ConversionPathLayout.ResolveExternalDirectories(sourceDirectory);
        return new ExternalOutputDirectories(
            defaults.RootDirectory,
            NormalizeOverride(xtDirectory, defaults.XtDirectory),
            NormalizeOverride(solidWorksDirectory, defaults.SolidWorksDirectory));
    }

    public static void EnsureDirectories(string sourceDirectory)
    {
        var directories = Resolve(sourceDirectory);
        EnsureDirectories(directories.XtDirectory, directories.SolidWorksDirectory);
    }

    /// <summary>
    /// <paramref name="xtDirectory"/> 传 null 表示本次没有 Parasolid 中转件
    /// （SW 自整备管线），此时不创建空的 XT 目录。
    /// </summary>
    public static void EnsureDirectories(string? xtDirectory, string solidWorksDirectory)
    {
        var directories = (string.IsNullOrWhiteSpace(xtDirectory)
                ? new[] { Path.GetFullPath(solidWorksDirectory) }
                : [Path.GetFullPath(xtDirectory), Path.GetFullPath(solidWorksDirectory)])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var conflicts = directories.Where(File.Exists).ToArray();
        if (conflicts.Length > 0)
            throw new IOException($"输出目录被同名文件占用：{string.Join("；", conflicts)}");
        foreach (var directory in directories)
            Directory.CreateDirectory(directory);
    }

    private static string NormalizeOverride(string? path, string defaultPath)
        => string.IsNullOrWhiteSpace(path) ? defaultPath : Path.GetFullPath(path.Trim());
}
