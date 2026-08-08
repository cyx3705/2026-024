using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public sealed record TransformDeviation(string OccurrenceId, double MaxDeviation);

public sealed record TransformVerification(
    int CheckedCount,
    double MaxDeviation,
    IReadOnlyList<TransformDeviation> Mismatches,
    IReadOnlyList<string> Unmatched)
{
    public bool IsConsistent => Mismatches.Count == 0 && Unmatched.Count == 0;
}

/// <summary>
/// 局部矩阵的自校验：把逐文档读数按层复合，与 Solid Edge 直接给出的世界矩阵逐元素比对。
///
/// V3.3 的局部矩阵是本版唯一的静默错误源——取错参考系不抛异常，只表现为整层错位或姿态错乱。
/// 探查结果里同时有"世界矩阵"和"局部矩阵"两套独立数据，本类用它们互相印证，
/// 不需要 CAD、不需要人眼看图，任何一层的参考系用错都会当场超差。
/// </summary>
public static class AssemblyTransformVerifier
{
    /// <summary>与 V3.0 的姿态精度判据同阈值。</summary>
    public const double Tolerance = 1e-9;

    private const int MaxDepth = 32;

    public static TransformVerification Verify(AssemblyProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var mismatches = new List<TransformDeviation>();
        var unmatched = new List<string>();
        if (probe.Documents is not { Count: > 0 })
            return new TransformVerification(0, 0, mismatches, unmatched);

        var documents = new Dictionary<string, AssemblyDocumentReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in probe.Documents)
            documents.TryAdd(Path.GetFullPath(reading.SourceAssemblyPath), reading);

        var world = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var occurrence in probe.Occurrences)
            world[occurrence.OccurrenceId] = occurrence.WorldTransform;

        var checkedCount = 0;
        var maxDeviation = 0d;
        Walk(
            Path.GetFullPath(probe.SourceAssemblyPath),
            parentId: null,
            Identity(),
            depth: 0,
            documents,
            world,
            [],
            ref checkedCount,
            ref maxDeviation,
            mismatches,
            unmatched);
        return new TransformVerification(checkedCount, maxDeviation, mismatches, unmatched);
    }

    private static void Walk(
        string assemblyPath,
        string? parentId,
        double[] parentWorld,
        int depth,
        Dictionary<string, AssemblyDocumentReading> documents,
        Dictionary<string, double[]> world,
        List<string> stack,
        ref int checkedCount,
        ref double maxDeviation,
        List<TransformDeviation> mismatches,
        List<string> unmatched)
    {
        if (depth >= MaxDepth || stack.Contains(assemblyPath, StringComparer.OrdinalIgnoreCase))
            return;
        if (!documents.TryGetValue(assemblyPath, out var reading))
            return;

        stack.Add(assemblyPath);
        try
        {
            foreach (var child in reading.Children)
            {
                var id = parentId is null ? child.Name : parentId + "/" + child.Name;
                var composed = Multiply(child.LocalTransform, parentWorld);
                if (world.TryGetValue(id, out var expected) && expected.Length == 16)
                {
                    checkedCount++;
                    var deviation = 0d;
                    for (var index = 0; index < 16; index++)
                        deviation = Math.Max(deviation, Math.Abs(composed[index] - expected[index]));
                    maxDeviation = Math.Max(maxDeviation, deviation);
                    if (deviation > Tolerance)
                        mismatches.Add(new TransformDeviation(id, deviation));
                }
                else
                {
                    unmatched.Add(id);
                }

                if (child.IsSubAssembly)
                {
                    Walk(
                        Path.GetFullPath(child.SourcePath),
                        id,
                        composed,
                        depth + 1,
                        documents,
                        world,
                        stack,
                        ref checkedCount,
                        ref maxDeviation,
                        mismatches,
                        unmatched);
                }
            }
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// 行主序 4×4 相乘，行向量约定（平移位于 12..14，与 Solid Edge 和 V3.0 的口径一致）：
    /// 点先经子级局部变换，再经父级世界变换，故 <c>世界 = 局部 × 父世界</c>。
    ///
    /// 顺序写反或只把平移相加，在无旋转的样件上都能"通过"——所以验收样件必须带旋转。
    /// </summary>
    public static double[] Multiply(IReadOnlyList<double> child, IReadOnlyList<double> parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        if (child.Count != 16 || parent.Count != 16)
            throw new InvalidDataException("变换矩阵必须是 16 元素。");

        var result = new double[16];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var sum = 0d;
                for (var k = 0; k < 4; k++)
                    sum += child[row * 4 + k] * parent[k * 4 + column];
                result[row * 4 + column] = sum;
            }
        }

        return result;
    }

    public static double[] Identity() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];
}
