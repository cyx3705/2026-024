using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// 把一个 SolidWorks 组件的面收集成 <see cref="MateCandidate"/>。
///
/// 两条全部由 <c>tools/MateMatchProbe</c> 实测确定、不能凭直觉写反的规矩：
///
/// 1. **曲面参数在零件坐标系**，必须乘组件的总变换才能和关系几何对上。
///    按装配系解释时实测只有 24/53 命中，按零件系是 53/53。
/// 2. **子装配组件自己没有实体**（<c>GetBodies3</c> 返回空），实体挂在它的子组件上。
///    关系可以直接引用一个子装配实例，所以必须递归到后代。漏了这一步，
///    引用子装配的关系恒不命中——实测正是这样丢了 5 条。
/// </summary>
/// <remarks>
/// 本类**不做 COM final 释放**。<c>Marshal.FinalReleaseComObject</c> 会把 RCW 直接清零，
/// 而 .NET 对同一个 COM 指针复用同一个 RCW——释放掉 body / surface 之后，
/// SolidWorks 下次交还同一对象时拿到的就是已失效的 RCW，报
/// "COM object that has been separated from its underlying RCW"。实测踩过一次。
/// 这些对象的生命周期由 <see cref="SolidWorksNestedAssemblyBuilder"/> 在节点结束时统一收尾。
/// </remarks>
internal static class MateCandidateCollector
{
    public static IReadOnlyList<MateCandidate> Collect(SolidWorksInteropBridge interop, object component)
    {
        var candidates = new List<MateCandidate>();
        Walk(interop, component, candidates, "c", depth: 0);
        return candidates;
    }

    private const int MaxDepth = 16;

    private static void Walk(
        SolidWorksInteropBridge interop,
        object component,
        List<MateCandidate> candidates,
        string prefix,
        int depth)
    {
        if (depth >= MaxDepth)
            return;

        CollectOwnFaces(interop, component, candidates, prefix);

        var children = interop.GetComponentChildren(component);
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            Walk(interop, child, candidates, prefix + "." + (index + 1).ToString(), depth + 1);
        }
    }

    private static void CollectOwnFaces(
        SolidWorksInteropBridge interop,
        object component,
        List<MateCandidate> candidates,
        string prefix)
    {
        // 组件的总变换是相对当前顶层文档的，正好是关系几何所在的参考系。
        var transform = interop.GetComponentTransform(component);
        if (transform.Length != 16)
            return;

        var bodies = interop.GetComponentBodies(component);
        for (var bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
        {
            var body = bodies[bodyIndex];
            {
                var faces = interop.GetBodyFaces(body);
                for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
                {
                    var face = faces[faceIndex];
                    try
                    {
                        var candidate = ToCandidate(
                            interop, face, transform, $"{prefix}b{bodyIndex + 1}f{faceIndex + 1}");
                        if (candidate is not null)
                            candidates.Add(candidate);
                    }
                    catch
                    {
                        // 读不出的面直接跳过：候选少一个最多导致定位失败并如实报告，
                        // 绝不会造成错误配合。
                    }
                }
            }
        }
    }

    private static MateCandidate? ToCandidate(
        SolidWorksInteropBridge interop,
        object face,
        double[] transform,
        string key)
    {
        {
            var surface = interop.GetFaceSurface(face);
            if (surface is null)
                return null;

            double[]? point = null;
            double[]? direction = null;
            var kind = 0;

            if (interop.SurfaceIsPlane(surface)
                && interop.GetSurfaceParameters(surface, "PlaneParams") is { Length: >= 6 } plane)
            {
                // PlaneParams：前三个是法向，后三个是根点。
                kind = MateGeometryMatcher.GeometryPlane;
                direction = [plane[0], plane[1], plane[2]];
                point = [plane[3], plane[4], plane[5]];
            }
            else if (interop.SurfaceIsCylinder(surface)
                && interop.GetSurfaceParameters(surface, "CylinderParams") is { Length: >= 6 } cylinder)
            {
                // CylinderParams：前三个是原点，接着三个是轴向，最后是半径。
                kind = MateGeometryMatcher.GeometryAxis;
                point = [cylinder[0], cylinder[1], cylinder[2]];
                direction = [cylinder[3], cylinder[4], cylinder[5]];
            }
            else if (interop.SurfaceIsCone(surface)
                && interop.GetSurfaceParameters(surface, "ConeParams") is { Length: >= 6 } cone)
            {
                kind = MateGeometryMatcher.GeometryAxis;
                point = [cone[0], cone[1], cone[2]];
                direction = [cone[3], cone[4], cone[5]];
            }

            if (point is null || direction is null)
                return null;

            // 面对象要留着给 AddMate5 选中，不能在这里释放。
            return new MateCandidate(
                key,
                kind,
                TransformPoint(point, transform),
                TransformDirection(direction, transform),
                face);
        }
    }

    /// <summary>SolidWorks 变换布局：旋转 0..8（行主序），平移 9..11，缩放 12。</summary>
    private static double[] TransformPoint(IReadOnlyList<double> point, IReadOnlyList<double> transform) =>
    [
        (point[0] * transform[0]) + (point[1] * transform[3]) + (point[2] * transform[6]) + transform[9],
        (point[0] * transform[1]) + (point[1] * transform[4]) + (point[2] * transform[7]) + transform[10],
        (point[0] * transform[2]) + (point[1] * transform[5]) + (point[2] * transform[8]) + transform[11],
    ];

    private static double[] TransformDirection(IReadOnlyList<double> direction, IReadOnlyList<double> transform) =>
    [
        (direction[0] * transform[0]) + (direction[1] * transform[3]) + (direction[2] * transform[6]),
        (direction[0] * transform[1]) + (direction[1] * transform[4]) + (direction[2] * transform[7]),
        (direction[0] * transform[2]) + (direction[1] * transform[5]) + (direction[2] * transform[8]),
    ];
}
