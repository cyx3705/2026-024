using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public sealed record AssemblyPlanIssue(
    ConversionErrorClass ErrorClass,
    string Message);

public sealed record AssemblyConversionPlan(
    string SourceAssemblyPath,
    string SourceDirectory,
    string XtDirectory,
    string SolidWorksDirectory,
    string AssemblyOutputPath,
    IReadOnlyList<ScanCandidate> Parts,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    IReadOnlyList<AssemblyPlanIssue> BlockingIssues,
    IReadOnlyList<string> Warnings,
    // V3.3：拓扑序的装配节点。为空表示退化到 V3.0 的展平行为。
    IReadOnlyList<AssemblyNode>? Nodes = null,
    int MaxDepth = 1,
    // V3.5：全部层的装配关系，按所属 .asm 分派。
    IReadOnlyList<AssemblyRelation>? Relations = null)
{
    public bool CanConvert => BlockingIssues.Count == 0 && Parts.Count > 0;

    /// <summary>本版是否按嵌套生成。为 false 时按 V3.0 展平。</summary>
    public bool IsNested => Nodes is { Count: > 0 };

    public int SubAssemblyCount => Nodes is null ? 0 : Nodes.Count(node => !node.IsRoot);

    /// <summary>本次探查采集到的关系总数。为 0 时界面上的"重建装配关系"应当禁用。</summary>
    public int RelationCount => Relations?.Count ?? 0;
}

public static class AssemblyPlanner
{
    public static AssemblyConversionPlan Create(AssemblyProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!Path.IsPathFullyQualified(probe.SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(probe.SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension))
        {
            throw new InvalidDataException("装配探查结果中的源路径不是绝对 .asm 路径。");
        }

        var sourceAssemblyPath = Path.GetFullPath(probe.SourceAssemblyPath);
        var sourceDirectory = Path.GetDirectoryName(sourceAssemblyPath)
            ?? throw new InvalidDataException("无法解析装配体所在目录。");
        var directories = ExternalOutputLayout.Resolve(sourceDirectory);
        var xtDirectory = directories.XtDirectory;
        var swDirectory = directories.SolidWorksDirectory;
        var assemblyOutputPath = ConversionPathLayout.ResolveAssemblyOutputPath(sourceAssemblyPath, swDirectory);
        var issues = new List<AssemblyPlanIssue>();
        var warnings = probe.Warnings.Distinct(StringComparer.Ordinal).ToList();

        if (!File.Exists(sourceAssemblyPath))
            issues.Add(new AssemblyPlanIssue(ConversionErrorClass.InputMissing, $"源装配体不存在：{sourceAssemblyPath}"));
        if (probe.UnresolvedCount > 0)
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.OccurrenceUnresolved,
                $"装配体有 {probe.UnresolvedCount} 个未解析引用，转换已阻止。"));

        CheckDirectoryNameConflict(xtDirectory, issues);
        CheckDirectoryNameConflict(swDirectory, issues);

        var supportedParts = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Where(path => ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidEdgePartExtension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var unsupported = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => item.SourcePath)
            .Where(path => !ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidEdgePartExtension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in unsupported)
            warnings.Add($"跳过 V3.0 不支持的引用：{path}");

        foreach (var group in supportedParts.GroupBy(
                     path => Path.GetFileNameWithoutExtension(path),
                     StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length < 2)
                continue;
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.DuplicateOutputName,
                $"同名不同路径零件会映射到同一输出“{group.Key}”：{string.Join("；", paths)}"));
        }

        var parts = supportedParts.Select(path =>
        {
            var paths = ConversionPathLayout.ResolvePartPaths(path, xtDirectory, swDirectory, sourceDirectory);
            var exists = File.Exists(paths.XtPath) || File.Exists(paths.SolidWorksPath)
                || File.Exists(paths.LegacyXtPath) || File.Exists(paths.LegacySolidWorksPath);
            var reusableXt = File.Exists(paths.XtPath)
                ? paths.XtPath
                : File.Exists(paths.LegacyXtPath) ? paths.LegacyXtPath : paths.XtPath;
            var reusableSw = File.Exists(paths.SolidWorksPath)
                ? paths.SolidWorksPath
                : File.Exists(paths.LegacySolidWorksPath) ? paths.LegacySolidWorksPath : paths.SolidWorksPath;
            return new ScanCandidate(path, reusableXt, reusableSw, exists);
        }).ToArray();

        if (parts.Any(item => item.HasExistingOutput))
            warnings.Add("检测到已有零件产物；转换时会校验时间与格式，安全复用有效的分层或旧平铺 XT/SLDPRT，不覆盖用户文件。");
        if (parts.Length == 0)
            issues.Add(new AssemblyPlanIssue(ConversionErrorClass.InputInvalid, "装配体中没有可转换的 .par 零件。"));

        if (probe.SuppressedCount > 0)
            warnings.Add($"将跳过 {probe.SuppressedCount} 个抑制实例。");
        var hiddenCount = probe.Occurrences.Count(item => item.IsHidden && !item.IsSuppressed && !item.IsSubAssembly);
        if (hiddenCount > 0)
            warnings.Add($"{hiddenCount} 个隐藏实例仍会插入，并保持普通可见组件。");

        var graph = BuildGraph(probe, sourceAssemblyPath, swDirectory, issues);
        VerifyTransforms(probe, issues);
        CheckAssemblyNameConflicts(graph, issues);
        ReportExistingAssemblyOutputs(graph, assemblyOutputPath, warnings);
        warnings.Add(graph is { Nodes.Count: > 0 }
            ? $"按源装配的层级生成嵌套装配体：{graph.Nodes.Count} 个装配文件、最大 {graph.MaxDepth} 层；"
                + "全部组件固定，不含配合。"
            : "本次未取到逐文档读数，将退回 V3.0 的展平方式组装。");

        return new AssemblyConversionPlan(
            sourceAssemblyPath,
            sourceDirectory,
            xtDirectory,
            swDirectory,
            assemblyOutputPath,
            parts,
            probe.Occurrences,
            issues,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            graph?.Nodes,
            graph?.MaxDepth ?? 1,
            (probe.Documents ?? [])
                .SelectMany(document => document.Relations ?? [])
                .ToArray());
    }

    /// <summary>V3.3：把逐文档读数组装成拓扑序的装配节点；读数缺失时返回 null，由调用方退回展平。</summary>
    private static AssemblyGraph? BuildGraph(
        AssemblyProbeResult probe,
        string sourceAssemblyPath,
        string swDirectory,
        ICollection<AssemblyPlanIssue> issues)
    {
        if (probe.Documents is not { Count: > 0 })
            return null;

        var graph = AssemblyGraphBuilder.Build(
            sourceAssemblyPath,
            probe.Documents,
            path => ConversionPathLayout.ResolveAssemblyOutputPath(path, swDirectory));

        foreach (var cycle in graph.Cycles)
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.SubAssemblyCycleDetected,
                $"装配引用成环，拒绝递归：{cycle}"));
        foreach (var missing in graph.MissingDocuments)
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.OccurrenceUnresolved,
                $"子装配缺少可读的一级读数：{missing}"));
        return graph;
    }

    /// <summary>
    /// V3.3 §3.3.1：用世界矩阵与局部矩阵互相印证。参考系用错不会抛异常，只会静默错位，
    /// 所以必须在触碰 CAD 之前把它拦住。
    /// </summary>
    private static void VerifyTransforms(AssemblyProbeResult probe, ICollection<AssemblyPlanIssue> issues)
    {
        var verification = AssemblyTransformVerifier.Verify(probe);
        if (verification.CheckedCount == 0 || verification.IsConsistent)
            return;
        var detail = verification.Mismatches.Count > 0
            ? string.Join("；", verification.Mismatches
                .Take(5)
                .Select(item => $"{item.OccurrenceId} 偏差 {item.MaxDeviation:G6}"))
            : string.Join("；", verification.Unmatched.Take(5));
        issues.Add(new AssemblyPlanIssue(
            ConversionErrorClass.ComponentTransformFailed,
            $"局部矩阵与世界矩阵不一致（{verification.Mismatches.Count} 处超差、"
                + $"{verification.Unmatched.Count} 处未匹配）：{detail}"));
    }

    /// <summary>§3.6：装配之间的同名冲突。两个同名不同路径的 .asm 会写到同一个 .SLDASM。</summary>
    private static void CheckAssemblyNameConflicts(AssemblyGraph? graph, ICollection<AssemblyPlanIssue> issues)
    {
        if (graph is null)
            return;
        foreach (var group in graph.Nodes.GroupBy(
                     node => Path.GetFileNameWithoutExtension(node.SourceAssemblyPath),
                     StringComparer.OrdinalIgnoreCase))
        {
            var paths = group
                .Select(node => node.SourceAssemblyPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length < 2)
                continue;
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.DuplicateOutputName,
                $"同名不同路径装配体会映射到同一输出“{group.Key}.SLDASM”：{string.Join("；", paths)}"));
        }
    }

    /// <summary>
    /// 已有装配产物不再是硬阻断。与 V3.0.2 的零件口径一致：转换时校验时间，
    /// 安全的直接复用，过期或为空的报错让用户自己移走，从不覆盖用户文件。
    /// </summary>
    private static void ReportExistingAssemblyOutputs(
        AssemblyGraph? graph,
        string assemblyOutputPath,
        ICollection<string> warnings)
    {
        var outputs = graph is { Nodes.Count: > 0 }
            ? graph.Nodes.Select(node => node.OutputPath).ToArray()
            : [assemblyOutputPath];
        var existing = outputs.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return;
        warnings.Add($"检测到 {existing.Length} 个已有装配产物；转换时会核验它是否比全部依赖都新，"
            + "过期或为空会报错而不是覆盖。");
    }

    private static void CheckDirectoryNameConflict(string path, ICollection<AssemblyPlanIssue> issues)
    {
        if (!File.Exists(path))
            return;
        issues.Add(new AssemblyPlanIssue(
            ConversionErrorClass.OutputNotWritable,
            $"输出目录被同名文件占用：{path}"));
    }
}
