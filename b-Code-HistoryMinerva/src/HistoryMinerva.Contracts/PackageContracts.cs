namespace HistoryMinerva.Contracts;

/// <summary>
/// V4.10 整体打包里一个零件的件别，决定它进哪一张 BOM。
///
/// 判据沿用本模块既有的唯一口径 <see cref="ConversionPathLayout.IsOutsideAssemblyDirectory"/>：
/// 与所选总装配体**同级**的是自制机加件，落在子文件夹或别处的是外购件。
/// 不另立「按属性槽判」或「按有没有工程图判」的第二套规则——同一件事有两个判据，
/// 现场就会出现同一个零件在改名管线里算机加件、在打包管线里算外购件。
/// </summary>
public enum PackagePartCategory
{
    Machined,
    Purchased,
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
public sealed record PackagePartEntry(
    string Id,
    string SourcePath,
    string? DrawingPath,
    string DrawingNumber,
    string PartName,
    string Specification,
    int Quantity,
    PackagePartCategory Category)
{
    public bool HasDrawing => !string.IsNullOrEmpty(DrawingPath);

    /// <summary>BOM 与表格显示用的文件名（含扩展名）。</summary>
    public string FileName => Path.GetFileName(SourcePath);
}

/// <summary>
/// 一次整体打包的完整计划。纯内存，不碰 CAD、不建目录。
/// </summary>
/// <param name="BomNamePrefix">
/// 两张 BOM 的文件名前缀，取自总装配体图号里**去掉全部纯数字段**之后剩下的部分：
/// <c>GHLSS-06-00 总装.SLDASM</c> → <c>GHLSS</c>，于是 BOM 叫
/// <c>GHLSS 机加件清单.xlsx</c>。认不出来时退回装配体主名。
/// </param>
public sealed record PackagePlan(
    string SourceAssemblyPath,
    PackageOutputDirectories Directories,
    string BomNamePrefix,
    IReadOnlyList<PackagePartEntry> Entries,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    /// <summary>机加件，按图号排序。第一张 BOM 的内容。</summary>
    public IReadOnlyList<PackagePartEntry> Machined { get; } = Entries
        .Where(entry => entry.Category == PackagePartCategory.Machined)
        .ToArray();

    /// <summary>外购件，按规格排序。第二张 BOM 的内容。</summary>
    public IReadOnlyList<PackagePartEntry> Purchased { get; } = Entries
        .Where(entry => entry.Category == PackagePartCategory.Purchased)
        .ToArray();

    /// <summary>
    /// 要导出 STEP 的零件：**只有机加件**。标准件厂商按规格供货，不需要你给模型，
    /// 而 Toolbox 螺钉螺母一批几十上百个，逐个导 STEP 只是把打包拖慢（用户确认）。
    /// </summary>
    public IReadOnlyList<PackagePartEntry> StepTargets => Machined;

    /// <summary>要导出 DWG / PDF 的工程图来源：识别到同名图纸的零件。</summary>
    public IReadOnlyList<PackagePartEntry> DrawingTargets { get; } = Entries
        .Where(entry => entry.HasDrawing)
        .ToArray();

    public bool CanPack => BlockingIssues.Count == 0 && Entries.Count > 0;

    public string MachinedBomFileName => BomNamePrefix + " 机加件清单.xlsx";

    public string PurchasedBomFileName => BomNamePrefix + " 外购件清单.xlsx";
}

/// <summary>
/// 一个导出作业。<paramref name="SourcePath"/> 是零件或工程图，
/// <paramref name="OutputPath"/> 已经落在对应的 STP / DWG / PDF 目录里。
/// </summary>
public sealed record PackageJob(
    string Id,
    string SourcePath,
    string OutputPath,
    PackageArtifact Artifact);

/// <summary>
/// Worker <c>--package-assembly</c> 请求。只做 CAD 导出——两张 BOM 不需要 SolidWorks，
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
