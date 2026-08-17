using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed record TransformReconcileResult(
    AssemblyProbeResult Probe,
    IReadOnlyList<string> AdjustedOccurrenceIds,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// SolidWorks 柔性子装配：单独打开子装配读到的局部矩阵，往往是默认行程；
/// 总装 <c>GetComponents(false)</c> 给出的世界矩阵才是当前在位姿态。
/// 本类用世界矩阵反算在位局部矩阵，供嵌套生成使用，而不是把柔性差异当成参考系错误。
/// 同一子装配在总装里出现两种在位姿态时无法写入同一个输出文件，仍报冲突。
/// </summary>
public static class AssemblyTransformReconciler
{
    public static TransformReconcileResult Reconcile(AssemblyProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Documents is not { Count: > 0 })
            return new TransformReconcileResult(probe, [], []);

        var working = new Dictionary<string, AssemblyDocumentReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in probe.Documents)
            working.TryAdd(Path.GetFullPath(reading.SourceAssemblyPath), reading);

        var world = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var occurrence in probe.Occurrences)
            world[occurrence.OccurrenceId] = occurrence.WorldTransform;

        var assigned = new Dictionary<string, Dictionary<string, double[]>>(StringComparer.OrdinalIgnoreCase);
        var firstInstance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var adjusted = new List<string>();
        var conflicts = new List<string>();

        Walk(
            Path.GetFullPath(probe.SourceAssemblyPath),
            parentId: null,
            AssemblyTransformVerifier.Identity(),
            depth: 0,
            working,
            world,
            assigned,
            firstInstance,
            [],
            adjusted,
            conflicts);

        var documents = probe.Documents
            .Select(reading => working.TryGetValue(Path.GetFullPath(reading.SourceAssemblyPath), out var updated)
                ? updated
                : reading)
            .ToArray();
        return new TransformReconcileResult(
            probe with { Documents = documents },
            adjusted,
            conflicts);
    }

    private static void Walk(
        string assemblyPath,
        string? parentId,
        double[] parentWorld,
        int depth,
        Dictionary<string, AssemblyDocumentReading> working,
        Dictionary<string, double[]> world,
        Dictionary<string, Dictionary<string, double[]>> assigned,
        Dictionary<string, string> firstInstance,
        List<string> stack,
        List<string> adjusted,
        List<string> conflicts)
    {
        if (depth >= 32 || stack.Contains(assemblyPath, StringComparer.OrdinalIgnoreCase))
            return;
        if (!working.TryGetValue(assemblyPath, out var reading))
            return;

        stack.Add(assemblyPath);
        try
        {
            var inverseOk = AssemblyTransformVerifier.TryInverse(parentWorld, out var inverseParent);
            var isFirst = !assigned.ContainsKey(assemblyPath);
            var assignedChildren = isFirst ? new Dictionary<string, double[]>(StringComparer.Ordinal) : assigned[assemblyPath];
            var instanceId = parentId ?? "(顶层)";
            var newChildren = new List<AssemblyChild>(reading.Children.Count);
            var documentAdjusted = false;

            foreach (var child in reading.Children)
            {
                var id = parentId is null ? child.Name : parentId + "/" + child.Name;
                var nextLocal = child.LocalTransform;
                if (inverseOk && world.TryGetValue(id, out var expected) && expected.Length == 16)
                {
                    var desired = AssemblyTransformVerifier.Multiply(expected, inverseParent);
                    if (isFirst)
                    {
                        if (AssemblyTransformVerifier.MaxAbsDifference(child.LocalTransform, desired)
                            > AssemblyTransformVerifier.Tolerance)
                        {
                            nextLocal = desired;
                            documentAdjusted = true;
                            adjusted.Add(id);
                        }
                    }
                    else if (assignedChildren.TryGetValue(child.Name, out var previous)
                             && AssemblyTransformVerifier.MaxAbsDifference(previous, desired)
                             > AssemblyTransformVerifier.Tolerance)
                    {
                        var first = firstInstance.GetValueOrDefault(assemblyPath, "(先前实例)");
                        conflicts.Add(
                            $"{Path.GetFileName(assemblyPath)} 的 {child.Name} 在实例 {first} 与 {instanceId} 中姿态不同");
                    }
                }

                if (isFirst)
                    assignedChildren[child.Name] = nextLocal;
                newChildren.Add(ReferenceEquals(nextLocal, child.LocalTransform)
                    ? child
                    : child with { LocalTransform = nextLocal });
            }

            if (isFirst)
            {
                assigned[assemblyPath] = assignedChildren;
                firstInstance[assemblyPath] = instanceId;
                working[assemblyPath] = reading with
                {
                    Children = newChildren,
                    Relations = documentAdjusted ? [] : reading.Relations,
                };
            }

            var children = isFirst ? newChildren : reading.Children;
            foreach (var child in children)
            {
                if (!child.IsSubAssembly)
                    continue;
                var id = parentId is null ? child.Name : parentId + "/" + child.Name;
                var nextParent = world.TryGetValue(id, out var expected) && expected.Length == 16
                    ? expected
                    : AssemblyTransformVerifier.Multiply(child.LocalTransform, parentWorld);
                Walk(
                    Path.GetFullPath(child.SourcePath),
                    id,
                    nextParent,
                    depth + 1,
                    working,
                    world,
                    assigned,
                    firstInstance,
                    stack,
                    adjusted,
                    conflicts);
            }
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }
}
