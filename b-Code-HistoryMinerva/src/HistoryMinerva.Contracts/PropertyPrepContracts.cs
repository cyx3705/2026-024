namespace HistoryMinerva.Contracts;

/// <summary>
/// 一个零件要写进 SolidWorks 配置特定属性的那几个值。装配体不写属性，因此只挂在零件条目上。
///
/// 空串在这里有**两种**含义，按槽分：
/// <list type="bullet">
///   <item><b>用户填的槽</b>（日期、设计、材料、表面处理、热处理）空串＝本轮不动这一槽。
///         用户没填不等于要把模板里已有的值抹成空；</item>
///   <item><b>计划算出来的槽</b>（名称、类型）恒写。图号在填了前缀时恒写；
///         V4.15 起**前缀为空＝保留原图号**（<see cref="KeepDrawingNumber"/>），图号槽一个字都不碰——
///         V4.9～V4.14 把空前缀当成删图号，用户没填前缀就点写入，整台设备的图号被抹掉。</item>
/// </list>
/// </summary>
/// <param name="Material">
/// 材质名，例如 <c>304不锈钢</c>。它**不会**被当成文本写进「材料」槽——那一槽是链接，
/// 见 <see cref="PartPropertyNames.Material"/>。必须与 <paramref name="MaterialDatabase"/> 成对出现。
/// </param>
/// <param name="MaterialDatabase">
/// 材质所在的材料库，原样取自 SolidWorks 收藏材料的注册表记录：可能是库名
/// （<c>SOLIDWORKS 材料</c>）也可能是 <c>.sldmat</c> 全路径，两种都能直接喂给
/// <c>SetMaterialPropertyName2</c>，无需归一化。V4.8 追加，缺省空串＝不动零件材质。
/// </param>
/// <param name="Name">
/// 「名称」槽的值，即文件名里图号之后的那一段零件名称，由
/// <see cref="RenameEntry.PartName"/> 带下来，与目标文件名取自同一次计算。
/// V4.9 起用户可以在表里逐行改名称，改完的名字同时决定文件名和这一槽——
/// 仍然只有一份真话，只是这份真话现在可以编辑了。
/// </param>
/// <param name="KeepDrawingNumber">
/// V4.15：本轮没填图号前缀，零件上的「图号」槽保持原样不写。缺省 false＝照旧写 <paramref name="DrawingNumber"/>。
/// </param>
public sealed record PartPropertyWrite(
    string DrawingNumber,
    string Category,
    string Date,
    string Designer,
    string Material,
    string SurfaceTreatment,
    string HeatTreatment,
    string MaterialDatabase = "",
    string Name = "",
    bool KeepDrawingNumber = false)
{
    /// <summary>材质名与材料库齐备，可以应用到零件。</summary>
    public bool HasMaterial
        => !string.IsNullOrEmpty(Material) && !string.IsNullOrEmpty(MaterialDatabase);

    /// <summary>
    /// 只有材质名没有材料库。<c>SetMaterialPropertyName2</c> 传空库名不会报错，只是**什么都不做**，
    /// 于是又变成「跑成功了但材料还是未指定」。Worker 拿它当硬错误，不让这种请求悄悄过去。
    /// </summary>
    public bool MaterialIsIncomplete
        => !string.IsNullOrEmpty(Material) && string.IsNullOrEmpty(MaterialDatabase);

    /// <summary>
    /// 一个槽都写不了时，没有必要为它打开一次 SolidWorks 文档。
    /// 判空的责任在这个记录自己身上，而不该由调用方按"哪几个槽是恒写的"另抄一遍。
    /// </summary>
    public bool IsEmpty => !Pairs().Any();

    /// <summary>
    /// 按 <see cref="PartPropertyNames"/> 的顺序展开成「槽名 → 值」。
    ///
    /// 「图号」在填了前缀时恒在：值由改名计划算出来，与文件名里的图号段是同一个字符串。
    /// 没填前缀（<see cref="KeepDrawingNumber"/>）时不出现——原图号原样留在零件上。
    /// 其余用户填的槽空串＝本轮不动，不会把模板里已有的值抹掉。
    ///
    /// 「材料」一槽给出的是链接记号 <see cref="PartPropertyNames.MaterialLinkValue"/> 而不是材质名：
    /// 材质本身由 <see cref="Material"/> / <see cref="MaterialDatabase"/> 走
    /// <c>SetMaterialPropertyName2</c> 应用；Worker 落盘时把记号换成指向这个零件的完整链接表达式
    /// （<see cref="PartPropertyNames.MaterialLinkExpression"/>），属性标签上显示的才是材质名。
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Pairs()
    {
        if (!KeepDrawingNumber)
            yield return new(PartPropertyNames.DrawingNumber, DrawingNumber ?? string.Empty);
        if (!string.IsNullOrEmpty(Name)) yield return new(PartPropertyNames.Name, Name);
        if (!string.IsNullOrEmpty(Category)) yield return new(PartPropertyNames.Category, Category);
        if (!string.IsNullOrEmpty(Date)) yield return new(PartPropertyNames.Date, Date);
        if (!string.IsNullOrEmpty(Designer)) yield return new(PartPropertyNames.Designer, Designer);
        if (HasMaterial) yield return new(PartPropertyNames.Material, PartPropertyNames.MaterialLinkValue);
        if (!string.IsNullOrEmpty(SurfaceTreatment)) yield return new(PartPropertyNames.SurfaceTreatment, SurfaceTreatment);
        if (!string.IsNullOrEmpty(HeatTreatment)) yield return new(PartPropertyNames.HeatTreatment, HeatTreatment);
    }
}

/// <summary>
/// 属性整备清单中的一个唯一文件。<see cref="Properties"/> 是 V4.7 追加的可选字段，
/// 放在末尾且缺省 <c>null</c>：没有它的历史请求 JSON 仍是一次纯改名。
/// </summary>
/// <param name="PartName">
/// 图号之后的那一段零件名称，与 <see cref="TargetPath"/> 的文件名取自同一次计算，
/// 并写进「名称」属性槽。来源是文件名里第一个空格之后的部分，用户在表里改过的以用户的为准。
/// </param>
/// <param name="DrawingNumber">
/// 这一条要用的图号文本。前缀为空时它是文件名里**原有**的图号段（V4.15 起保留原号），
/// 原来就没有图号的文件则是空串。
/// 它与 <see cref="AssignsDrawingNumber"/> 不是一回事：后者说的是"这个文件归本模块管"，
/// 前者说的是"管出来的号是什么"。
/// </param>
public sealed record RenameEntry(
    string Id,
    string SourcePath,
    string TargetPath,
    string DrawingNumber,
    bool AssignsDrawingNumber,
    IReadOnlyList<string> ParentSourcePaths,
    int Depth,
    PartPropertyWrite? Properties = null,
    string PartName = "");

/// <summary>
/// V4.8：解析装配体时从零件身上**读回来**的三个槽的现值。
///
/// 为什么在探查时读而不是写入时读：这三栏本来就大多已经填过——用户是在既有零件上补图号，
/// 不是从空白开始建库。表格开出来一片空白，用户唯一能做的就是把已有的值再选一遍；
/// 选错一格就把零件上原本正确的材质换掉了，而且看不出来。探查时读回来，
/// 表格显示的才是零件的事实，用户只改需要改的那几格。
///
/// 读数走的是装配里已经载入的组件文档，不为此另开零件——一个几百件的装配那样要开几百次。
/// </summary>
/// <param name="Material">
/// 零件当前材质名。取自 <c>GetMaterialPropertyName2</c> 而不是「材料」槽的文本：
/// 那一槽存的是链接记号 <see cref="PartPropertyNames.MaterialLinkValue"/>，不是材质名。
/// </param>
/// <param name="MaterialDatabase">材质所在的材料库，与 <paramref name="Material"/> 同一次调用读出。</param>
public sealed record PartPropertyReading(
    string SourcePath,
    string Material,
    string MaterialDatabase,
    string SurfaceTreatment,
    string HeatTreatment);

/// <summary>按所选装配体层级生成的改名计划。纯内存，不碰 CAD。</summary>
/// <param name="Excluded">
/// V4.11：因件别不参与编号的文件——被设成外购件或参考的同级件，以及子文件夹里的外购件。
/// 它们不编号、不写属性，在表里占一行，让用户看得见、点得回来。
/// V4.15：其中被改成外购件的**同级**件，<see cref="RenameEntry.TargetPath"/> 指向
/// <c>外购件\</c> 子文件夹——写入时随改名一起挪过去（见 <see cref="Relocations"/>）。
/// 为 null 等同于空（V4.10 及以前的计划没有这一项）。
/// </param>
/// <param name="Relocated">
/// V4.15：跟着外购组件一起挪进 <c>外购件\</c> 的内部件。组件已经是一种货，它们不在表里出现，
/// 但文件得跟着组件走，否则同级目录里留着一堆没人认领的零件。为 null 等同于空。
/// </param>
public sealed record AssemblyRenamePlan(
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    IReadOnlyList<RenameEntry> Unnumbered,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RenameEntry>? Excluded = null,
    IReadOnlyList<RenameEntry>? Relocated = null)
{
    /// <inheritdoc cref="Excluded"/>
    public IReadOnlyList<RenameEntry> ExcludedEntries => Excluded ?? [];

    /// <summary>
    /// 本轮要挪进 <c>外购件\</c> 的全部文件：表里看得见的外购件行，加上跟着组件走的内部件。
    /// </summary>
    public IReadOnlyList<RenameEntry> Relocations
        => ExcludedEntries.Concat(Relocated ?? [])
            .Where(entry => !SamePath(entry.SourcePath, entry.TargetPath))
            .ToArray();

    /// <summary>送给 Worker 的完整清单：编号件（含名字已经对了的，属性还要写）加上要挪的外购件。</summary>
    public IReadOnlyList<RenameEntry> WorkerEntries => [.. Entries, .. Relocations];

    public bool CanRename =>
        BlockingIssues.Count == 0
        && (Entries.Any(entry => !SamePath(entry.SourcePath, entry.TargetPath)) || Relocations.Count > 0);

    /// <summary>
    /// 「写入」= 改名 + 写属性，因此不能沿用 <see cref="CanRename"/>。
    ///
    /// 第二次点写入时文件名早就对了，<see cref="CanRename"/> 是 false；若拿它当门，
    /// 用户改完材料再点写入就会被拒，只能先把文件改回旧名才能写属性。
    /// </summary>
    public bool CanWrite =>
        BlockingIssues.Count == 0
        && (CanRename || Entries.Any(IsWritablePart));

    /// <summary>属性只写识别出的零件：装配体和未编号的内部件都不碰。</summary>
    public static bool IsWritablePart(RenameEntry entry)
        => entry.AssignsDrawingNumber
           && !ConversionPathLayout.IsUnderReferencePartsDirectory(entry.SourcePath)
           && ConversionPathLayout.HasExtension(
               entry.SourcePath, ConversionPathLayout.SolidWorksPartExtension);

    public static bool SamePath(string left, string right)
        => Path.IsPathFullyQualified(left)
           && Path.IsPathFullyQualified(right)
           && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Worker <c>--rename-assembly</c> 请求：改名、把同级外购件挪进 <c>外购件\</c>、写零件属性，一次做完。
///
/// 没有「删图号」这件事（V4.15）：前缀为空只表示本轮不动图号。V4.9～V4.14 曾把空前缀当成删图号，
/// 用户只是没填前缀就点了写入，整台设备的图号被抹掉——那不是用户要的。
/// </summary>
/// <param name="DrawingPrefix">
/// 图号前缀。**空串合法**，表示这一轮不动图号：文件名里的原图号段与「图号」槽都原样保留（V4.15）。
/// </param>
/// <param name="WriteProperties">
/// 为 true 时，改名与引用更新全部成功后再逐个打开零件写 <see cref="RenameEntry.Properties"/>。
/// </param>
public sealed record AssemblyRenameRequest(
    string BatchId,
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    ConversionSourceFormat SourceFormat = ConversionSourceFormat.SolidWorks,
    bool WriteProperties = false);
