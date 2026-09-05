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
/// V4.10 整体打包的四个交付目录，与源装配体同级。
///
/// 四个目录**平级**放在装配体旁边，与既有的 <c>XT/</c>、<c>SW/</c> 同一层，
/// 而不是再收进一个「打包」根目录：交付时用户是逐个目录拖给不同的人
/// （STP 给加工厂、DWG/PDF 给图纸审核、BOM 给采购），多一层壳只是多一次点击。
/// </summary>
public sealed record PackageOutputDirectories(
    string RootDirectory,
    string StepDirectory,
    string DwgDirectory,
    string PdfDirectory,
    string BomDirectory);

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
    public const string StepDirectoryName = "STP";
    public const string DwgDirectoryName = "DWG";
    public const string PdfDirectoryName = "PDF";
    public const string BomDirectoryName = "BOM";

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
    /// V4.10：按源装配体所在目录解析四个打包目录。只算路径，不建目录、不看存在性。
    /// </summary>
    public static PackageOutputDirectories ResolvePackageDirectories(string assemblyDirectory)
    {
        var root = Path.GetFullPath(assemblyDirectory);
        return new PackageOutputDirectories(
            root,
            Path.Combine(root, StepDirectoryName),
            Path.Combine(root, DwgDirectoryName),
            Path.Combine(root, PdfDirectoryName),
            Path.Combine(root, BomDirectoryName));
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
}
