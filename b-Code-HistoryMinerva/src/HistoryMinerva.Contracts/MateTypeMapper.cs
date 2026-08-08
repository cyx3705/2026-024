
namespace SE2SW.Contracts;

/// <summary>swMateType_e 的子集，只列本版会用到的。</summary>
public enum SolidWorksMateType
{
    Coincident = 0,
    Concentric = 1,
    Distance = 5,
}

/// <summary>swMateAlign_e。</summary>
public enum SolidWorksMateAlign
{
    Aligned = 0,
    AntiAligned = 1,
    /// <summary>取离当前姿态最近的解。见 <see cref="MateTypeMapper"/> 的对齐策略说明。</summary>
    Closest = 2,
}

public enum MatePlanKind
{
    /// <summary>翻译成一条 SolidWorks 配合。</summary>
    Mate,
    /// <summary>不是配合，而是把某个组件固定（GroundRelation3d）。</summary>
    Fix,
    /// <summary>关系被抑制，在 SE 里本来就没生效。</summary>
    SkipSuppressed,
    /// <summary>本版不翻译这个类型。</summary>
    Unsupported,
}

public sealed record MatePlan(
    MatePlanKind Kind,
    SolidWorksMateType MateType = SolidWorksMateType.Coincident,
    SolidWorksMateAlign Align = SolidWorksMateAlign.Aligned,
    double Distance = 0,
    string? Reason = null);

/// <summary>
/// V3.5 §3.4：SE 关系 → SW 配合的映射表。
///
/// 只认 19 号报告实测到的三种接口名。遇到没实测过的类型一律 <see cref="MatePlanKind.Unsupported"/>
/// 并把接口名带进报告——据此决定下一版补哪个，而不是现在凭猜测写一行映射。
///
/// 纯逻辑，不碰 CAD。
/// </summary>
public static class MateTypeMapper
{
    public const string Ground = "GroundRelation3d";
    public const string Axial = "AxialRelation3d";
    public const string Planar = "PlanarRelation3d";

    /// <summary>偏移量小于这个值就当作重合，而不是零距离的距离配合。</summary>
    public const double OffsetEpsilon = 1e-9;

    public static MatePlan Map(AssemblyRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (relation.IsSuppressed)
            return new MatePlan(MatePlanKind.SkipSuppressed, Reason: "关系在 Solid Edge 中已被抑制");

        return relation.InterfaceName switch
        {
            Ground => new MatePlan(MatePlanKind.Fix, Reason: "接地关系，固定该组件"),
            Planar => MapPlanar(relation),
            Axial => MapAxial(relation),
            _ => new MatePlan(
                MatePlanKind.Unsupported,
                Reason: $"本版未实测过的关系类型：{relation.InterfaceName}"),
        };
    }

    // ---- 对齐策略（2026-08-03 真机定案）----
    //
    // 一律 swAlignCLOSEST。理由：配合建立时每个组件都已经**精确位于源装配的位置上**
    // （V3.3 保证，偏差 < 1e-6 m），这条关系在当前摆放下本来就成立——
    // "离当前姿态最近的解"就是 Solid Edge 里的那个解，即：不动。
    //
    // 反面教训：按 NormalsAligned 显式传 Aligned/AntiAligned 时，求解器会按指定方向
    // 翻转组件来满足对齐——顶层 43 条里大量出现 180° 翻转（旋转元素偏差恒为 2），
    // 全部被漂移校验回滚。NormalsAligned 仍照实采集进契约，但应用侧不再消费它。

    private static MatePlan MapPlanar(AssemblyRelation relation)
        => Math.Abs(relation.Offset) <= OffsetEpsilon
            ? new MatePlan(MatePlanKind.Mate, SolidWorksMateType.Coincident, SolidWorksMateAlign.Closest)
            : new MatePlan(MatePlanKind.Mate, SolidWorksMateType.Distance, SolidWorksMateAlign.Closest, Math.Abs(relation.Offset));

    private static MatePlan MapAxial(AssemblyRelation relation)
    {
        // 条件可读（19 号报告 §3）：ParallelOffset == false 时 Offset 根本读不出来，
        // 采集侧会留默认 0。这里绝不能反过来"看 Offset 是否为 0"来推断类型——
        // 那会把一条正常的同轴关系误判成零距离配合。开关是唯一依据。
        if (!relation.ParallelOffset)
            return new MatePlan(MatePlanKind.Mate, SolidWorksMateType.Concentric, SolidWorksMateAlign.Closest);
        return new MatePlan(
            MatePlanKind.Mate,
            SolidWorksMateType.Distance,
            SolidWorksMateAlign.Closest,
            Math.Abs(relation.Offset));
    }

    /// <summary>
    /// 条件可读的守门人：告诉采集侧现在能不能读这个成员。
    /// 读不出来是正常状态，不是故障——19 号报告 §3 已实测。
    /// </summary>
    public static bool CanReadAxialOffset(bool parallelOffset) => parallelOffset;

    /// <summary>同上：<c>RangedOffset == false</c> 时读 RangeLow/RangeHigh 抛 0x8000FFFF。</summary>
    public static bool CanReadRange(bool rangedOffset) => rangedOffset;
}
