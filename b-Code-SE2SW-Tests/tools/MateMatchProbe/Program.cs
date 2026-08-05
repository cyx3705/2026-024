using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SE2SW;
using SE2SW.Contracts;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MateMatchProbe;

/// <summary>
/// V3.5 第 6 步的只读探针：量出"关系几何能不能在 SolidWorks 组件上唯一命中一个面"。
///
/// 回答两个问题，两个都用数据说话、不靠推断：
///   1. <c>Face2.GetSurface()</c> 的曲面参数在**装配坐标系**还是**零件坐标系**？
///      两种假设都算一遍，看哪种的命中率高——沿用 V3.0 确定 MathTransform 布局时的同一套办法。
///   2. A 路成不成立？即：被关系引用的面，在 SW 侧的唯一命中率是多少。
///
/// 只读：打开 .SLDASM 与组件，不保存、不修改、不新建。
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
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--probe-result": probeResult = Path.GetFullPath(args[index + 1]); break;
                case "--sw-dir": swDirectory = Path.GetFullPath(args[index + 1]); break;
            }
        }

        if (probeResult is null || swDirectory is null)
        {
            Console.Error.WriteLine("用法：MateMatchProbe --probe-result <装配探查结果.json> --sw-dir <含 .SLDASM 的 SW 目录>");
            return 2;
        }

        var result = new ProbeReport { ProbeResult = probeResult, SolidWorksDirectory = swDirectory };
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var probe = JsonSerializer.Deserialize<AssemblyProbeResult>(File.ReadAllText(probeResult), JsonOptions)
                ?? throw new InvalidDataException("装配探查结果为空。");
            if (probe.Documents is not { Count: > 0 })
                throw new InvalidDataException("探查结果里没有逐文档读数，无法定位关系所属的装配文件。");

            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            var owned = GetPids("SLDWORKS").Except(before).ToArray();
            if (owned.Length > 0)
            {
                application.Visible = false;
                application.UserControl = false;
            }

            foreach (var document in probe.Documents)
            {
                var assemblyPath = Path.Combine(
                    swDirectory,
                    Path.GetFileNameWithoutExtension(document.SourceAssemblyPath) + ".SLDASM");
                result.Documents.Add(Inspect(application, document, assemblyPath));
            }

            result.TotalRelations = result.Documents.Sum(item => item.RelationsWithGeometry);
            result.AssemblyFrameMatched = result.Documents.Sum(item => item.AssemblyFrameMatched);
            result.PartFrameMatched = result.Documents.Sum(item => item.PartFrameMatched);
            result.Verdict = BuildVerdict(result);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            if (application is not null)
            {
                if (GetPids("SLDWORKS").Except(before).Any())
                {
                    try { application.ExitApp(); } catch { }
                }
                Release(application);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        Console.Error.WriteLine(result.Verdict ?? result.Error);
        return result.Success ? 0 : 1;
    }

    private static DocumentReport Inspect(
        ISldWorks application,
        AssemblyDocumentReading document,
        string assemblyPath)
    {
        var report = new DocumentReport
        {
            SourceAssembly = document.SourceAssemblyPath,
            Assembly = assemblyPath,
        };
        var relations = (document.Relations ?? [])
            .Where(item => item.Geometry1 is not null && item.Geometry2 is not null)
            .ToArray();
        report.RelationsWithGeometry = relations.Length;
        if (relations.Length == 0 || !File.Exists(assemblyPath))
        {
            report.Note = File.Exists(assemblyPath) ? "该层没有带几何的关系" : "找不到对应的 .SLDASM";
            return report;
        }

        var errors = 0;
        var warnings = 0;
        ModelDoc2? model = null;
        try
        {
            model = application.OpenDoc6(
                assemblyPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref errors,
                ref warnings);
            if (model is null || errors != 0)
                throw new InvalidDataException($"打开 .SLDASM 失败：errors={errors}");

            var assembly = (AssemblyDoc)model;
            var components = (assembly.GetComponents(true) as Array ?? Array.Empty<object>())
                .Cast<object>().OfType<Component2>().ToArray();
            report.ComponentCount = components.Length;

            // SE occurrence 名（如 太阳能板子.par:14）与 SW 组件名（太阳能板子-8）的实例号对不上——
            // SE 的编号有跳号。所以按"同一源文件 + 变换最接近"来配对，与生产门禁同一套办法。
            var byName = MapOccurrences(document.Children, components, report);

            foreach (var relation in relations)
            {
                var entry = new RelationReport
                {
                    Index = relation.Index,
                    InterfaceName = relation.InterfaceName,
                    Occurrence1 = relation.Occurrence1,
                    Occurrence2 = relation.Occurrence2,
                };

                var side1 = Evaluate(relation.Occurrence1, relation.Geometry1!, byName, entry, 1);
                var side2 = Evaluate(relation.Occurrence2, relation.Geometry2!, byName, entry, 2);
                entry.AssemblyFrameMatched = side1.assembly && side2.assembly;
                entry.PartFrameMatched = side1.part && side2.part;
                if (entry.AssemblyFrameMatched)
                    report.AssemblyFrameMatched++;
                if (entry.PartFrameMatched)
                    report.PartFrameMatched++;
                report.Relations.Add(entry);
            }
        }
        catch (Exception ex)
        {
            report.Note = ex.Message;
        }
        finally
        {
            if (model is not null)
            {
                try { application.CloseDoc(model.GetTitle()); } catch { }
                Release(model);
            }
        }

        return report;
    }

    private static (bool assembly, bool part) Evaluate(
        string? occurrenceName,
        RelationGeometry geometry,
        IReadOnlyDictionary<string, Component2> byName,
        RelationReport entry,
        int side)
    {
        if (occurrenceName is null || !byName.TryGetValue(occurrenceName, out var component))
        {
            entry.Notes.Add($"侧 {side}：找不到对应组件 {occurrenceName}");
            return (false, false);
        }

        var (assemblyFrame, partFrame) = BuildCandidates(component);
        var inAssembly = MateGeometryMatcher.Match(geometry, assemblyFrame);
        var inPart = MateGeometryMatcher.Match(geometry, partFrame);
        entry.Notes.Add(
            $"侧 {side}（{occurrenceName}）：面 {assemblyFrame.Count} 个；"
            + $"装配系 {inAssembly.Status}（候选 {inAssembly.CandidateKeys.Count}）；"
            + $"零件系 {inPart.Status}（候选 {inPart.CandidateKeys.Count}）");
        return (inAssembly.Status == MateMatchStatus.Matched, inPart.Status == MateMatchStatus.Matched);
    }

    /// <summary>
    /// 枚举组件全部实体面，产出两套候选：一套按"曲面参数已经是装配系"，
    /// 一套按"曲面参数是零件系、需乘组件总变换"。哪套命中就是哪套——不预设答案。
    /// </summary>
    private static (IReadOnlyList<MateCandidate> AssemblyFrame, IReadOnlyList<MateCandidate> PartFrame)
        BuildCandidates(Component2 component)
    {
        var asIs = new List<MateCandidate>();
        var transformed = new List<MateCandidate>();
        Collect(component, asIs, transformed, "c");
        return (asIs, transformed);
    }

    /// <summary>
    /// 递归收集组件及其**全部后代**的面。
    ///
    /// 子装配组件自己没有实体（实测：GetBodies3 返回空、面 0 个），实体挂在它的子组件上。
    /// 关系可以直接引用一个子装配实例，所以必须往下走。每个叶组件用它自己的
    /// GetTotalTransform(true)——那已经是相对当前顶层文档的，正好是我们要的参考系。
    /// </summary>
    private static void Collect(Component2 component, List<MateCandidate> asIs, List<MateCandidate> transformed, string prefix)
    {
        CollectOwnFaces(component, asIs, transformed, prefix);
        if (component.GetChildren() is not Array children)
            return;
        var childIndex = 0;
        foreach (var raw in children)
        {
            if (raw is not Component2 child)
                continue;
            childIndex++;
            Collect(child, asIs, transformed, prefix + "." + childIndex.ToString());
        }
    }

    private static void CollectOwnFaces(Component2 component, List<MateCandidate> asIs, List<MateCandidate> transformed, string prefix)
    {
        var total = ReadTransform(component);
        var bodies = component.GetBodies3((int)swBodyType_e.swSolidBody, out _) as Array;
        if (bodies is null)
            return;

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
                var key = $"{prefix}b{bodyIndex}f{faceIndex}";
                if (face.GetSurface() is not Surface surface)
                    continue;

                double[]? point = null;
                double[]? direction = null;
                var kind = 0;
                if (surface.IsPlane() && surface.PlaneParams is double[] plane && plane.Length >= 6)
                {
                    // PlaneParams：前三个是法向，后三个是根点。
                    kind = MateGeometryMatcher.GeometryPlane;
                    direction = [plane[0], plane[1], plane[2]];
                    point = [plane[3], plane[4], plane[5]];
                }
                else if (surface.IsCylinder() && surface.CylinderParams is double[] cylinder && cylinder.Length >= 6)
                {
                    // CylinderParams：前三个是原点，接着三个是轴向，最后是半径。
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
                asIs.Add(new MateCandidate(key, kind, point, direction));
                transformed.Add(new MateCandidate(
                    key, kind, TransformPoint(point, total), TransformDirection(direction, total)));
            }
        }
    }

    private static Dictionary<string, Component2> MapOccurrences(
        IReadOnlyList<AssemblyChild> children,
        IReadOnlyList<Component2> components,
        DocumentReport report)
    {
        var map = new Dictionary<string, Component2>(StringComparer.Ordinal);
        var pool = components.ToList();
        foreach (var child in children)
        {
            var expected = ToSolidWorksTransform(child.LocalTransform);
            var best = pool
                .Where(component => SameSource(component, child.SourcePath))
                .OrderBy(component => TranslationDeviation(expected, ReadTransform(component)))
                .FirstOrDefault();
            if (best is null)
            {
                report.Notes.Add($"没有与 {child.Name} 对应的 SW 组件");
                continue;
            }
            pool.Remove(best);
            map[child.Name] = best;
        }
        return map;
    }

    private static bool SameSource(Component2 component, string sourcePath)
    {
        var path = component.GetPathName();
        return !string.IsNullOrEmpty(path)
            && string.Equals(
                Path.GetFileNameWithoutExtension(path),
                Path.GetFileNameWithoutExtension(sourcePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static double TranslationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        var max = 0d;
        for (var index = 9; index < 12; index++)
            max = Math.Max(max, Math.Abs(expected[index] - actual[index]));
        return max;
    }

    private static double[] ReadTransform(Component2 component)
    {
        var transform = component.GetTotalTransform(true) ?? component.Transform2;
        return transform?.ArrayData as double[] ?? [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
    }

    /// <summary>SW 变换布局：旋转 0..8（行主序），平移 9..11，缩放 12。</summary>
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

    private static double[] ToSolidWorksTransform(IReadOnlyList<double> source) =>
    [
        source[0], source[1], source[2],
        source[4], source[5], source[6],
        source[8], source[9], source[10],
        source[12], source[13], source[14],
        1, 0, 0, 0,
    ];

    private static string BuildVerdict(ProbeReport report)
    {
        if (report.TotalRelations == 0)
            return "没有带几何的关系可评估。";
        var assembly = report.AssemblyFrameMatched;
        var part = report.PartFrameMatched;
        var frame = assembly >= part ? "装配坐标系" : "零件坐标系";
        var best = Math.Max(assembly, part);
        return $"带几何的关系 {report.TotalRelations} 条；两侧均唯一命中："
            + $"装配系 {assembly} 条、零件系 {part} 条。"
            + $"曲面参数应按 {frame} 解释；A 路命中率 {best * 100.0 / report.TotalRelations:F1}%。";
    }

    private static int[] GetPids(string processName)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(processName).Select(item => item.Id).ToArray(); }
        catch { return []; }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }
    }
}

internal sealed class ProbeReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string ProbeResult { get; set; } = string.Empty;
    public string SolidWorksDirectory { get; set; } = string.Empty;
    public int TotalRelations { get; set; }
    public int AssemblyFrameMatched { get; set; }
    public int PartFrameMatched { get; set; }
    public string? Verdict { get; set; }
    public List<DocumentReport> Documents { get; } = [];
}

internal sealed class DocumentReport
{
    public string SourceAssembly { get; set; } = string.Empty;
    public string Assembly { get; set; } = string.Empty;
    public int ComponentCount { get; set; }
    public int RelationsWithGeometry { get; set; }
    public int AssemblyFrameMatched { get; set; }
    public int PartFrameMatched { get; set; }
    public string? Note { get; set; }
    public List<string> Notes { get; } = [];
    public List<RelationReport> Relations { get; } = [];
}

internal sealed class RelationReport
{
    public int Index { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public string? Occurrence1 { get; set; }
    public string? Occurrence2 { get; set; }
    public bool AssemblyFrameMatched { get; set; }
    public bool PartFrameMatched { get; set; }
    public List<string> Notes { get; } = [];
}
