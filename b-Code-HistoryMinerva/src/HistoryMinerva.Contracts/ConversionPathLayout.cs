namespace HistoryMinerva.Contracts;

public enum ConversionArtifactKind
{
    Xt,
    SolidWorksPart,
    SolidWorksAssembly,
    // V4.10 整体打包的三种交付产物。枚举按数值序列化，新值只能追加在末尾。
    Step,
    Dwg,
    Pdf,
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
/// V4.11 整体打包的交付目录：装配体旁边**一个**文件夹 <c>&lt;前缀&gt; 零件采购/</c>。
///
/// V4.10 是与装配体平级的 STP / DWG / PDF / BOM 四个目录；现场实际交付时总是把它们
/// 再手工归成一个包发出去（参照 2026-025 a14 的「抽滤模块试制零件采购」），于是改成直接出这个包：
/// 两张 BOM 与同名截图放在包的最外层，机加件的 DWG / PDF / STEP 三件套放进 <c>图纸/</c>。
/// </summary>
/// <param name="RootDirectory">总装配体所在目录。</param>
/// <param name="PackageDirectory">打包目录，BOM 与截图直接放在这里。</param>
/// <param name="DrawingDirectory">打包目录下的 <c>图纸/</c>，机加件三件套的去处。</param>
public sealed record PackageOutputDirectories(
    string RootDirectory,
    string PackageDirectory,
    string DrawingDirectory);

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

    /// <summary>
    /// V4.10：工程图。整体打包按「与零件同名、同目录」这一条唯一规则找它，
    /// 不递归、不跨目录——递归找同名图会把别的项目的重名图纸打进这一次交付。
    /// </summary>
    public const string SolidWorksDrawingExtension = ".SLDDRW";

    public const string StepExtension = ".STEP";
    public const string DwgExtension = ".DWG";
    public const string PdfExtension = ".PDF";
    /// <summary>V4.11 打包目录名的后缀：<c>&lt;前缀&gt; 零件采购</c>。</summary>
    public const string PackageDirectorySuffix = " 零件采购";
    public const string DrawingDirectoryName = "图纸";
    public const string ReferencePartsDirectoryName = "参考部件";

    /// <summary>源零件扩展名。SW 特征整备的源就是 <c>.SLDPRT</c> 本身。</summary>
    public static string GetSourcePartExtension(ConversionSourceFormat format)
        => format == ConversionSourceFormat.SolidWorks ? SolidWorksPartExtension : SolidEdgePartExtension;

    /// <summary>源装配扩展名。</summary>
    public static string GetSourceAssemblyExtension(ConversionSourceFormat format)
        => format == ConversionSourceFormat.SolidWorks ? SolidWorksAssemblyExtension : SolidEdgeAssemblyExtension;

    /// <summary>
    /// 该源格式是否经过交付用 Parasolid 中转。Solid Edge 与 SolidWorks 特征整备都先落到
    /// <c>XT/</c> 再导入识别；属性整备改名不走本布局。
    /// </summary>
    public static bool UsesParasolidHandoff(ConversionSourceFormat format)
        => format is ConversionSourceFormat.SolidEdge or ConversionSourceFormat.SolidWorks;

    /// <summary>
    /// 该源格式是否可能留下 V3.0 时代的旧平铺产物（产物与源同目录、同基名）。
    ///
    /// Solid Edge 源有：<c>A.par</c> 旁边的 <c>A.SLDPRT</c> 确实是上一代的产物。
    /// SolidWorks 源没有：那个位置按构造就是**源零件本身**。把它当成旧产物，会让
    /// 自整备的每个零件都被判成"已存在"，并把产物路径改写回源文件——计划阶段随即
    /// 以"产物会覆盖源零件本身"整轮阻断，用户一个零件都整备不了。
    /// </summary>
    public static bool HasLegacyFlatLayout(ConversionSourceFormat format)
        => format == ConversionSourceFormat.SolidEdge;

    public static ExternalOutputDirectories ResolveExternalDirectories(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        return new ExternalOutputDirectories(
            root,
            Path.Combine(root, XtDirectoryName),
            Path.Combine(root, SolidWorksDirectoryName));
    }

    /// <summary>
    /// V4.11：按源装配体所在目录与 BOM 前缀解析打包目录。只算路径，不建目录、不看存在性。
    /// </summary>
    public static PackageOutputDirectories ResolvePackageDirectories(string assemblyDirectory, string namePrefix)
    {
        var root = Path.GetFullPath(assemblyDirectory);
        var package = Path.Combine(root, namePrefix.Trim() + PackageDirectorySuffix);
        return new PackageOutputDirectories(root, package, Path.Combine(package, DrawingDirectoryName));
    }

    /// <summary>
    /// V4.10：与零件同目录、同主名的工程图路径。**只算路径**，存在性由调用方判断。
    /// </summary>
    public static string ResolveDrawingPath(string partPath)
        => Path.ChangeExtension(Path.GetFullPath(partPath), SolidWorksDrawingExtension);

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
            ConversionArtifactKind.Step => StepExtension,
            ConversionArtifactKind.Dwg => DwgExtension,
            ConversionArtifactKind.Pdf => PdfExtension,
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null),
        };

    public static bool HasExtension(string path, ConversionArtifactKind artifact)
        => HasExtension(path, GetExtension(artifact));

    public static bool HasExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 带 CAD 扩展名的是文件，不是零件文件夹。来源选择不得靠扩展名在两种输入之间跳。
    /// </summary>
    public static bool IsKnownCadFile(string path)
        => HasExtension(path, SolidEdgePartExtension)
            || HasExtension(path, SolidEdgeAssemblyExtension)
            || HasExtension(path, SolidWorksPartExtension)
            || HasExtension(path, SolidWorksAssemblyExtension)
            || HasExtension(path, SolidWorksDrawingExtension);

    /// <summary>
    /// 正式零件与所选装配体同级。路径落在子文件夹或其它目录即为外购件。
    /// 空路径不算外购件，留给未解析引用诊断。
    /// </summary>
    public static bool IsOutsideAssemblyDirectory(string filePath, string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(assemblyPath))
            return false;

        try
        {
            var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath.Trim()));
            var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath.Trim()));
            return !string.IsNullOrWhiteSpace(fileDirectory)
                && !string.IsNullOrWhiteSpace(assemblyDirectory)
                && !string.Equals(fileDirectory, assemblyDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 文件是否位于名为 <c>参考部件</c> 的目录之下。
    ///
    /// 该目录是现场明确标出的参考资料，不是外购件目录：其下的零件不参与属性整备，
    /// 也不进入任一打包清单或导出作业。检查目录段而不是字符串前缀，避免把
    /// <c>参考部件备份</c> 之类的无关目录误排除。
    /// </summary>
    public static bool IsUnderReferencePartsDirectory(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            for (var directory = Path.GetDirectoryName(Path.GetFullPath(filePath.Trim()));
                 !string.IsNullOrEmpty(directory);
                 directory = Path.GetDirectoryName(directory))
            {
                if (string.Equals(
                        Path.GetFileName(directory),
                        ReferencePartsDirectoryName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return false;
    }
}
