namespace HistoryMinerva.Contracts;

/// <summary>
/// V4.8：改名（或按空格洗图号）之后，把一份探查结果整体挪到新文件名上。
///
/// **为什么必须有这一步**：改名成功的那一刻，上一次解析出来的每一条路径都指向一个不存在的
/// 文件。V4.8 之前用户只能重新选一次装配体再解析一次——而重新解析要再开一遍 SolidWorks、
/// 再走一遍整棵装配树，只为了换掉一批已经算得出来的文件名。更糟的是源装配体的内容也变了
/// （<c>ReplaceReferencedDocument</c> 会重写父装配），于是第二次写入会被
/// 「源装配体在解析后发生变化」直接挡住，属性一个也写不进去。
///
/// 这里做的事纯粹是改路径：读数、层级、矩阵、装配关系一个字都不动。
/// </summary>
public static class RenameReindex
{
    /// <summary>
    /// 这一轮里**真的**改成了名的文件：源路径 → 新路径。
    ///
    /// 判据是磁盘而不是 Worker 的退出码：部分失败时，改成了的那几个照样要跟上，
    /// 没改成的那几个必须留在原路径上，否则表格会指向一个并不存在的新名字。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Moved(IEnumerable<RenameEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!Path.IsPathFullyQualified(entry.SourcePath)
                || !Path.IsPathFullyQualified(entry.TargetPath)
                || AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
            {
                continue;
            }

            var source = Path.GetFullPath(entry.SourcePath);
            var target = Path.GetFullPath(entry.TargetPath);
            if (File.Exists(target) && !File.Exists(source))
                moved[source] = target;
        }

        return moved;
    }

    /// <summary>
    /// 按 <paramref name="moved"/> 产出一份同形、但路径已经更新的探查结果。
    /// 映射为空时原样返回，调用方不必自己判空。
    /// </summary>
    public static AssemblyProbeResult Remap(
        AssemblyProbeResult probe,
        IReadOnlyDictionary<string, string> moved)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(moved);
        if (moved.Count == 0)
            return probe;

        return new AssemblyProbeResult(
            Map(probe.SourceAssemblyPath, moved),
            [.. probe.Occurrences.Select(item => item with { SourcePath = Map(item.SourcePath, moved) })],
            [.. probe.UniquePartPaths.Select(path => Map(path, moved))],
            probe.SuppressedCount,
            probe.UnresolvedCount,
            probe.OrderedPartCount,
            probe.SynchronousPartCount,
            probe.Warnings,
            probe.Documents is null
                ? null
                : [.. probe.Documents.Select(document => RemapDocument(document, moved))],
            probe.PartProperties is null
                ? null
                : [.. probe.PartProperties.Select(item => item with { SourcePath = Map(item.SourcePath, moved) })]);
    }

    /// <summary>按同一份映射搬一份「源路径 → 值」的记账，键换新路径，值原样带过去。</summary>
    public static Dictionary<string, TValue> RemapKeys<TValue>(
        IReadOnlyDictionary<string, TValue> bySourcePath,
        IReadOnlyDictionary<string, string> moved)
    {
        ArgumentNullException.ThrowIfNull(bySourcePath);
        ArgumentNullException.ThrowIfNull(moved);
        var result = new Dictionary<string, TValue>(bySourcePath.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in bySourcePath)
            result[Map(pair.Key, moved)] = pair.Value;
        return result;
    }

    private static AssemblyDocumentReading RemapDocument(
        AssemblyDocumentReading document,
        IReadOnlyDictionary<string, string> moved)
        => new(
            Map(document.SourceAssemblyPath, moved),
            [.. document.Children.Select(child => child with { SourcePath = Map(child.SourcePath, moved) })],
            document.Warnings,
            document.Relations is null
                ? null
                : [.. document.Relations.Select(relation => relation with
                {
                    SourceAssemblyPath = Map(relation.SourceAssemblyPath, moved),
                })]);

    /// <summary>没改过名的路径原样返回：映射只覆盖真的动过的那几个文件。</summary>
    private static string Map(string path, IReadOnlyDictionary<string, string> moved)
    {
        if (!Path.IsPathFullyQualified(path))
            return path;
        return moved.TryGetValue(Path.GetFullPath(path), out var target) ? target : path;
    }
}
