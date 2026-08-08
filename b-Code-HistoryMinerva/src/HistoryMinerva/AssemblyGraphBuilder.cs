using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

/// <summary>装配图的构建结果。<see cref="Cycles"/> 非空时不可转换。</summary>
public sealed record AssemblyGraph(
    IReadOnlyList<AssemblyNode> Nodes,
    int MaxDepth,
    IReadOnlyList<string> Cycles,
    IReadOnlyList<string> MissingDocuments)
{
    public bool IsValid => Cycles.Count == 0 && MissingDocuments.Count == 0 && Nodes.Count > 0;
}

/// <summary>
/// 把"每个装配文档的一级读数"组装成拓扑序的装配节点列表。
///
/// 纯数据变换，不碰 CAD，全部逻辑由 <c>SE2SW.Smoke</c> 离线覆盖。
///
/// 遵守 V3.3 的每层同构原则：本类只按"某文档有哪些直接子项"推导，
/// 从不为某一层去查它的父级或祖先——需要祖先信息才能算出的结果，
/// 一定是把世界矩阵当成局部矩阵在用。
/// </summary>
public static class AssemblyGraphBuilder
{
    private const int Unvisited = 0;
    private const int OnStack = 1;
    private const int Done = 2;

    /// <param name="rootAssemblyPath">顶层 <c>.asm</c> 绝对路径。</param>
    /// <param name="readings">逐文档的一级读数，含顶层。按绝对路径去重，重复项以第一条为准。</param>
    /// <param name="outputPathFor">源 <c>.asm</c> 绝对路径 → 目标 <c>.SLDASM</c> 绝对路径。</param>
    public static AssemblyGraph Build(
        string rootAssemblyPath,
        IReadOnlyList<AssemblyDocumentReading> readings,
        Func<string, string> outputPathFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAssemblyPath);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(outputPathFor);

        var root = Normalize(rootAssemblyPath);
        var documents = new Dictionary<string, AssemblyDocumentReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in readings)
            documents.TryAdd(Normalize(reading.SourceAssemblyPath), reading);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var ordered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cycles = new List<string>();
        var missing = new List<string>();

        if (!documents.ContainsKey(root))
        {
            missing.Add(root);
            return new AssemblyGraph([], 1, cycles, missing);
        }

        Visit(root, 1, [], documents, state, depths, order, ordered, cycles, missing);

        if (cycles.Count > 0 || missing.Count > 0)
            return new AssemblyGraph([], depths.Count == 0 ? 1 : depths.Values.Max(), cycles, missing);

        // order 是后序：子级一定排在父级之前，顶层在最后。
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<AssemblyNode>(order.Count);
        foreach (var path in order)
        {
            var reading = documents[path];
            var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in reading.Children)
            {
                var childPath = Normalize(child.SourcePath);
                closure.Add(childPath);
                // 子级已经在前面处理过，它的闭包可以直接并进来。
                if (dependencies.TryGetValue(childPath, out var nested))
                    closure.UnionWith(nested);
            }

            dependencies[path] = closure;
            nodes.Add(new AssemblyNode(
                path,
                outputPathFor(path),
                IsRoot: string.Equals(path, root, StringComparison.OrdinalIgnoreCase),
                Depth: depths[path],
                reading.Children,
                closure.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return new AssemblyGraph(nodes, depths.Values.Max(), cycles, missing);
    }

    private static void Visit(
        string path,
        int depth,
        List<string> stack,
        Dictionary<string, AssemblyDocumentReading> documents,
        Dictionary<string, int> state,
        Dictionary<string, int> depths,
        List<string> order,
        HashSet<string> ordered,
        List<string> cycles,
        List<string> missing)
    {
        if (state.GetValueOrDefault(path) == OnStack)
        {
            // 成环。必须在这里截断，不能靠递归深度兜底——装配引用成环时深度是无界的。
            var start = stack.FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            var loop = stack.Skip(start < 0 ? 0 : start).Append(path).Select(Path.GetFileName);
            cycles.Add(string.Join(" → ", loop));
            return;
        }

        var known = depths.GetValueOrDefault(path, 0);
        if (state.GetValueOrDefault(path) == Done && depth <= known)
            return;

        depths[path] = Math.Max(known, depth);
        state[path] = OnStack;
        stack.Add(path);
        try
        {
            foreach (var child in documents[path].Children)
            {
                if (!child.IsSubAssembly)
                    continue;
                var childPath = Normalize(child.SourcePath);
                if (!documents.ContainsKey(childPath))
                {
                    if (!missing.Contains(childPath, StringComparer.OrdinalIgnoreCase))
                        missing.Add(childPath);
                    continue;
                }

                Visit(childPath, depth + 1, stack, documents, state, depths, order, ordered, cycles, missing);
                if (cycles.Count > 0)
                    return;
            }
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
            state[path] = Done;
        }

        // 后序追加，且只追加一次：同一子装配被多处引用时仍然只生成一个 .SLDASM。
        if (ordered.Add(path))
            order.Add(path);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
