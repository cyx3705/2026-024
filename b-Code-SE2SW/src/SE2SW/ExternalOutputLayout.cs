using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public static class ExternalOutputLayout
{
    public static ExternalOutputDirectories Resolve(string sourceDirectory)
        => ConversionPathLayout.ResolveExternalDirectories(sourceDirectory);

    public static void EnsureDirectories(string sourceDirectory)
    {
        var directories = Resolve(sourceDirectory);
        var conflicts = new[] { directories.XtDirectory, directories.SolidWorksDirectory }.Where(File.Exists).ToArray();
        if (conflicts.Length > 0)
            throw new IOException($"输出目录被同名文件占用：{string.Join("；", conflicts)}");
        Directory.CreateDirectory(directories.XtDirectory);
        Directory.CreateDirectory(directories.SolidWorksDirectory);
    }
}
