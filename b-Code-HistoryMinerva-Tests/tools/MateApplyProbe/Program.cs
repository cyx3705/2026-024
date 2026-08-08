using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SE2SW.Contracts;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MateApplyProbe;

/// <summary>
/// V3.5 阻塞问题（26 号报表）的最小复现矩阵。
///
/// 生产路径里 AddMate5 恒报 IncorrectSelections，而选择读回来是干净的。
/// 本探针用**编译期强类型 interop**（排除生产桥接层反射调用的嫌疑），在最小样件
/// （测试装配3.SLDASM，两个零件、三条平面关系）上把各种姿势逐一试出来：
///
///   V1  Select4 + SelectData(Mark=1) + AddMate5      —— 与生产完全同姿势的对照组
///   V2  GetCorresponding 映射到实例上下文再 Select4    —— 怀疑 GetBodies3 的面是零件上下文
///   V3  SelectByID2 按坐标选面 + AddMate5             —— 官方样例的姿势
///   V4  Select4 + AddMate3                            —— 排除 AddMate5 参数语义
///   V5  Select4 + AddMate5 + swAlignCLOSEST           —— 排除对齐取值
///
/// 找到能建的姿势后，用它把该层全部关系建完、强制重建、量位移漂移——
/// 顺带回答 26 号报表 §4（0 条配合却漂移 0.34m）里"配合参与后是否稳定"。
/// 最后单独做一组"浮动→固定"隔离实验，直接测 §4 的嫌疑环节。
///
/// 只读：所有改动只在内存里，CloseDoc 不保存，磁盘零改动。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? probeResult = null;
        string? swDirectory = null;
        string target = "测试装配3";
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--probe-result": probeResult = Path.GetFullPath(args[index + 1]); break;
                case "--sw-dir": swDirectory = Path.GetFullPath(args[index + 1]); break;
                case "--target": target = args[index + 1]; break;
            }
        }

        if (probeResult is null || swDirectory is null)
        {
            Console.Error.WriteLine("用法：MateApplyProbe --probe-result <探查结果.json> --sw-dir <SW目录> [--target 测试装配3]");
            return 2;
        }

        var report = new ApplyReport();
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var probe = JsonSerializer.Deserialize<AssemblyProbeResult>(File.ReadAllText(probeResult), JsonOptions)
                ?? throw new InvalidDataException("装配探查结果为空。");
            var document = (probe.Documents ?? []).FirstOrDefault(item =>
                    string.Equals(Path.GetFileNameWithoutExtension(item.SourceAssemblyPath), target, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"探查结果里没有 {target}。");
            var relations = (document.Relations ?? [])
                .Where(item => item.Geometry1 is not null && item.Geometry2 is not null)
                .ToArray();
            if (relations.Length == 0)
                throw new InvalidDataException("目标文档没有带几何的关系。");

            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            if (GetPids("SLDWORKS").Except(before).Any())
            {
                application.Visible = false;
                application.UserControl = false;
            }

            RunMatrix(application, Path.Combine(swDirectory, target + ".SLDASM"), document, relations, report);
            RunFloatFixIsolation(application, Path.Combine(swDirectory, "风滚子.SLDASM"), report);
            report.Success = true;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.Error = ex.Message;
            report.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            if (application is not null)
            {
                if (GetPids("SLDWORKS").Except(before).Any())
                {
                    try { application.ExitApp(); } catch { }
                }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        Console.Error.WriteLine(report.Verdict ?? report.Error);
        return report.Success ? 0 : 1;
    }

    private sealed record Cand(
        string Key,
        int Kind,
        double[] PointPart,
        double[] DirectionPart,
        double[] PointAsm,
        double[] DirectionAsm,
        Face2 Face,
        Component2 Component);

    private static void RunMatrix(
        ISldWorks application,
        string assemblyPath,
        AssemblyDocumentReading document,
        AssemblyRelation[] relations,
        ApplyReport report)
    {
        report.Assembly = assemblyPath;
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            assemblyPath, (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings)
            ?? throw new InvalidDataException($"打开失败：{assemblyPath}");
        try
        {
            var assembly = (AssemblyDoc)model;
            var components = (assembly.GetComponents(true) as Array ?? Array.Empty<object>())
                .Cast<object>().OfType<Component2>().ToArray();
            var byName = MapOccurrences(document.Children, components);
            var candidates = byName.ToDictionary(
                pair => pair.Key,
                pair => BuildCandidates(pair.Value),
                StringComparer.Ordinal);
            var baseline = components.ToDictionary(
                component => component.Name2,
                component => (double[])component.Transform2.ArrayData,
                StringComparer.Ordinal);

            // 测试床：第一条可建的关系。逐个姿势试，每次成功后删掉配合复位。
            var testbed = relations.First(item =>
                MateTypeMapper.Map(item).Kind == MatePlanKind.Mate);
            var (testbedSide1, testbedSide2) = LocatePair(testbed, byName, candidates)
                ?? throw new InvalidDataException("测试床关系的实体定位失败——这与 MateMatchProbe 的 100% 矛盾，须先查。");
            report.TestbedRelation = $"#{testbed.Index} {testbed.InterfaceName}";

            foreach (var variant in new[] { "V1", "V2", "V3", "V4", "V5" })
            {
                var outcome = TryVariant(variant, model, assembly, testbed, testbedSide1, testbedSide2);
                report.Variants.Add(outcome);
                if (outcome.MateCreated)
                    DeleteMateByName(model, outcome.MateName!);
                model.ClearSelection2(true);
            }

            var winner = report.Variants.FirstOrDefault(item => item.MateCreated)?.Name;
            report.Winner = winner;
            if (winner is null)
            {
                report.Verdict = "五种姿势全部失败——问题不在选择方式，也不在 AddMate 版本或对齐取值。";
                return;
            }

            // 用胜出姿势把该层全部关系建完，验证多配合 + 求解后位置稳定性。
            var applied = 0;
            foreach (var relation in relations)
            {
                var plan = MateTypeMapper.Map(relation);
                if (plan.Kind != MatePlanKind.Mate)
                    continue;
                var sides = LocatePair(relation, byName, candidates);
                if (sides is null)
                    continue;
                var (runSide1, runSide2) = sides.Value;
                var run = TryVariant(winner, model, assembly, relation, runSide1, runSide2);
                if (run.MateCreated)
                    applied++;
                else
                    report.FullRunFailures.Add($"#{relation.Index}：{run.Detail}");
                model.ClearSelection2(true);
            }

            model.ForceRebuild3(false);
            var drift = 0d;
            foreach (var component in components)
            {
                var actual = (double[])component.Transform2.ArrayData;
                var expected = baseline[component.Name2];
                for (var index = 9; index < 12; index++)
                    drift = Math.Max(drift, Math.Abs(actual[index] - expected[index]));
            }

            report.FullRunApplied = applied;
            report.FullRunDriftMeters = drift;
            report.Verdict = $"胜出姿势 {winner}；全量重建 {applied} 条配合，强制重建后最大平移漂移 {drift:G3} m。";
        }
        finally
        {
            try { application.CloseDoc(model.GetTitle()); } catch { }
        }
    }

    private static (MateCandidate, MateCandidate)? LocatePair(
        AssemblyRelation relation,
        IReadOnlyDictionary<string, Component2> byName,
        IReadOnlyDictionary<string, IReadOnlyList<Cand>> candidates)
    {
        var side1 = Locate(relation.Occurrence1, relation.Geometry1!, candidates);
        var side2 = Locate(relation.Occurrence2, relation.Geometry2!, candidates);
        return side1 is null || side2 is null ? null : (side1, side2);
    }

    private static MateCandidate? Locate(
        string? occurrence,
        RelationGeometry geometry,
        IReadOnlyDictionary<string, IReadOnlyList<Cand>> candidates)
    {
        if (occurrence is null || !candidates.TryGetValue(occurrence, out var pool))
            return null;
        var wrapped = pool
            .Select(item => new MateCandidate(item.Key, item.Kind, item.PointAsm, item.DirectionAsm, item))
            .ToArray();
        var match = MateGeometryMatcher.Match(geometry, wrapped);
        return match.Status == MateMatchStatus.Matched ? match.Candidate : null;
    }

    private static VariantOutcome TryVariant(
        string name,
        ModelDoc2 model,
        AssemblyDoc assembly,
        AssemblyRelation relation,
        MateCandidate side1,
        MateCandidate side2)
    {
        var outcome = new VariantOutcome { Name = name };
        var cand1 = (Cand)side1.Entity!;
        var cand2 = (Cand)side2.Entity!;
        var plan = MateTypeMapper.Map(relation);
        try
        {
            model.ClearSelection2(true);
            bool selected;
            switch (name)
            {
                case "V2":
                {
                    // GetCorresponding：把零件上下文的面映射为该组件实例上下文的实体。
                    var mapped1 = cand1.Component.GetCorresponding(cand1.Face) as Entity;
                    var mapped2 = cand2.Component.GetCorresponding(cand2.Face) as Entity;
                    if (mapped1 is null || mapped2 is null)
                    {
                        outcome.Detail = $"GetCorresponding 返回 null（侧1 {mapped1 is not null}，侧2 {mapped2 is not null}）";
                        return outcome;
                    }
                    selected = SelectWithMark(model, mapped1, false) && SelectWithMark(model, mapped2, true);
                    break;
                }
                case "V3":
                {
                    selected = SelectByPoint(model, cand1, false) && SelectByPoint(model, cand2, true);
                    break;
                }
                default:
                {
                    selected = SelectWithMark(model, (Entity)cand1.Face, false)
                        && SelectWithMark(model, (Entity)cand2.Face, true);
                    break;
                }
            }

            var selectionManager = (SelectionMgr)model.SelectionManager;
            outcome.SelectedCount = selectionManager.GetSelectedObjectCount2(-1);
            outcome.SelectedComponents = string.Join("、", Enumerable.Range(1, Math.Max(outcome.SelectedCount, 0))
                .Select(index => (selectionManager.GetSelectedObjectsComponent4(index, -1) as Component2)?.Name2 ?? "<null>"));
            if (!selected || outcome.SelectedCount != 2)
            {
                outcome.Detail = $"选择失败：selected={selected}, count={outcome.SelectedCount}";
                return outcome;
            }

            var align = name == "V5" ? 2 : (int)plan.Align;
            Feature? mate;
            int errorStatus;
            if (name == "V4")
            {
                mate = assembly.AddMate3(
                    (int)plan.MateType, align, false,
                    plan.Distance, plan.Distance, plan.Distance,
                    1, 1, 0, 0, 0, false, out errorStatus) as Feature;
            }
            else
            {
                mate = assembly.AddMate5(
                    (int)plan.MateType, align, false,
                    plan.Distance, plan.Distance, plan.Distance,
                    1, 1, 0, 0, 0, false, false, 0, out errorStatus) as Feature;
            }

            outcome.ErrorStatus = errorStatus;
            // swAddMateError_e：NoError = 1（不是 0！IncorrectSelections 是 4）。
            // 生产代码把 NoError 当失败删掉了刚建好的配合——这就是 26 号报表的整个阻塞。
            outcome.MateCreated = mate is not null && errorStatus == 1;
            outcome.MateName = mate?.Name;
            outcome.Detail = outcome.MateCreated
                ? $"成功，配合 {mate!.Name}"
                : $"errorStatus={errorStatus}，feature={(mate is null ? "null" : mate.Name)}";
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Detail = "异常：" + ex.Message;
            return outcome;
        }
    }

    private static bool SelectWithMark(ModelDoc2 model, Entity entity, bool append)
    {
        var selectionManager = (SelectionMgr)model.SelectionManager;
        var selectData = (SelectData)selectionManager.CreateSelectData();
        selectData.Mark = 1;
        return entity.Select4(append, selectData);
    }

    /// <summary>官方样例的姿势：按装配空间坐标选面。用 GetClosestPointOn 保证点真的落在有界面上。</summary>
    private static bool SelectByPoint(ModelDoc2 model, Cand candidate, bool append)
    {
        var onFacePart = candidate.Face.GetClosestPointOn(
            candidate.PointPart[0], candidate.PointPart[1], candidate.PointPart[2]) as double[];
        if (onFacePart is null || onFacePart.Length < 3)
            return false;
        var transform = candidate.Component.GetTotalTransform(true)?.ArrayData as double[]
            ?? (double[])candidate.Component.Transform2.ArrayData;
        var point = TransformPoint(onFacePart, transform);
        return model.Extension.SelectByID2(
            "", "FACE", point[0], point[1], point[2], append, 1, null,
            (int)swSelectOption_e.swSelectOptionDefault);
    }

    private static void DeleteMateByName(ModelDoc2 model, string mateName)
    {
        model.ClearSelection2(true);
        if (model.Extension.SelectByID2(mateName, "MATE", 0, 0, 0, false, 0, null, 0))
            model.EditDelete();
        model.ClearSelection2(true);
    }

    /// <summary>26 号报表 §4 的隔离实验：不建任何配合，只做 浮动全部 → 固定全部，量漂移。</summary>
    private static void RunFloatFixIsolation(ISldWorks application, string assemblyPath, ApplyReport report)
    {
        if (!File.Exists(assemblyPath))
            return;
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            assemblyPath, (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings);
        if (model is null)
            return;
        try
        {
            var assembly = (AssemblyDoc)model;
            var components = (assembly.GetComponents(true) as Array ?? Array.Empty<object>())
                .Cast<object>().OfType<Component2>().ToArray();
            var baseline = components.ToDictionary(
                component => component.Name2,
                component => (double[])component.Transform2.ArrayData,
                StringComparer.Ordinal);

            model.ClearSelection2(true);
            foreach (var component in components)
                component.Select4(true, null, false);
            assembly.UnfixComponent();
            var afterUnfix = MaxDrift(components, baseline);

            model.ClearSelection2(true);
            foreach (var component in components)
                component.Select4(true, null, false);
            assembly.FixComponent();
            model.ClearSelection2(true);
            var afterRefix = MaxDrift(components, baseline);

            report.FloatFixAssembly = assemblyPath;
            report.DriftAfterUnfix = afterUnfix;
            report.DriftAfterRefix = afterRefix;
        }
        finally
        {
            try { application.CloseDoc(model.GetTitle()); } catch { }
        }
    }

    private static double MaxDrift(IReadOnlyList<Component2> components, IReadOnlyDictionary<string, double[]> baseline)
    {
        var drift = 0d;
        foreach (var component in components)
        {
            var actual = (double[])component.Transform2.ArrayData;
            var expected = baseline[component.Name2];
            for (var index = 0; index < 12; index++)
                drift = Math.Max(drift, Math.Abs(actual[index] - expected[index]));
        }

        return drift;
    }

    private static IReadOnlyList<Cand> BuildCandidates(Component2 component)
    {
        var result = new List<Cand>();
        var transform = component.GetTotalTransform(true)?.ArrayData as double[]
            ?? (double[])component.Transform2.ArrayData;
        var bodies = component.GetBodies3((int)swBodyType_e.swSolidBody, out _) as Array;
        if (bodies is null)
            return result;
        var bodyIndex = 0;
        foreach (var raw in bodies)
        {
            if (raw is not Body2 body)
                continue;
            bodyIndex++;
            var faces = body.GetFaces() as Array;
            if (faces is null)
                continue;
            var faceIndex = 0;
            foreach (var rawFace in faces)
            {
                if (rawFace is not Face2 face)
                    continue;
                faceIndex++;
                if (face.GetSurface() is not Surface surface)
                    continue;
                double[]? point = null;
                double[]? direction = null;
                var kind = 0;
                if (surface.IsPlane() && surface.PlaneParams is double[] plane && plane.Length >= 6)
                {
                    kind = MateGeometryMatcher.GeometryPlane;
                    direction = [plane[0], plane[1], plane[2]];
                    point = [plane[3], plane[4], plane[5]];
                }
                else if (surface.IsCylinder() && surface.CylinderParams is double[] cylinder && cylinder.Length >= 6)
                {
                    kind = MateGeometryMatcher.GeometryAxis;
                    point = [cylinder[0], cylinder[1], cylinder[2]];
                    direction = [cylinder[3], cylinder[4], cylinder[5]];
                }
                else if (surface.IsCone() && surface.ConeParams is double[] cone && cone.Length >= 6)
                {
                    kind = MateGeometryMatcher.GeometryAxis;
                    point = [cone[0], cone[1], cone[2]];
                    direction = [cone[3], cone[4], cone[5]];
                }

                if (point is null || direction is null)
                    continue;
                result.Add(new Cand(
                    $"b{bodyIndex}f{faceIndex}",
                    kind,
                    point,
                    direction,
                    TransformPoint(point, transform),
                    TransformDirection(direction, transform),
                    face,
                    component));
            }
        }

        return result;
    }

    private static Dictionary<string, Component2> MapOccurrences(
        IReadOnlyList<AssemblyChild> children,
        IReadOnlyList<Component2> components)
    {
        var map = new Dictionary<string, Component2>(StringComparer.Ordinal);
        var pool = components.ToList();
        foreach (var child in children)
        {
            var expected = ToSolidWorksTransform(child.LocalTransform);
            var best = pool
                .Where(component => string.Equals(
                    Path.GetFileNameWithoutExtension(component.GetPathName()),
                    Path.GetFileNameWithoutExtension(child.SourcePath),
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(component =>
                {
                    var actual = (double[])component.Transform2.ArrayData;
                    var deviation = 0d;
                    for (var index = 9; index < 12; index++)
                        deviation = Math.Max(deviation, Math.Abs(expected[index] - actual[index]));
                    return deviation;
                })
                .FirstOrDefault();
            if (best is null)
                continue;
            pool.Remove(best);
            map[child.Name] = best;
        }

        return map;
    }

    private static double[] ToSolidWorksTransform(IReadOnlyList<double> source) =>
    [
        source[0], source[1], source[2],
        source[4], source[5], source[6],
        source[8], source[9], source[10],
        source[12], source[13], source[14],
        1, 0, 0, 0,
    ];

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

    private static int[] GetPids(string processName)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(processName).Select(item => item.Id).ToArray(); }
        catch { return []; }
    }
}

internal sealed class ApplyReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string Assembly { get; set; } = string.Empty;
    public string? TestbedRelation { get; set; }
    public List<VariantOutcome> Variants { get; } = [];
    public string? Winner { get; set; }
    public int FullRunApplied { get; set; }
    public double FullRunDriftMeters { get; set; }
    public List<string> FullRunFailures { get; } = [];
    public string? FloatFixAssembly { get; set; }
    public double DriftAfterUnfix { get; set; }
    public double DriftAfterRefix { get; set; }
    public string? Verdict { get; set; }
}

internal sealed class VariantOutcome
{
    public string Name { get; set; } = string.Empty;
    public int SelectedCount { get; set; }
    public string? SelectedComponents { get; set; }
    public int ErrorStatus { get; set; } = -1;
    public bool MateCreated { get; set; }
    public string? MateName { get; set; }
    public string? Detail { get; set; }
}
