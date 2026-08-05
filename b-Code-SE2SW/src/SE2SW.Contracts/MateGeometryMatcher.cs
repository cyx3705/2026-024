using System.IO;

namespace SE2SW.Contracts;

/// <summary>SolidWorks 侧一个候选面的几何签名。抽出来是为了让匹配逻辑能脱离 CAD 测试。</summary>
/// <param name="Key">回到 SW 里定位该面用的标识，匹配逻辑不解释它。</param>
/// <param name="GeometryType">1 = 平面；2 = 轴（圆柱/圆锥）。与 <see cref="RelationGeometry"/> 同口径。</param>
/// <param name="Point">平面上一点，或轴线上一点。米。</param>
/// <param name="Direction">平面法向，或轴线方向。</param>
/// <param name="Entity">
/// SolidWorks 侧的实体对象，供 Worker 选中它建配合。匹配逻辑完全不解释这个字段，
/// 因此离线测试可以留空——几何判定只看 <paramref name="Point"/> 与 <paramref name="Direction"/>。
/// </param>
public sealed record MateCandidate(
    string Key,
    int GeometryType,
    double[] Point,
    double[] Direction,
    object? Entity = null);

public enum MateMatchStatus
{
    Matched,
    Unmatched,
    Ambiguous,
}

public sealed record MateMatch(
    MateMatchStatus Status,
    MateCandidate? Candidate,
    IReadOnlyList<string> CandidateKeys)
{
    public ConversionErrorClass? ErrorClass => Status switch
    {
        MateMatchStatus.Unmatched => ConversionErrorClass.MateEntityUnmatched,
        MateMatchStatus.Ambiguous => ConversionErrorClass.MateEntityAmbiguous,
        _ => null,
    };
}

/// <summary>
/// V3.5 §3.3：把关系给出的（点 + 方向）匹配到 SolidWorks 组件的某个面。
///
/// 不用 <c>SelectByID2</c> 的坐标点选：那条路选错面不会报错，只会生成一条语义错误的配合。
/// 这里枚举候选并要求**唯一命中**——0 个和 &gt;1 个都当场判定为定位失败，绝不猜。
///
/// 纯数学，不碰 CAD，由 <c>SE2SW.Smoke</c> 完整覆盖。
/// </summary>
public static class MateGeometryMatcher
{
    /// <summary>位置容差，与 V3.0 起的位置判据同口径。</summary>
    public const double PositionTolerance = 1e-6;

    /// <summary>方向容差：两个单位向量叉积模长的上限。</summary>
    public const double DirectionTolerance = 1e-9;

    public const int GeometryPlane = 1;
    public const int GeometryAxis = 2;

    public static MateMatch Match(RelationGeometry geometry, IReadOnlyList<MateCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(candidates);

        var hits = candidates.Where(candidate => IsMatch(geometry, candidate)).ToArray();
        var keys = hits.Select(candidate => candidate.Key).ToArray();
        if (hits.Length == 0)
            return new MateMatch(MateMatchStatus.Unmatched, null, keys);
        if (hits.Length == 1)
            return new MateMatch(MateMatchStatus.Matched, hits[0], keys);

        // 多个候选**不等于**歧义。SolidWorks 常把一个几何面切成多块拓扑面：
        // 一个通孔切成 2~3 个圆柱面、一个大平面切成多块共面面。实测 106 侧里有 43 侧如此。
        // 这些候选按构造就是同一个平面或同一条轴（筛选条件就是"法向平行 + 点在其上"），
        // 对配合而言给出完全相同的约束，选哪一个都一样。
        //
        // 但不假设推理一定成立：这里显式两两验证等价性，真出现不等价的候选才判歧义。
        return AreAllEquivalent(hits)
            ? new MateMatch(MateMatchStatus.Matched, Choose(geometry, hits), keys)
            : new MateMatch(MateMatchStatus.Ambiguous, null, keys);
    }

    /// <summary>候选之间是否互为同一平面 / 同一轴。两两验证，候选数极小，不做优化。</summary>
    public static bool AreAllEquivalent(IReadOnlyList<MateCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        for (var left = 0; left < candidates.Count; left++)
        {
            for (var right = left + 1; right < candidates.Count; right++)
            {
                if (!AreEquivalent(candidates[left], candidates[right]))
                    return false;
            }
        }

        return true;
    }

    public static bool AreEquivalent(MateCandidate left, MateCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.GeometryType != right.GeometryType)
            return false;
        if (!IsParallel(left.Direction, right.Direction))
            return false;

        return left.GeometryType switch
        {
            // 互相都在对方的平面上 → 同一平面。
            GeometryPlane => IsPointOnPlane(left.Point, right.Point, right.Direction)
                && IsPointOnPlane(right.Point, left.Point, left.Direction),
            // 互相都在对方的轴线上 → 同一条轴。
            GeometryAxis => IsPointOnAxis(left.Point, right.Point, right.Direction)
                && IsPointOnAxis(right.Point, left.Point, left.Direction),
            _ => false,
        };
    }

    /// <summary>
    /// 从等价候选里挑一个。优先法向与关系同向——配合的对齐方向依赖它；
    /// 其次按 Key 取序，保证同样的输入永远得到同样的输出（可复现比"最优"更重要）。
    /// </summary>
    private static MateCandidate Choose(RelationGeometry geometry, IReadOnlyList<MateCandidate> candidates)
        => candidates
            .OrderByDescending(candidate => IsSameDirection(geometry.Direction, candidate.Direction))
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .First();

    public static bool IsSameDirection(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a is not null && b is not null && Dot(a, b) > 0;
    }

    public static bool IsMatch(RelationGeometry geometry, MateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(candidate);
        if (geometry.GeometryType != candidate.GeometryType)
            return false;
        if (!IsParallel(geometry.Direction, candidate.Direction))
            return false;

        return geometry.GeometryType switch
        {
            // 平面：法向平行且关系给出的点落在候选平面上，两者才是同一个平面。
            GeometryPlane => IsPointOnPlane(geometry.Point, candidate.Point, candidate.Direction),
            // 轴：方向平行且关系给出的点落在候选轴线上，两者才是同一条轴。
            GeometryAxis => IsPointOnAxis(geometry.Point, candidate.Point, candidate.Direction),
            _ => false,
        };
    }

    /// <summary>方向平行判定。**同向与反向都算平行**——SE 的两侧法向常是反的，由对齐标志表达朝向。</summary>
    public static bool IsParallel(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a is null || b is null)
            return false;
        var cross = new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        };
        return Length(cross) <= DirectionTolerance;
    }

    public static bool IsPointOnPlane(
        IReadOnlyList<double> point,
        IReadOnlyList<double> planePoint,
        IReadOnlyList<double> planeNormal)
    {
        var normal = Normalize(planeNormal);
        if (normal is null)
            return false;
        var delta = Subtract(point, planePoint);
        return Math.Abs(Dot(delta, normal)) <= PositionTolerance;
    }

    public static bool IsPointOnAxis(
        IReadOnlyList<double> point,
        IReadOnlyList<double> axisPoint,
        IReadOnlyList<double> axisDirection)
    {
        var direction = Normalize(axisDirection);
        if (direction is null)
            return false;
        var delta = Subtract(point, axisPoint);
        // 点到直线的距离 = |delta × 单位方向|
        var cross = new[]
        {
            delta[1] * direction[2] - delta[2] * direction[1],
            delta[2] * direction[0] - delta[0] * direction[2],
            delta[0] * direction[1] - delta[1] * direction[0],
        };
        return Length(cross) <= PositionTolerance;
    }

    private static double[]? Normalize(IReadOnlyList<double> vector)
    {
        if (vector is not { Count: 3 } || vector.Any(value => !double.IsFinite(value)))
            return null;
        var length = Length(vector);
        if (length <= double.Epsilon)
            return null;
        return [vector[0] / length, vector[1] / length, vector[2] / length];
    }

    private static double[] Subtract(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left is not { Count: 3 } || right is not { Count: 3 })
            throw new InvalidDataException("几何点必须是 3 元素。");
        return [left[0] - right[0], left[1] - right[1], left[2] - right[2]];
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
        => (left[0] * right[0]) + (left[1] * right[1]) + (left[2] * right[2]);

    private static double Length(IReadOnlyList<double> vector)
        => Math.Sqrt(Dot(vector, vector));
}
