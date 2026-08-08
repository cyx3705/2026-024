namespace SE2SW.Contracts;

public enum ConversionArtifactKind
{
    Xt,
    SolidWorksPart,
    SolidWorksAssembly,
}

public enum ReuseKind
{
    ExistingXt,
    ExistingSolidWorksPart,
    ExistingSolidWorksAssembly,
}

public sealed record ExternalOutputDirectories(
    string RootDirectory,
    string XtDirectory,
    string SolidWorksDirectory);

public sealed record ConversionPartPaths(
    string XtPath,
    string SolidWorksPath,
    string LegacyXtPath,
    string LegacySolidWorksPath);

/// <summary>
/// 外界模式的输出布局与文件扩展名。它只解析路径，不创建或检查文件。
/// </summary>
public static class ConversionPathLayout
{
    public const string SolidEdgePartExtension = ".par";
    public const string SolidEdgeAssemblyExtension = ".asm";
    public const string XtDirectoryName = "XT";
    public const string SolidWorksDirectoryName = "SW";
    public const string XtExtension = ".x_t";
    public const string SolidWorksPartExtension = ".SLDPRT";
    public const string SolidWorksAssemblyExtension = ".SLDASM";

    public static ExternalOutputDirectories ResolveExternalDirectories(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        return new ExternalOutputDirectories(
            root,
            Path.Combine(root, XtDirectoryName),
            Path.Combine(root, SolidWorksDirectoryName));
    }

    public static ConversionPartPaths ResolvePartPaths(
        string sourcePath,
        string xtDirectory,
        string solidWorksDirectory,
        string legacyDirectory)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return new ConversionPartPaths(
            Path.Combine(xtDirectory, name + XtExtension),
            Path.Combine(solidWorksDirectory, name + SolidWorksPartExtension),
            Path.Combine(legacyDirectory, name + XtExtension),
            Path.Combine(legacyDirectory, name + SolidWorksPartExtension));
    }

    public static string ResolveAssemblyOutputPath(string sourceAssemblyPath, string solidWorksDirectory)
        => Path.Combine(
            solidWorksDirectory,
            Path.GetFileNameWithoutExtension(sourceAssemblyPath) + SolidWorksAssemblyExtension);

    public static string GetExtension(ConversionArtifactKind artifact)
        => artifact switch
        {
            ConversionArtifactKind.Xt => XtExtension,
            ConversionArtifactKind.SolidWorksPart => SolidWorksPartExtension,
            ConversionArtifactKind.SolidWorksAssembly => SolidWorksAssemblyExtension,
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null),
        };

    public static bool HasExtension(string path, ConversionArtifactKind artifact)
        => HasExtension(path, GetExtension(artifact));

    public static bool HasExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
}
