namespace HistoryMinerva.Contracts;

/// <summary>
/// 零件 / 子装配体的件别，决定它进哪一张 BOM、要不要编号和导出。
///
/// **缺省**仍按本模块既有的唯一口径由路径推出（<see cref="PartKinds.Default"/>）：
/// 与所选总装配体同级的是机加件，落在子文件夹或别处的是外购件，
/// 落在 <c>参考部件</c> 目录下的是参考。V4.11 起用户可以在属性整备与打包两张表里
/// 逐行改写它（<see cref="PartKinds.Resolve"/>），两张表共用同一份改写记账——
/// 同一个零件不会在改名管线里算机加件、在打包管线里算外购件。
///
/// 枚举按数值序列化，新值只能追加在末尾。
/// </summary>
public enum PackagePartCategory
{
    Machined,
    Purchased,

    /// <summary>V4.11：参考件。不编号、不写属性、不进任一 BOM、不导出；子装配体连同其内部件一起排除。</summary>
    Reference,
}

/// <summary>
/// V4.11 件别的判定与轮换。界面上的件别格与 Janus 的「操作」格同一个手感：
/// 显示「件别 + 符号」，点一下换到下一种。
/// </summary>
public static class PartKinds
{
    /// <summary>按路径推出的缺省件别，与 V4.10 的判据逐条一致。</summary>
    public static PackagePartCategory Default(string path, string rootAssemblyPath)
    {
        if (ConversionPathLayout.IsUnderReferencePartsDirectory(path))
            return PackagePartCategory.Reference;
        return ConversionPathLayout.IsOutsideAssemblyDirectory(path, rootAssemblyPath)
            ? PackagePartCategory.Purchased
            : PackagePartCategory.Machined;
    }

    /// <summary>
    /// 用户改写过的以用户的为准，否则取缺省。
    /// <paramref name="edits"/> 按源文件全路径记账，键的大小写规则由调用方的字典决定。
    /// <c>参考部件</c> 目录下的件不认改写，见 <see cref="IsFixed"/>。
    /// </summary>
    public static PackagePartCategory Resolve(
        IReadOnlyDictionary<string, PackagePartCategory>? edits,
        string path,
        string rootAssemblyPath)
    {
        if (!IsFixed(path)
            && edits is { Count: > 0 }
            && TryFullPath(path, out var full)
            && edits.TryGetValue(full, out var kind))
        {
            return kind;
        }

        return Default(path, rootAssemblyPath);
    }

    /// <summary>
    /// 件别不能改的件：<c>参考部件</c> 目录下的一切。那是现场明确标出的参考资料，
    /// Worker 也会拒绝改名、写属性或导出它们——放开改写只会换来一次失败的打包。
    /// </summary>
    public static bool IsFixed(string path) => ConversionPathLayout.IsUnderReferencePartsDirectory(path);

    /// <summary>
    /// 点一下之后的件别：机加件 → 外购件 → 参考 → 机加件。
    ///
    /// 子文件夹里的装配体跳过「机加件」：探查不打开外购装配体，拿不到它的内部层级，
    /// 设成自制组件也展不开，只会让它和它里面的件一起从清单上消失。
    /// </summary>
    public static PackagePartCategory Next(PackagePartCategory current, string path, string rootAssemblyPath)
    {
        if (IsFixed(path))
            return PackagePartCategory.Reference;
        var next = current switch
        {
            PackagePartCategory.Machined => PackagePartCategory.Purchased,
            PackagePartCategory.Purchased => PackagePartCategory.Reference,
            _ => PackagePartCategory.Machined,
        };
        if (next == PackagePartCategory.Machined && !CanBeMachined(path, rootAssemblyPath))
            next = PackagePartCategory.Purchased;
        return next;
    }

    /// <summary>子文件夹里的装配体没有展开过的内部层级，不能当自制组件。</summary>
    public static bool CanBeMachined(string path, string rootAssemblyPath)
        => !(ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksAssemblyExtension)
             && ConversionPathLayout.IsOutsideAssemblyDirectory(path, rootAssemblyPath));

    /// <summary>件别的文字。</summary>
    public static string Label(PackagePartCategory kind) => kind switch
    {
        PackagePartCategory.Machined => "机加件",
        PackagePartCategory.Purchased => "外购件",
        PackagePartCategory.Reference => "参考",
        _ => kind.ToString(),
    };

    /// <summary>件别格的「文字 + 符号」，写法照 Janus「操作」格。</summary>
    public static string Cell(PackagePartCategory kind) => kind switch
    {
        PackagePartCategory.Machined => "机加件 ●",
        PackagePartCategory.Purchased => "外购件 ◆",
        PackagePartCategory.Reference => "参考 ○",
        _ => kind.ToString(),
    };

    internal static bool TryFullPath(string path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            full = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>整体打包要导出的三种产物。枚举按数值序列化，新值只能追加在末尾。</summary>
public enum PackageArtifact
{
    /// <summary>零件 <c>.SLDPRT → .STEP</c>。</summary>
    Step,

    /// <summary>工程图 <c>.SLDDRW → .DWG</c>。</summary>
    Dwg,

    /// <summary>工程图 <c>.SLDDRW → .PDF</c>。</summary>
    Pdf,
}

/// <summary>
/// 打包清单里的一个唯一零件。
/// </summary>
/// <param name="Id">
/// 行身份，按源文件全路径定死（与 <c>PropertyPrepPlanner</c> 同一约定）。
/// 重新解析后同一个零件仍是同一行，界面可以原地更新而不是整表重画。
/// </param>
/// <param name="DrawingPath">同名同目录的 <c>.SLDDRW</c>；没有图纸时为 <c>null</c>。</param>
/// <param name="DrawingNumber">
/// 零件图号，取自**当前文件名**里第一个空格之前的那一段（<see cref="Contracts.DrawingNumber.SplitFileName"/>）。
/// BOM 描述的是此刻磁盘上的事实，不是「改名之后应该是什么」——所以这里不重新编号。
/// </param>
/// <param name="PartName">零件名称，即文件名里图号之后的那一段。</param>
/// <param name="Specification">
/// 外购件的「规格」，即文件名里的非中文字段；机加件恒为空串。
/// </param>
/// <param name="Quantity">
/// 该零件在整个总装配体里的实例总数，含嵌套倍数，抑制件不计。
/// </param>
/// <param name="IsAssembly">
/// V4.11：这一行是子装配体。机加件（自制组件）子装配体只在表里占一行供改件别，
/// 不进 BOM、不导出——它里面的件各自进清单；外购件子装配体整体算一种货。
/// </param>
public sealed record PackagePartEntry(
    string Id,
    string SourcePath,
    string? DrawingPath,
    string DrawingNumber,
    string PartName,
    string Specification,
    int Quantity,
    PackagePartCategory Category,
    bool IsAssembly = false)
{
    public bool HasDrawing => !string.IsNullOrEmpty(DrawingPath);

    /// <summary>BOM 与表格显示用的文件名（含扩展名）。</summary>
    public string FileName => Path.GetFileName(SourcePath);
}

/// <summary>
/// 一次整体打包的完整计划。纯内存，不碰 CAD、不建目录。
/// </summary>
/// <param name="BomNamePrefix">
/// 两张 BOM 与打包目录的文件名前缀，取自总装配体图号里**去掉全部纯数字段**之后剩下的部分：
/// <c>GHLSS-06-00 总装.SLDASM</c> → <c>GHLSS</c>，于是 BOM 叫
/// <c>GHLSS 机加件清单.xlsx</c>、打包目录叫 <c>GHLSS 零件采购</c>。认不出来时退回装配体主名。
/// </param>
/// <param name="Entries">
/// 表里的全部行：机加件、外购件、参考件，以及自制子装配体。BOM 与导出只取其中该取的那几类。
/// </param>
public sealed record PackagePlan(
    string SourceAssemblyPath,
    PackageOutputDirectories Directories,
    string BomNamePrefix,
    IReadOnlyList<PackagePartEntry> Entries,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    /// <summary>机加件零件，按图号排序。第一张 BOM 的内容；自制子装配体不在其中。</summary>
    public IReadOnlyList<PackagePartEntry> Machined { get; } = Entries
        .Where(entry => entry.Category == PackagePartCategory.Machined && !entry.IsAssembly)
        .ToArray();

    /// <summary>外购件（零件或整体外购的子装配体），按规格排序。第二张 BOM 的内容。</summary>
    public IReadOnlyList<PackagePartEntry> Purchased { get; } = Entries
        .Where(entry => entry.Category == PackagePartCategory.Purchased)
        .ToArray();

    /// <summary>
    /// 要导出 STEP 的零件：**只有机加件**。标准件厂商按规格供货，不需要你给模型，
    /// 而 Toolbox 螺钉螺母一批几十上百个，逐个导 STEP 只是把打包拖慢（用户确认）。
    /// </summary>
    public IReadOnlyList<PackagePartEntry> StepTargets => Machined;

    /// <summary>
    /// 要导出 DWG / PDF 的工程图来源。V4.11 起**只有机加件**：
    /// 「图纸」目录是给加工厂的三件套（DWG / PDF / STEP），外购件的图没有人要。
    /// </summary>
    public IReadOnlyList<PackagePartEntry> DrawingTargets { get; } = Entries
        .Where(entry => entry.Category == PackagePartCategory.Machined && !entry.IsAssembly && entry.HasDrawing)
        .ToArray();

    /// <summary>两张清单至少有一张有内容才打包。表里只剩参考件或自制组件时没有东西可交付。</summary>
    public bool CanPack => BlockingIssues.Count == 0 && (Machined.Count > 0 || Purchased.Count > 0);

    public string MachinedBomFileName => BomNamePrefix + " 机加件清单.xlsx";

    public string PurchasedBomFileName => BomNamePrefix + " 外购件清单.xlsx";

    /// <summary>V4.11：与 BOM 同名的截图，放在 BOM 旁边。</summary>
    public string MachinedBomImageFileName => BomNamePrefix + " 机加件清单.png";

    /// <inheritdoc cref="MachinedBomImageFileName"/>
    public string PurchasedBomImageFileName => BomNamePrefix + " 外购件清单.png";
}

/// <summary>
/// 一个导出作业。<paramref name="SourcePath"/> 是零件或工程图，
/// <paramref name="OutputPath"/> 已经落在打包目录的 <c>图纸/</c> 里。
/// </summary>
public sealed record PackageJob(
    string Id,
    string SourcePath,
    string OutputPath,
    PackageArtifact Artifact);

/// <summary>
/// Worker <c>--package-assembly</c> 请求。只做 CAD 导出——BOM 与截图不需要 SolidWorks，
/// 由前端在同一轮里自己写，Worker 起不起得来都不影响采购拿到清单。
/// </summary>
/// <param name="Overwrite">
/// 缺省 <c>true</c>：打包要的是一份**与当前模型一致**的交付包，留着上一轮的旧 STEP
/// 比缺一个文件更危险（用户确认）。这与转换管线默认不覆盖是两件事——那边保护的是
/// 用户手工整备过的产物，这边的产物本来就由本模块整批生成。
/// </param>
public sealed record PackageRequest(
    string BatchId,
    string SourceAssemblyPath,
    IReadOnlyList<PackageJob> Jobs,
    bool Overwrite = true);

/// <summary>
/// V4.10：探查时收下的一个外购件读数。
///
/// 外购件不进转换计划、不打开模型、不读属性（见 <c>SolidWorksAssemblyExplorer</c>），
/// 但采购要的正是它们的数量，所以这里只收两件按路径就能确定的事实：是谁、有几个。
/// </summary>
/// <param name="InstanceCount">整个总装配体里的实例总数，抑制件不计。</param>
public sealed record PurchasedPartReading(string SourcePath, int InstanceCount);
