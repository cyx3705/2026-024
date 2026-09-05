using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public enum MappingContent
{
    SolidEdgePartToSolidWorksPart,
    SolidEdgeAssemblyToSolidWorksAssembly,
    // V4.3：SolidWorks 自整备。源与产物都是 SolidWorks，做的是"补上特征树"，不是格式转换。
    SolidWorksAssemblyToSolidWorksAssembly,
    // V4.3.9：SolidWorks 属性整备。本版只做按图号改名，不改其它属性。
    SolidWorksAssemblyPropertyPrep,
    // V4.10：整体打包。选总装配体，一次生成 STP / DWG / PDF / BOM 四个交付目录。
    SolidWorksAssemblyPackage,
}

/// <param name="Kind">转换内容枚举值。</param>
/// <param name="DisplayName">下拉列表里显示的文字。</param>
/// <param name="SourceHint">选择来源时给用户的提示。</param>
/// <param name="SourceFormat">该项的源 CAD 格式，是界面到 Worker 之间唯一的格式真值来源。</param>
/// <param name="IsAssemblySource">源是单个装配体文件（而不是零件文件夹）。</param>
public sealed record MappingContentOption(
    MappingContent Kind,
    string DisplayName,
    string SourceHint,
    ConversionSourceFormat SourceFormat,
    bool IsAssemblySource)
{
    public static IReadOnlyList<MappingContentOption> Available { get; } =
    [
        new(
            MappingContent.SolidEdgePartToSolidWorksPart,
            "Solid Edge .par → SolidWorks .SLDPRT",
            "选择包含顶层 .par 文件的文件夹",
            ConversionSourceFormat.SolidEdge,
            IsAssemblySource: false),
        new(
            MappingContent.SolidEdgeAssemblyToSolidWorksAssembly,
            "Solid Edge .asm → SolidWorks .SLDASM",
            "选择单个 .asm 装配体文件",
            ConversionSourceFormat.SolidEdge,
            IsAssemblySource: true),
        new(
            MappingContent.SolidWorksAssemblyToSolidWorksAssembly,
            "SolidWorks .SLDASM → SolidWorks .SLDASM（特征整备）",
            "选择单个 .SLDASM 装配体文件",
            ConversionSourceFormat.SolidWorks,
            IsAssemblySource: true),
        new(
            MappingContent.SolidWorksAssemblyPropertyPrep,
            "SolidWorks .SLDASM → 属性整备（改名）",
            "选择单个 .SLDASM 装配体，并填写图号前缀",
            ConversionSourceFormat.SolidWorks,
            IsAssemblySource: true),
        new(
            MappingContent.SolidWorksAssemblyPackage,
            "SolidWorks .SLDASM → 整体打包（STP/DWG/PDF/BOM）",
            "选择总装配体 .SLDASM，一次生成四个交付目录",
            ConversionSourceFormat.SolidWorks,
            IsAssemblySource: true),
    ];

    /// <summary>按源装配文件的扩展名选出对应的转换内容。识别不出时返回 null。</summary>
    public static MappingContentOption? ForAssemblyFile(string path)
        => Available.FirstOrDefault(option => option.IsAssemblySource
            && ConversionPathLayout.HasExtension(
                path, ConversionPathLayout.GetSourceAssemblyExtension(option.SourceFormat)));

    public string SourceRequirement => IsAssemblySource
        ? $"请选择单个 {ConversionPathLayout.GetSourceAssemblyExtension(SourceFormat)} 装配体文件，不要选择文件夹"
        : "请选择零件文件夹，不要选择装配体或零件文件";

    public bool AcceptsSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (IsAssemblySource)
            return ConversionPathLayout.HasExtension(
                path, ConversionPathLayout.GetSourceAssemblyExtension(SourceFormat));
        return !ConversionPathLayout.IsKnownCadFile(path);
    }
}
