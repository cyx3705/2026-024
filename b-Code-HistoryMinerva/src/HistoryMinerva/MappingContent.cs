namespace HistoryMinerva;

public enum MappingContent
{
    SolidEdgePartToSolidWorksPart,
    SolidEdgeAssemblyToSolidWorksAssembly,
}

public sealed record MappingContentOption(
    MappingContent Kind,
    string DisplayName,
    string SourceHint)
{
    public static IReadOnlyList<MappingContentOption> Available { get; } =
    [
        new(
            MappingContent.SolidEdgePartToSolidWorksPart,
            "Solid Edge .par → SolidWorks .SLDPRT",
            "选择包含顶层 .par 文件的文件夹"),
        new(
            MappingContent.SolidEdgeAssemblyToSolidWorksAssembly,
            "Solid Edge .asm → SolidWorks .SLDASM",
            "选择单个 .asm 装配体文件"),
    ];
}
