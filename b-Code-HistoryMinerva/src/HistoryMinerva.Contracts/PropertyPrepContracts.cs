namespace HistoryMinerva.Contracts;

/// <summary>
/// 批量改名清单中的一个唯一文件。
/// </summary>
/// <param name="PartName">
/// 图号之后的那一段零件名称，与 <see cref="TargetPath"/> 的文件名取自同一次计算，
/// 来源是文件名里第一个空格之后的部分，用户在表里改过的以用户的为准。
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
    string PartName = "");

/// <summary>
/// 解析装配体时从外购件身上读回来的「名称」槽：外购件文件名里常没有名称，打包清单要用。
/// 机加件的图号、名称一律取文件名，不读这里（属性归 HistoryStrenua 写，可能落后于文件名）。
/// </summary>
/// <param name="Name">「名称」槽的值；空串表示读不到。</param>
public sealed record PartPropertyReading(
    string SourcePath,
    string Name = "");

/// <summary>按所选装配体层级生成的改名计划。纯内存，不碰 CAD。</summary>
/// <param name="Excluded">
/// V4.11：因件别不参与编号的文件——被设成外购件或参考的同级件，以及子文件夹里的外购件。
/// 它们不编号，在表里占一行，让用户看得见、点得回来。
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

    /// <summary>送给 Worker 的完整清单：编号件加上要挪的外购件。</summary>
    public IReadOnlyList<RenameEntry> WorkerEntries => [.. Entries, .. Relocations];

    public bool CanRename =>
        BlockingIssues.Count == 0
        && (Entries.Any(entry => !SamePath(entry.SourcePath, entry.TargetPath)) || Relocations.Count > 0);

    public static bool SamePath(string left, string right)
        => Path.IsPathFullyQualified(left)
           && Path.IsPathFullyQualified(right)
           && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这一轮会被改走的源文件（全路径）。重排号时号码在同名件之间互换，
    /// 某个目标名此刻正被另一个条目的源文件占着——那不算冲突：Worker 先把这些源挪到临时名，
    /// 再逐个落到目标名上。规划与 Worker 校验都靠它区分「互换」和「真被计划外的文件占着」。
    /// </summary>
    public static HashSet<string> VacatedSources(IEnumerable<RenameEntry> entries)
        => entries
            .Where(entry => !SamePath(entry.SourcePath, entry.TargetPath))
            .Select(entry => Path.GetFullPath(entry.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Worker <c>--rename-assembly</c> 请求：改名、把同级外购件挪进 <c>外购件\</c>，一次做完。
///
/// 没有「删图号」这件事（V4.15）：前缀为空只表示本轮不动图号。V4.9～V4.14 曾把空前缀当成删图号，
/// 用户只是没填前缀就点了写入，整台设备的图号被抹掉——那不是用户要的。
/// </summary>
/// <param name="DrawingPrefix">
/// 图号前缀。**空串合法**，表示这一轮不动图号：文件名里的原图号段原样保留（V4.15）。
/// </param>
public sealed record AssemblyRenameRequest(
    string BatchId,
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    ConversionSourceFormat SourceFormat = ConversionSourceFormat.SolidWorks);
