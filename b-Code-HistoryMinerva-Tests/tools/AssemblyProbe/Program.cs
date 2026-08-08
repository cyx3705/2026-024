using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AssemblyProbe;

internal static class Program
{
    private const string SolidEdgeProgId = "SolidEdge.Application";
    private const string SolidWorksProgId = "SldWorks.Application";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("用法: AssemblyProbe --se-asm <绝对.asm> [--gold-sw <绝对.SLDASM>] --sw-parts <SLDPRT目录> --build-output <新.SLDASM> [--fixture-part <绝对.par>] [--blank-sw <空.SLDASM>]");
            return 2;
        }

        var result = new ProbeResult
        {
            SourceAssembly = options.SourceAssembly,
            GoldAssembly = options.GoldAssembly,
            BuildOutput = options.BuildOutput,
        };

        try
        {
            if (options.FixturePart is not null)
                GenerateSolidEdgeFixture(
                    options.SourceAssembly,
                    options.FixturePart,
                    options.NestedFixture,
                    options.SingleFixture,
                    options.StateFixture,
                    options.DuplicateFixture,
                    options.MissingFixture,
                    result.Notes);
            result.SolidEdgeOccurrences = ReadSolidEdge(options.SourceAssembly, result.Notes);
            if (options.GoldAssembly is not null)
                result.GoldComponents = ReadSolidWorksAssembly(options.GoldAssembly, result.Notes);
            result.MatrixMapping = options.NestedFixture
                ? DetectNestedKnownMapping(result.SolidEdgeOccurrences, result.Notes)
                : result.GoldComponents.Count > 0
                    ? DetectMatrixMapping(result.SolidEdgeOccurrences, result.GoldComponents)
                    : DetectMatrixMappingByRange(options, result.SolidEdgeOccurrences, result.Notes);
            result.BuiltComponents = BuildSolidWorksAssembly(
                options,
                result.SolidEdgeOccurrences,
                result.MatrixMapping,
                result.Notes);
            result.Success = result.MatrixMapping.MaxRotationDeviation < 1e-9
                && result.MatrixMapping.MaxTranslationDeviationMeters < 1e-6
                && result.BuiltComponents.Count == result.SolidEdgeOccurrences.Count(x => !x.IsSubAssembly)
                && result.BuiltComponents.All(x => x.IsFixed)
                && File.Exists(options.BuildOutput)
                && new FileInfo(options.BuildOutput).Length > 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }

        result.EdgeProcessesAfter = GetPids("Edge");
        result.SolidWorksProcessesAfter = GetPids("SLDWORKS");
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Success ? 0 : 1;
    }

    private static void GenerateSolidEdgeFixture(
        string output,
        string partPath,
        bool nested,
        bool singleFixture,
        bool stateFixture,
        bool duplicateFixture,
        bool missingFixture,
        List<string> notes)
    {
        if (File.Exists(output))
            throw new IOException($"探针拒绝覆盖 SE fixture：{output}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var before = GetPids("Edge");
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        string? deleteAfterClose = null;
        try
        {
            var type = Type.GetTypeFromProgID(SolidEdgeProgId, throwOnError: false)
                ?? throw new InvalidOperationException("SolidEdge.Application 未注册。");
            applicationObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Solid Edge COM 返回空实例。");
            dynamic application = applicationObject;
            try { application.Visible = false; } catch { }
            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            if (nested)
            {
                var childPath = Path.Combine(
                    Path.GetDirectoryName(output)!,
                    Path.GetFileNameWithoutExtension(output) + ".child.asm");
                object? childObject = null;
                object? childOccurrencesObject = null;
                try
                {
                    childObject = documents.Add("SolidEdge.AssemblyDocument", Type.Missing);
                    dynamic child = childObject;
                    childOccurrencesObject = child.Occurrences;
                    dynamic childOccurrences = childOccurrencesObject;
                    Array local = new double[]
                    {
                        1, 0, 0, 0,
                        0, 0, 1, 0,
                        0, -1, 0, 0,
                        0.04, 0.02, -0.03, 1,
                    };
                    object? leaf = childOccurrences.AddWithMatrix(partPath, ref local);
                    Release(leaf);
                    child.UpdateAll();
                    child.SaveAs(
                        childPath,
                        Type.Missing, Type.Missing, Type.Missing, Type.Missing,
                        Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                    child.Close(false);
                }
                finally
                {
                    Release(childOccurrencesObject);
                    Release(childObject);
                }

                documentObject = documents.Add("SolidEdge.AssemblyDocument", Type.Missing);
                dynamic top = documentObject;
                dynamic topOccurrences = top.Occurrences;
                Array parent = new double[]
                {
                    0, 1, 0, 0,
                    -1, 0, 0, 0,
                    0, 0, 1, 0,
                    0.15, -0.02, 0.08, 1,
                };
                object? subassembly = topOccurrences.AddWithMatrix(childPath, ref parent);
                Release(subassembly);
                Release(topOccurrences);
                top.UpdateAll();
                top.SaveAs(
                    output,
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing,
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                notes.Add("已生成受控两层 SE fixture：父级绕 Z 轴、叶零件绕 X 轴并分别平移。");
                return;
            }

            documentObject = documents.Add("SolidEdge.AssemblyDocument", Type.Missing);
            dynamic document = documentObject;
            dynamic occurrences = document.Occurrences;
            var firstPartPath = partPath;
            var secondPartPath = partPath;
            if (duplicateFixture)
            {
                var firstDirectory = Path.Combine(Path.GetDirectoryName(output)!, "duplicate-a");
                var secondDirectory = Path.Combine(Path.GetDirectoryName(output)!, "duplicate-b");
                Directory.CreateDirectory(firstDirectory);
                Directory.CreateDirectory(secondDirectory);
                firstPartPath = Path.Combine(firstDirectory, "Same.par");
                secondPartPath = Path.Combine(secondDirectory, "Same.par");
                File.Copy(partPath, firstPartPath, overwrite: false);
                File.Copy(partPath, secondPartPath, overwrite: false);
            }
            else if (missingFixture)
            {
                firstPartPath = Path.Combine(Path.GetDirectoryName(output)!, "missing-reference.par");
                File.Copy(partPath, firstPartPath, overwrite: false);
                secondPartPath = firstPartPath;
                deleteAfterClose = firstPartPath;
            }
            Array first = new double[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0.15, -0.02, 0.03, 1,
            };
            Array second = new double[]
            {
                1, 0, 0, 0,
                0, 0, 1, 0,
                0, -1, 0, 0,
                -0.04, 0.12, 0.08, 1,
            };
            object? firstOccurrence = occurrences.AddWithMatrix(firstPartPath, ref first);
            object? secondOccurrence = missingFixture || singleFixture
                ? null
                : occurrences.AddWithMatrix(secondPartPath, ref second);
            if (stateFixture)
            {
                ((dynamic)firstOccurrence!).Visible = false;
                ((dynamic)secondOccurrence!).Activate = false;
            }
            Release(firstOccurrence);
            Release(secondOccurrence);
            Release(occurrences);
            document.UpdateAll();
            document.SaveAs(
                output,
                Type.Missing, Type.Missing, Type.Missing, Type.Missing,
                Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            notes.Add(singleFixture
                ? "已生成单零件受控 SE fixture。"
                : stateFixture
                ? "已生成隐藏实例 + 抑制/未激活实例的受控 SE fixture。"
                : duplicateFixture
                    ? "已生成同名不同路径零件的受控 SE fixture。"
                    : missingFixture
                        ? "已生成即将移除引用零件的受控 SE fixture。"
                        : "已生成两个重复实例的受控 SE fixture：包含平移与绕 X 轴 90° 旋转。");
        }
        finally
        {
            try
            {
                if (documentObject is not null)
                    ((dynamic)documentObject).Close(false);
            }
            catch { }
            Release(documentObject);
            Release(documentsObject);
            if (applicationObject is not null && GetPids("Edge").Except(before).Any())
            {
                try { ((dynamic)applicationObject).Quit(); } catch { }
            }
            Release(applicationObject);
        }
        if (deleteAfterClose is not null && File.Exists(deleteAfterClose))
            File.Delete(deleteAfterClose);
    }

    private static List<OccurrenceFact> ReadSolidEdge(string path, List<string> notes)
    {
        var before = GetPids("Edge");
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        var facts = new List<OccurrenceFact>();
        try
        {
            var type = Type.GetTypeFromProgID(SolidEdgeProgId, throwOnError: false)
                ?? throw new InvalidOperationException("SolidEdge.Application 未注册。");
            applicationObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Solid Edge COM 返回空实例。");
            dynamic application = applicationObject;
            try { application.Visible = false; } catch { }
            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            documentObject = documents.Open(path);
            dynamic document = documentObject;
            dynamic occurrences = document.Occurrences;
            for (var index = 1; index <= Convert.ToInt32(occurrences.Count); index++)
            {
                dynamic occurrence = occurrences.Item(index);
                ReadOccurrence(occurrence, null, Identity(), facts, isSubOccurrence: false);
                Release(occurrence);
            }
            Release(occurrences);
            notes.Add($"Solid Edge 探查到 {facts.Count} 个 occurrence，其中叶零件 {facts.Count(x => !x.IsSubAssembly)} 个。");
            return facts;
        }
        finally
        {
            try
            {
                if (documentObject is not null)
                {
                    dynamic document = documentObject;
                    document.Close(false);
                }
            }
            catch { }
            Release(documentObject);
            Release(documentsObject);
            if (applicationObject is not null)
            {
                var after = GetPids("Edge");
                if (after.Except(before).Any())
                {
                    try { ((dynamic)applicationObject).Quit(); } catch { }
                }
            }
            Release(applicationObject);
        }
    }

    private static void ReadOccurrence(
        dynamic occurrence,
        string? parentId,
        double[] parentWorld,
        List<OccurrenceFact> facts,
        bool isSubOccurrence)
    {
        var id = parentId is null
            ? $"{Convert.ToString(occurrence.Name)}"
            : $"{parentId}/{Convert.ToString(occurrence.Name)}";
        var raw = ReadMatrix(occurrence);
        var composed = Multiply(raw, parentWorld);
        var isSubAssembly = Convert.ToBoolean(occurrence.Subassembly);
        var sourcePath = Convert.ToString(isSubOccurrence
            ? occurrence.SubOccurrenceFileName
            : occurrence.PartFileName) ?? string.Empty;
        bool visible;
        try { visible = Convert.ToBoolean(occurrence.Visible); }
        catch { visible = true; }

        facts.Add(new OccurrenceFact(
            id,
            parentId,
            sourcePath,
            isSubAssembly,
            visible,
            raw,
            composed,
            ReadRange(occurrence, isSubOccurrence)));

        if (!isSubAssembly)
            return;

        dynamic children = occurrence.SubOccurrences;
        try
        {
            for (var index = 1; index <= Convert.ToInt32(children.Count); index++)
            {
                dynamic child = children.Item(index);
                try { ReadOccurrence(child, id, composed, facts, isSubOccurrence: true); }
                finally { Release(child); }
            }
        }
        finally
        {
            Release(children);
        }
    }

    private static double[]? ReadRange(dynamic occurrence, bool isSubOccurrence)
    {
        object? rangeTarget = null;
        try
        {
            rangeTarget = isSubOccurrence ? occurrence.ThisAsOccurrence : occurrence;
            dynamic target = rangeTarget;
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            target.Range(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            return [minX, minY, minZ, maxX, maxY, maxZ];
        }
        catch
        {
            return null;
        }
        finally
        {
            if (isSubOccurrence)
                Release(rangeTarget);
        }
    }

    private static List<ComponentFact> ReadSolidWorksAssembly(string path, List<string> notes)
    {
        var session = OpenSolidWorks();
        ModelDoc2? model = null;
        try
        {
            var errors = 0;
            var warnings = 0;
            model = session.Application.OpenDoc6(
                path,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref errors,
                ref warnings);
            if (model is null)
                throw new InvalidOperationException($"金标准 SLDASM 打开失败：errors={errors}, warnings={warnings}");

            var assembly = (AssemblyDoc)model;
            var components = (object[]?)assembly.GetComponents(false) ?? [];
            var facts = components.Cast<Component2>().Select(ReadComponent).ToList();
            notes.Add($"金标准 SLDASM 打开 errors={errors}, warnings={warnings}，组件 {facts.Count} 个。");
            return facts;
        }
        finally
        {
            if (model is not null)
            {
                try { session.Application.CloseDoc(model.GetTitle()); } catch { }
                Release(model);
            }
            session.Dispose();
        }
    }

    private static MatrixMapping DetectMatrixMapping(
        IReadOnlyList<OccurrenceFact> occurrences,
        IReadOnlyList<ComponentFact> components)
    {
        var leaves = occurrences.Where(x => !x.IsSubAssembly).ToArray();
        if (leaves.Length == 0 || components.Count == 0)
            throw new InvalidOperationException("探针样本没有可比较的叶零件或 SW 组件。");

        var candidates = new[]
        {
            ScoreMapping("packed-row", leaves, components, transpose: false, useComposed: false),
            ScoreMapping("packed-transpose", leaves, components, transpose: true, useComposed: false),
            ScoreMapping("composed-row", leaves, components, transpose: false, useComposed: true),
            ScoreMapping("composed-transpose", leaves, components, transpose: true, useComposed: true),
        };
        return candidates.OrderBy(x => x.Score).First();
    }

    private static MatrixMapping DetectMatrixMappingByRange(
        ProbeOptions options,
        IReadOnlyList<OccurrenceFact> occurrences,
        List<string> notes)
    {
        var candidates = new List<MatrixMapping>();
        foreach (var useComposed in new[] { false, true })
            foreach (var transpose in new[] { false, true })
            {
                var name = $"range-{(useComposed ? "composed" : "raw")}-{(transpose ? "transpose" : "row")}";
                var candidateOutput = Path.Combine(
                    Path.GetDirectoryName(options.BuildOutput)!,
                    Path.GetFileNameWithoutExtension(options.BuildOutput) + "." + name + ".SLDASM");
                var built = BuildSolidWorksAssembly(
                    options with { BuildOutput = candidateOutput },
                    occurrences,
                    new MatrixMapping(name, transpose, useComposed, 0, 0, 0),
                    notes);
                var deviation = ScoreRanges(occurrences, built);
                candidates.Add(new MatrixMapping(name, transpose, useComposed, deviation, deviation, deviation));
            }
        var winner = candidates.OrderBy(x => x.Score).First();
        notes.Add($"无可用金标准组件，按 SE/SW 组件包围盒选择映射 {winner.Name}，最大偏差 {winner.Score:G6} m。");
        return winner;
    }

    private static MatrixMapping DetectNestedKnownMapping(
        IReadOnlyList<OccurrenceFact> occurrences,
        List<string> notes)
    {
        var leaf = occurrences.Single(x => !x.IsSubAssembly);
        var expected = new double[]
        {
            0, 1, 0, 0,
            0, 0, 1, 0,
            1, 0, 0, 0,
            0.13, 0.02, 0.05, 1,
        };
        var maxRotation = 0d;
        foreach (var index in new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10 })
            maxRotation = Math.Max(maxRotation, Math.Abs(leaf.RawTransform[index] - expected[index]));
        var maxTranslation = 0d;
        foreach (var index in new[] { 12, 13, 14 })
            maxTranslation = Math.Max(maxTranslation, Math.Abs(leaf.RawTransform[index] - expected[index]));
        notes.Add($"两层 fixture 证明 SubOccurrence.GetMatrix 已返回世界矩阵；与手工矩阵乘积的最大偏差 rotation={maxRotation:G6}, translation={maxTranslation:G6} m。");
        return new MatrixMapping(
            "known-nested-world-row",
            TransposeRotation: false,
            UseComposed: false,
            maxRotation,
            maxTranslation,
            maxRotation + maxTranslation);
    }

    private static double ScoreRanges(
        IReadOnlyList<OccurrenceFact> occurrences,
        IReadOnlyList<ComponentFact> components)
    {
        var expected = occurrences.Where(x => !x.IsSubAssembly && x.Range is not null)
            .Select(x => x.Range!).ToList();
        var actual = components.Where(x => x.Box is not null).Select(x => x.Box!).ToList();
        if (expected.Count == 0 || expected.Count != actual.Count)
            return double.MaxValue;
        var max = 0d;
        while (expected.Count > 0)
        {
            var source = expected[0];
            expected.RemoveAt(0);
            var best = actual
                .Select((box, index) => new { box, index, deviation = MaxDeviation(source, box) })
                .OrderBy(x => x.deviation)
                .First();
            max = Math.Max(max, best.deviation);
            actual.RemoveAt(best.index);
        }
        return max;
    }

    private static double MaxDeviation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var max = 0d;
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
            max = Math.Max(max, Math.Abs(left[index] - right[index]));
        return max;
    }

    private static MatrixMapping ScoreMapping(
        string name,
        IReadOnlyList<OccurrenceFact> leaves,
        IReadOnlyList<ComponentFact> components,
        bool transpose,
        bool useComposed)
    {
        var remaining = components.ToList();
        var maxRotation = 0d;
        var maxTranslation = 0d;
        foreach (var leaf in leaves)
        {
            var fileName = Path.GetFileNameWithoutExtension(leaf.SourcePath);
            var component = remaining.FirstOrDefault(x =>
                string.Equals(Path.GetFileNameWithoutExtension(x.Path), fileName, StringComparison.OrdinalIgnoreCase))
                ?? remaining.First();
            remaining.Remove(component);
            var converted = ToSolidWorks(useComposed ? leaf.ComposedTransform : leaf.RawTransform, transpose);
            for (var i = 0; i < 9; i++)
                maxRotation = Math.Max(maxRotation, Math.Abs(converted[i] - component.Transform[i]));
            for (var i = 9; i < 12; i++)
                maxTranslation = Math.Max(maxTranslation, Math.Abs(converted[i] - component.Transform[i]));
        }
        return new MatrixMapping(name, transpose, useComposed, maxRotation, maxTranslation, maxRotation + maxTranslation);
    }

    private static List<ComponentFact> BuildSolidWorksAssembly(
        ProbeOptions options,
        IReadOnlyList<OccurrenceFact> occurrences,
        MatrixMapping mapping,
        List<string> notes)
    {
        if (File.Exists(options.BuildOutput))
            throw new IOException($"探针拒绝覆盖输出：{options.BuildOutput}");
        Directory.CreateDirectory(Path.GetDirectoryName(options.BuildOutput)!);

        var session = OpenSolidWorks();
        ModelDoc2? model = null;
        var inserted = new List<Component2>();
        var openedParts = new List<ModelDoc2>();
        try
        {
            var template = session.Application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            // SolidWorks may return the documented virtual template token
            // "~BLANK_ASSY_TEMPLATE.asmdot" instead of a physical path.
            if (string.IsNullOrWhiteSpace(template))
                throw new InvalidOperationException($"SolidWorks 装配模板无效：{template}");
            var resolvedTemplate = session.Application.GetDocumentTemplate(
                (int)swDocumentTypes_e.swDocASSEMBLY,
                template,
                0,
                0,
                0);
            if (!string.IsNullOrWhiteSpace(resolvedTemplate))
                template = resolvedTemplate;
            for (var attempt = 1; attempt <= 10 && model is null; attempt++)
            {
                model = session.Application.INewDocument2(template, 0, 0, 0);
                if (model is null)
                    Thread.Sleep(500);
            }
            if (model is null && options.BlankAssembly is not null)
            {
                var openErrors = 0;
                var openWarnings = 0;
                var blank = session.Application.OpenDoc6(
                    options.BlankAssembly,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings);
                if (blank is null)
                    throw new InvalidOperationException($"空 SLDASM 打开失败：errors={openErrors}, warnings={openWarnings}");
                var temporaryTemplate = Path.Combine(
                    Path.GetDirectoryName(options.BuildOutput)!,
                    Path.GetFileNameWithoutExtension(options.BuildOutput) + ".probe.asmdot");
                var saveErrors = 0;
                var saveWarnings = 0;
                var templateSaved = blank.Extension.SaveAs3(
                    temporaryTemplate,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    null,
                    ref saveErrors,
                    ref saveWarnings);
                session.Application.CloseDoc(blank.GetTitle());
                Release(blank);
                if (!templateSaved || saveErrors != 0 || !File.Exists(temporaryTemplate))
                    throw new InvalidOperationException($"临时装配模板生成失败：saved={templateSaved}, errors={saveErrors}, warnings={saveWarnings}");
                model = session.Application.INewDocument2(temporaryTemplate, 0, 0, 0);
                notes.Add($"默认装配模板不可用；探针从显式空 SLDASM 生成临时 ASMDOT 后验证 NewDocument，errors={openErrors}, warnings={openWarnings}。");
            }
            if (model is null)
                throw new InvalidOperationException($"SolidWorks NewDocument 连续返回 null，模板={template}。");
            var activateErrors = 0;
            session.Application.ActivateDoc3(model.GetTitle(), false, 0, ref activateErrors);
            if (activateErrors != 0)
                notes.Add($"装配文档激活返回 errors={activateErrors}。");
            var assembly = (AssemblyDoc)model;
            var math = (MathUtility)session.Application.GetMathUtility();
            foreach (var partPath in occurrences.Where(x => !x.IsSubAssembly)
                         .Select(x => Path.Combine(
                             options.SolidWorksPartDirectory,
                             Path.GetFileNameWithoutExtension(x.SourcePath) + ".SLDPRT"))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var openErrors = 0;
                var openWarnings = 0;
                var opened = session.Application.OpenDoc6(
                    partPath,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings);
                if (opened is null)
                    throw new InvalidOperationException($"组件零件打开失败：{partPath}, errors={openErrors}, warnings={openWarnings}");
                openedParts.Add(opened);
            }
            session.Application.ActivateDoc3(model.GetTitle(), false, 0, ref activateErrors);
            foreach (var occurrence in occurrences.Where(x => !x.IsSubAssembly))
            {
                var partPath = Path.Combine(
                    options.SolidWorksPartDirectory,
                    Path.GetFileNameWithoutExtension(occurrence.SourcePath) + ".SLDPRT");
                if (!File.Exists(partPath))
                    throw new FileNotFoundException("缺少探针所需 SLDPRT。", partPath);
                var component = assembly.AddComponent5(
                    partPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    0) ?? throw new InvalidOperationException($"AddComponent5 返回 null：{partPath}");
                var matrix = ToSolidWorks(
                    mapping.UseComposed ? occurrence.ComposedTransform : occurrence.RawTransform,
                    mapping.TransposeRotation);
                var transform = (MathTransform)math.CreateTransform(matrix);
                component.Transform2 = transform;
                inserted.Add(component);
                Release(transform);
            }

            model.ClearSelection2(true);
            foreach (var component in inserted)
            {
                if (!component.Select4(true, null, false))
                    throw new InvalidOperationException($"组件选择失败：{component.Name2}");
            }
            assembly.FixComponent();

            var errors = 0;
            var warnings = 0;
            var saved = model.Extension.SaveAs3(
                options.BuildOutput,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                null,
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
                throw new InvalidOperationException($"SLDASM 保存失败：saved={saved}, errors={errors}, warnings={warnings}");
            notes.Add($"探针新建 SLDASM saved={saved}, errors={errors}, warnings={warnings}。");
            return inserted.Select(ReadComponent).ToList();
        }
        finally
        {
            if (model is not null)
            {
                try { session.Application.CloseDoc(model.GetTitle()); } catch { }
                Release(model);
            }
            foreach (var component in inserted)
                Release(component);
            foreach (var part in openedParts)
            {
                try { session.Application.CloseDoc(part.GetTitle()); } catch { }
                Release(part);
            }
            session.Dispose();
        }
    }

    private static SwSession OpenSolidWorks()
    {
        var before = GetPids("SLDWORKS");
        var type = Type.GetTypeFromProgID(SolidWorksProgId, throwOnError: false)
            ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
        var application = (ISldWorks?)Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
        var owned = GetPids("SLDWORKS").Except(before).ToArray();
        return new SwSession(application, owned);
    }

    private static ComponentFact ReadComponent(Component2 component)
    {
        var transform = component.Transform2;
        try
        {
            var data = transform is null ? IdentitySw() : ToDoubles(transform.ArrayData, 16);
            double[]? box = null;
            try { box = ToDoubles(component.GetBox(false, false), 6); } catch { }
            return new ComponentFact(component.Name2, component.GetPathName(), data, component.IsFixed(), box);
        }
        finally
        {
            Release(transform);
        }
    }

    private static double[] ReadMatrix(dynamic occurrence)
    {
        Array matrix = new double[16];
        occurrence.GetMatrix(ref matrix);
        return ToDoubles(matrix, 16);
    }

    private static double[] ToSolidWorks(double[] se, bool transpose)
    {
        var result = new double[16];
        if (transpose)
        {
            result[0] = se[0]; result[1] = se[4]; result[2] = se[8];
            result[3] = se[1]; result[4] = se[5]; result[5] = se[9];
            result[6] = se[2]; result[7] = se[6]; result[8] = se[10];
        }
        else
        {
            result[0] = se[0]; result[1] = se[1]; result[2] = se[2];
            result[3] = se[4]; result[4] = se[5]; result[5] = se[6];
            result[6] = se[8]; result[7] = se[9]; result[8] = se[10];
        }
        result[9] = se[12];
        result[10] = se[13];
        result[11] = se[14];
        result[12] = 1;
        return result;
    }

    private static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                for (var k = 0; k < 4; k++)
                    result[(row * 4) + column] += left[(row * 4) + k] * right[(k * 4) + column];
        return result;
    }

    private static double[] Identity() =>
        [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static double[] IdentitySw() =>
        [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    private static double[] ToDoubles(object value, int expected)
    {
        if (value is not Array array || array.Length < expected)
            throw new InvalidDataException($"矩阵不是至少 {expected} 元素的数组。");
        return array.Cast<object>().Take(expected).Select(Convert.ToDouble).ToArray();
    }

    private static int[] GetPids(string processName) =>
        Process.GetProcessesByName(processName)
            .Select(process => { using (process) return process.Id; })
            .OrderBy(id => id)
            .ToArray();

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private sealed class SwSession(ISldWorks application, int[] ownedPids) : IDisposable
    {
        public ISldWorks Application { get; } = application;

        public void Dispose()
        {
            if (ownedPids.Length > 0)
            {
                try { Application.ExitApp(); } catch { }
            }
            Release(Application);
            var stopwatch = Stopwatch.StartNew();
            while (ownedPids.Any(IsAlive) && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(100);
        }

        private static bool IsAlive(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }
}

internal sealed record ProbeOptions(
    string SourceAssembly,
    string? GoldAssembly,
    string SolidWorksPartDirectory,
    string BuildOutput,
    string? FixturePart,
    string? BlankAssembly,
    bool NestedFixture,
    bool SingleFixture,
    bool StateFixture,
    bool DuplicateFixture,
    bool MissingFixture)
{
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        string? se = null;
        string? gold = null;
        string? parts = null;
        string? output = null;
        string? fixturePart = null;
        string? blankAssembly = null;
        var nestedFixture = false;
        var singleFixture = false;
        var stateFixture = false;
        var duplicateFixture = false;
        var missingFixture = false;
        for (var index = 0; index < args.Count; index++)
        {
            string Next() => index + 1 < args.Count
                ? args[++index]
                : throw new ArgumentException($"{args[index]} 缺少取值。");
            switch (args[index])
            {
                case "--se-asm": se = Next(); break;
                case "--gold-sw": gold = Next(); break;
                case "--sw-parts": parts = Next(); break;
                case "--build-output": output = Next(); break;
                case "--fixture-part": fixturePart = Next(); break;
                case "--blank-sw": blankAssembly = Next(); break;
                case "--fixture-nested": nestedFixture = true; break;
                case "--fixture-single": singleFixture = true; break;
                case "--fixture-states": stateFixture = true; break;
                case "--fixture-duplicates": duplicateFixture = true; break;
                case "--fixture-missing": missingFixture = true; break;
                default: throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(se) || !Path.IsPathFullyQualified(se))
            throw new ArgumentException($"SE 装配路径不是绝对路径：{se}");
        if (fixturePart is null && !File.Exists(se))
            throw new ArgumentException($"SE 装配不存在：{se}");
        if (gold is not null && (!Path.IsPathFullyQualified(gold) || !File.Exists(gold)))
            throw new ArgumentException($"金标准装配不存在或不是绝对路径：{gold}");
        if (fixturePart is not null && (!Path.IsPathFullyQualified(fixturePart) || !File.Exists(fixturePart)))
            throw new ArgumentException($"fixture 零件不存在或不是绝对路径：{fixturePart}");
        if (blankAssembly is not null && (!Path.IsPathFullyQualified(blankAssembly) || !File.Exists(blankAssembly)))
            throw new ArgumentException($"空 SLDASM 不存在或不是绝对路径：{blankAssembly}");
        if (string.IsNullOrWhiteSpace(parts) || !Path.IsPathFullyQualified(parts) || !Directory.Exists(parts))
            throw new ArgumentException($"SLDPRT 目录不存在：{parts}");
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException($"输出不是绝对路径：{output}");
        if (new[] { nestedFixture, singleFixture, stateFixture, duplicateFixture, missingFixture }.Count(value => value) > 1)
            throw new ArgumentException("fixture 模式只能选择一个。");
        return new ProbeOptions(
            Path.GetFullPath(se),
            gold is null ? null : Path.GetFullPath(gold),
            Path.GetFullPath(parts),
            Path.GetFullPath(output),
            fixturePart is null ? null : Path.GetFullPath(fixturePart),
            blankAssembly is null ? null : Path.GetFullPath(blankAssembly),
            nestedFixture,
            singleFixture,
            stateFixture,
            duplicateFixture,
            missingFixture);
    }
}

internal sealed class ProbeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string SourceAssembly { get; set; } = string.Empty;
    public string? GoldAssembly { get; set; }
    public string BuildOutput { get; set; } = string.Empty;
    public List<OccurrenceFact> SolidEdgeOccurrences { get; set; } = [];
    public List<ComponentFact> GoldComponents { get; set; } = [];
    public MatrixMapping MatrixMapping { get; set; } = new("unresolved", false, false, double.MaxValue, double.MaxValue, double.MaxValue);
    public List<ComponentFact> BuiltComponents { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public int[] EdgeProcessesAfter { get; set; } = [];
    public int[] SolidWorksProcessesAfter { get; set; } = [];
}

internal sealed record OccurrenceFact(
    string Id,
    string? ParentId,
    string SourcePath,
    bool IsSubAssembly,
    bool IsVisible,
    double[] RawTransform,
    double[] ComposedTransform,
    double[]? Range);

internal sealed record ComponentFact(string Name, string Path, double[] Transform, bool IsFixed, double[]? Box);

internal sealed record MatrixMapping(
    string Name,
    bool TransposeRotation,
    bool UseComposed,
    double MaxRotationDeviation,
    double MaxTranslationDeviationMeters,
    double Score);
