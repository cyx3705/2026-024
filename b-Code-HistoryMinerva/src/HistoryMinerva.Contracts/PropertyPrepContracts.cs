namespace HistoryMinerva.Contracts;

/// <summary>属性整备改名清单中的一个唯一文件。</summary>
public sealed record RenameEntry(
    string Id,
    string SourcePath,
    string TargetPath,
    string DrawingNumber,
    bool AssignsDrawingNumber,
    IReadOnlyList<string> ParentSourcePaths,
    int Depth);

/// <summary>按所选装配体层级生成的改名计划。纯内存，不碰 CAD。</summary>
public sealed record AssemblyRenamePlan(
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    IReadOnlyList<RenameEntry> Unnumbered,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    public bool CanRename =>
        BlockingIssues.Count == 0
        && Entries.Any(entry => !SamePath(entry.SourcePath, entry.TargetPath));

    public static bool SamePath(string left, string right)
        => Path.IsPathFullyQualified(left)
           && Path.IsPathFullyQualified(right)
           && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Worker <c>--rename-assembly</c> 请求。字段只追加，不插入；
/// <see cref="SourceFormat"/> 与 <see cref="StripBySpace"/> 放在末尾，缺省 SolidWorks / 按图号改名。
/// </summary>
public sealed record AssemblyRenameRequest(
    string BatchId,
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    ConversionSourceFormat SourceFormat = ConversionSourceFormat.SolidWorks,
    bool StripBySpace = false);
