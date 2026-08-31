using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public static class FileScanner
{
    public static bool HasPartFiles(string selectedDirectory)
    {
        var workingDirectory = NormalizeExistingDirectory(selectedDirectory);
        return Directory.EnumerateFiles(
                workingDirectory,
                "*" + ConversionPathLayout.SolidEdgePartExtension,
                SearchOption.TopDirectoryOnly)
            .Any(path => ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidEdgePartExtension));
    }

    public static IReadOnlyList<ScanCandidate> Scan(
        ConversionMode mode,
        string selectedDirectory,
        ProjectLayout? layout = null,
        ExternalOutputDirectories? outputDirectories = null,
        bool allowLegacyXt = true,
        bool allowLegacySolidWorks = true)
    {
        var workingDirectory = NormalizeExistingDirectory(selectedDirectory);
        var sourceDirectory = mode == ConversionMode.Ohs
            ? layout?.SourceDirectory ?? throw new ArgumentNullException(nameof(layout))
            : workingDirectory;
        var externalDirectories = mode == ConversionMode.External
            ? outputDirectories ?? ConversionPathLayout.ResolveExternalDirectories(workingDirectory)
            : null;
        var xtDirectory = mode == ConversionMode.Ohs ? layout!.XtDirectory : externalDirectories!.XtDirectory;
        var swDirectory = mode == ConversionMode.Ohs ? layout!.SolidWorksDirectory : externalDirectories!.SolidWorksDirectory;

        return Directory.EnumerateFiles(
                sourceDirectory,
                "*" + ConversionPathLayout.SolidEdgePartExtension,
                SearchOption.TopDirectoryOnly)
            .Where(path => ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidEdgePartExtension))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path =>
            {
                var paths = ConversionPathLayout.ResolvePartPaths(path, xtDirectory, swDirectory, workingDirectory);
                var exists = File.Exists(paths.XtPath) || File.Exists(paths.SolidWorksPath)
                    || mode == ConversionMode.External &&
                    ((allowLegacyXt && File.Exists(paths.LegacyXtPath))
                        || (allowLegacySolidWorks && File.Exists(paths.LegacySolidWorksPath)));
                return new ScanCandidate(path, paths.XtPath, paths.SolidWorksPath, exists);
            })
            .ToArray();
    }

    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择文件夹。", nameof(path));
        return Path.GetFullPath(path.Trim());
    }
}
