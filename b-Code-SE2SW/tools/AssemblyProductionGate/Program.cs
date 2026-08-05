using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SE2SW;
using SE2SW.Contracts;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AssemblyProductionGate;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--set-template", StringComparison.OrdinalIgnoreCase))
            return SetAssemblyTemplate(args[1]);
        if (args.Length == 1 && string.Equals(args[0], "--cleanup-gate-documents", StringComparison.OrdinalIgnoreCase))
            return CleanupGateDocuments();
        if (args.Length == 2 && string.Equals(args[0], "--audit-assemblies", StringComparison.OrdinalIgnoreCase))
            return AuditAssemblies(args[1]);

        GateOptions options;
        try
        {
            options = GateOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("用法：AssemblyProductionGate --se-asm <绝对.asm> --worker <SE2SW.Worker.exe> --assembly-template <物理.asmdot>；或追加 --reuse-existing 测试安全重试与模板回退");
            return 2;
        }

        var result = new GateResult
        {
            SourceAssembly = options.SourceAssembly,
            WorkerPath = options.WorkerPath,
            PhysicalAssemblyTemplate = options.PhysicalAssemblyTemplate ?? string.Empty,
        };
        try
        {
            Run(options, result);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.Success ? 0 : 1;
    }

    private static int SetAssemblyTemplate(string template)
    {
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            var previous = application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            if (!application.SetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                    template))
            {
                throw new InvalidOperationException("SolidWorks 拒绝设置默认装配模板。");
            }
            Console.WriteLine($"SolidWorks 装配模板：{previous} -> {template}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                var owned = GetPids("SLDWORKS").Except(before).ToArray();
                if (owned.Length > 0)
                {
                    try { application.ExitApp(); } catch { }
                }
                Release(application);
            }
        }
    }

    private static int CleanupGateDocuments()
    {
        var tempGateRoot = Path.GetFullPath(Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "SE2SW-V300"));
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            ModelDoc2? document = application.GetFirstDocument() as ModelDoc2;
            var closed = 0;
            while (document is not null)
            {
                ModelDoc2? next = null;
                try
                {
                    next = document.GetNext() as ModelDoc2;
                    var path = document.GetPathName();
                    if (!string.IsNullOrWhiteSpace(path)
                        && Path.GetFullPath(path).StartsWith(tempGateRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var title = document.GetTitle();
                        application.CloseDoc(title);
                        Console.WriteLine($"已关闭门禁文档：{path}");
                        closed++;
                    }
                }
                finally
                {
                    Release(document);
                }
                document = next;
            }
            Console.WriteLine($"门禁文档清理完成：{closed} 个。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Release(application);
        }
    }

    /// <summary>
    /// 只读重开一组已生成的装配，并输出 SW 实际保存的配合特征树。
    /// 这是关系重建的独立验收路径：不采信 Worker 的自报结果，也不需要显示 GUI。
    /// </summary>
    private static int AuditAssemblies(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"装配目录不存在：{root}");
            return 2;
        }

        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
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

            var facts = new List<AssemblyAuditFact>();
            foreach (var path in Directory.EnumerateFiles(root, "*.SLDASM").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                var (model, assembly) = OpenAssembly(application, path);
                try
                {
                    var components = (assembly.GetComponents(true) as Array ?? Array.Empty<object>())
                        .Cast<object>().OfType<Component2>().ToArray();
                    facts.Add(new AssemblyAuditFact(
                        path,
                        components.Length,
                        components.Count(component => component.IsFixed()),
                        ReadMateFeatures(model)));
                }
                finally
                {
                    CloseModel(application, model);
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(facts, JsonOptions));
            if (owned.Length > 0)
            {
                try { application.ExitApp(); } catch { }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Release(application);
        }
    }

    private static IReadOnlyList<string> ReadMateFeatures(ModelDoc2 model)
    {
        var mates = new List<string>();
        Feature? feature = model.FirstFeature() as Feature;
        while (feature is not null)
        {
            if (string.Equals(feature.GetTypeName2(), "MateGroup", StringComparison.OrdinalIgnoreCase))
            {
                Feature? mate = feature.GetFirstSubFeature() as Feature;
                while (mate is not null)
                {
                    mates.Add($"{mate.Name}:{mate.GetTypeName2()}");
                    mate = mate.GetNextSubFeature() as Feature;
                }
            }
            feature = feature.GetNextFeature() as Feature;
        }

        return mates;
    }

    private static void Run(GateOptions options, GateResult result)
    {
        ValidateInput(options);
        var sourceDirectory = Path.GetDirectoryName(options.SourceAssembly)!;
        var xtDirectory = Path.Combine(sourceDirectory, "XT");
        var swDirectory = Path.Combine(sourceDirectory, "SW");
        if (File.Exists(xtDirectory) || File.Exists(swDirectory))
            throw new IOException("XT 或 SW 被同名文件占用。");
        if (!options.ReuseExisting && (Directory.Exists(xtDirectory) || Directory.Exists(swDirectory)))
            throw new IOException("真机门禁拒绝复用已有 XT/SW，请使用干净 fixture，或显式传入 --reuse-existing。");
        if (options.ReuseExisting && (!Directory.Exists(xtDirectory) || !Directory.Exists(swDirectory)))
            throw new DirectoryNotFoundException("安全重试门禁要求 XT/SW 分层目录已经存在。");

        var edgeBefore = GetPids("Edge");
        var swBefore = GetPids("SLDWORKS");
        var gateDirectory = Directory.CreateTempSubdirectory("SE2SW-V300-ProductionGate-").FullName;
        result.GateDirectory = gateDirectory;
        var probeResultPath = Path.Combine(gateDirectory, "assembly-probe-result.json");
        var probeRequest = new AssemblyProbeRequest(
            "gate-probe-" + Guid.NewGuid().ToString("N"),
            options.SourceAssembly,
            probeResultPath);
        var probeRun = RunWorker(options.WorkerPath, "--probe-assembly", probeRequest.BatchId, probeRequest, gateDirectory);
        result.WorkerEvents.AddRange(probeRun.Events);
        if (probeRun.ExitCode is not (0 or 1) || !File.Exists(probeResultPath))
            throw new InvalidOperationException($"生产 Worker 装配探查失败，exit={probeRun.ExitCode}：{probeRun.StandardError}");
        var probe = JsonSerializer.Deserialize<AssemblyProbeResult>(File.ReadAllText(probeResultPath), JsonOptions)
            ?? throw new InvalidDataException("生产 Worker 装配探查结果为空。");
        result.OccurrenceCount = probe.Occurrences.Count;
        result.UniquePartCount = probe.UniquePartPaths.Count;
        result.SuppressedCount = probe.SuppressedCount;
        result.UnresolvedCount = probe.UnresolvedCount;
        if (probe.UnresolvedCount != 0)
            throw new InvalidDataException($"装配有 {probe.UnresolvedCount} 个未解析引用。");

        var partPaths = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Where(path => string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateNames = partPaths
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicateNames.Length > 0)
            throw new InvalidDataException("门禁 fixture 存在同名不同路径零件。");
        if (partPaths.Length == 0)
            throw new InvalidDataException("门禁 fixture 没有可转换 .par 零件。");
        if (!options.ReuseExisting && (Directory.Exists(xtDirectory) || Directory.Exists(swDirectory)))
            throw new InvalidOperationException("探查阶段错误创建了 XT/SW 目录。");

        var sourceHashes = partPaths.Append(options.SourceAssembly)
            .ToDictionary(path => path, ComputeSha256, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(xtDirectory);
        Directory.CreateDirectory(swDirectory);
        var jobs = partPaths.Select((path, index) => new ConversionJob(
            "gate-part-" + (index + 1),
            path,
            Path.Combine(xtDirectory, Path.GetFileNameWithoutExtension(path) + ".x_t"),
            Path.Combine(swDirectory, Path.GetFileNameWithoutExtension(path) + ".SLDPRT"))).ToArray();
        var assemblyOutput = Path.Combine(
            swDirectory,
            Path.GetFileNameWithoutExtension(options.SourceAssembly) + ".SLDASM");
        // --reuse-existing 档专门验证安全重试：V3.3 起已有装配产物由 AssemblyNodeReusePlanner
        // 按"比全部递归依赖都新"核验后复用，因此这一档必须允许它存在，否则复用路径永远跑不到。
        if (!options.ReuseExisting && File.Exists(assemblyOutput))
            throw new IOException($"装配输出已经存在，门禁不会覆盖：{assemblyOutput}");
        var existingPartOutputHashes = options.ReuseExisting
            ? jobs.SelectMany(job => new[] { job.XtPath, job.SolidWorksPath })
                .Where(File.Exists)
                .ToDictionary(path => path, ComputeSha256, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // V3.3：按逐文档读数构图，走嵌套；读数缺失时退回 V3.0 的展平，门禁两条路都要能跑。
        var graph = probe.Documents is { Count: > 0 }
            ? AssemblyGraphBuilder.Build(
                options.SourceAssembly,
                probe.Documents,
                path => Path.Combine(swDirectory, Path.GetFileNameWithoutExtension(path) + ".SLDASM"))
            : null;
        if (graph is not null)
        {
            if (!graph.IsValid)
                throw new InvalidDataException(
                    "装配图不可用：环=" + string.Join("/", graph.Cycles)
                        + "，缺失=" + string.Join("/", graph.MissingDocuments));
            var verification = AssemblyTransformVerifier.Verify(probe);
            result.TransformSelfCheckCount = verification.CheckedCount;
            result.TransformSelfCheckDeviation = verification.MaxDeviation;
            if (!verification.IsConsistent)
                throw new InvalidDataException(
                    $"局部矩阵与世界矩阵不一致：超差 {verification.Mismatches.Count} 处、未匹配 {verification.Unmatched.Count} 处。");
            result.AssemblyNodeCount = graph.Nodes.Count;
            result.MaxDepth = graph.MaxDepth;
            var preexisting = graph.Nodes.Where(item => File.Exists(item.OutputPath)).ToArray();
            if (!options.ReuseExisting && preexisting.Length > 0)
                throw new IOException($"装配输出已经存在，门禁不会覆盖：{preexisting[0].OutputPath}");
            result.PreexistingAssemblyOutputs = preexisting.Select(item => item.OutputPath).ToList();
        }

        var assemblyRequest = new AssemblyBatchRequest(
            "gate-build-" + Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            options.SourceAssembly,
            assemblyOutput,
            jobs,
            probe.Occurrences,
            // --reuse-existing 的语义就是"在已有产物上重跑门禁"，此时必须允许覆盖：
            // 否则上一轮成功生成的顶层 SLDASM 会让下一轮在校验阶段就以
            // "装配输出已经存在" 失败，门禁变成一次性的。
            Overwrite: options.ReuseExisting,
            RecognizeFeatures: options.RecognizeFeatures,
            FullyDefineSketches: options.RecognizeFeatures,
            // V3.6.5：与产品默认一致——个别零件导不出也要生成缺件装配体。
            ContinueWhenPartFails: true,
            RebuildMates: options.RebuildMates,
            Nodes: graph?.Nodes,
            Relations: (probe.Documents ?? [])
                .SelectMany(document => document.Relations ?? [])
                .ToArray());

        ISldWorks? application = null;
        string? originalTemplate = null;
        int[] ownedSwPids = [];
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            ownedSwPids = GetPids("SLDWORKS").Except(swBefore).ToArray();
            if (ownedSwPids.Length > 0)
            {
                application.Visible = false;
                application.UserControl = false;
            }
            originalTemplate = application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            result.OriginalAssemblyTemplate = originalTemplate;
            if (options.PhysicalAssemblyTemplate is not null
                && !application.SetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                    options.PhysicalAssemblyTemplate))
            {
                throw new InvalidOperationException("无法临时设置 SolidWorks 默认装配模板。");
            }

            var buildRun = RunWorker(
                options.WorkerPath,
                "--assembly",
                assemblyRequest.BatchId,
                assemblyRequest,
                gateDirectory);
            result.WorkerEvents.AddRange(buildRun.Events);
            result.WorkerExitCode = buildRun.ExitCode;

            // 统计必须在退出码判定**之前**做：exit=1 是"完成但有零件失败"，
            // 装配体照样生成了，此时更需要把识别数据留痕。先算再判。
            var outcomes = buildRun.Events.Where(item => item.Feature is not null).Select(item => item.Feature!).ToArray();
            result.RecognizedPartCount = outcomes.Count(item => !item.DegradedToDumbSolid);
            result.DumbSolidPartCount = outcomes.Count(item => item.DegradedToDumbSolid);
            result.SketchTotal = outcomes.Sum(item => item.SketchTotal);
            result.SketchFullyDefined = outcomes.Sum(item => item.SketchFullyDefined);
            // exit=1 表示"有零件失败但批次已完成"。ContinueWhenPartFails 默认开启后
            // 这是正常的部分成功（本装配体固定有 1 个 .par 文件名编码损坏、导不出），
            // 装配体已经生成，不该判整轮失败。只有 exit≥2 才是真的没跑完。
            if (buildRun.ExitCode is not (0 or 1))
                throw new InvalidOperationException($"生产 Worker 装配构建失败，exit={buildRun.ExitCode}：{buildRun.StandardError}");

            if (options.RecognizeFeatures && options.MinimumRecognizedParts > 0)
            {
                if (result.RecognizedPartCount < options.MinimumRecognizedParts)
                {
                    throw new InvalidDataException(
                        $"特征识别低于基线：完全识别 {result.RecognizedPartCount} 个，"
                        + $"要求至少 {options.MinimumRecognizedParts} 个（降级 {result.DumbSolidPartCount} 个）。");
                }
                if (result.SketchTotal != result.SketchFullyDefined)
                {
                    throw new InvalidDataException(
                        $"草图未全部完全定义：{result.SketchFullyDefined}/{result.SketchTotal}。");
                }
            }
            VerifyAssembly(application, assemblyOutput, probe.Occurrences, jobs, result, graph, options.RebuildMates);
        }
        finally
        {
            if (application is not null)
            {
                // 泄漏断言：转换结束后 SolidWorks 里不该还留着我们打开的文档。
                // V3.3 的嵌套生成器漏了 CloseDocument，文档在会话里累积，
                // 直到后续节点撞上 swFileWithSameTitleAlreadyOpen 才暴露——
                // 而那时门禁早已"通过"过很多次。单次运行就能查出来的事，
                // 不该等到跑第二次才发现。
                result.LeakedDocuments = ListOpenDocuments(application);
                if (ownedSwPids.Length > 0)
                {
                    foreach (var leaked in result.LeakedDocuments)
                        TryCloseByPath(application, leaked);
                }

                if (options.PhysicalAssemblyTemplate is not null
                    && originalTemplate is not null
                    && !application.SetUserPreferenceStringValue(
                        (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                        originalTemplate))
                {
                    result.TemplateRestored = false;
                }
                else
                {
                    result.TemplateRestored = true;
                }
                if (ownedSwPids.Length > 0)
                {
                    try { application.ExitApp(); } catch { }
                }
                Release(application);
            }
            WaitForProcessesToExit(ownedSwPids, TimeSpan.FromSeconds(20));
        }

        if (!result.TemplateRestored)
            throw new InvalidOperationException("SolidWorks 默认装配模板未能恢复。");
        if (result.LeakedDocuments.Count > 0)
        {
            throw new InvalidOperationException(
                $"转换结束后仍有 {result.LeakedDocuments.Count} 个文档留在 SolidWorks 会话里，"
                + "这类泄漏会在后续节点上表现为组件插入失败、配合整块丢失："
                + string.Join("；", result.LeakedDocuments.Select(Path.GetFileName)));
        }
        foreach (var pair in sourceHashes)
        {
            if (!string.Equals(pair.Value, ComputeSha256(pair.Key), StringComparison.Ordinal))
                throw new InvalidDataException($"源 CAD 文件被修改：{pair.Key}");
        }
        result.SourceHashesUnchanged = true;
        // 源 CAD 文件必须逐字节不变（上面那条），这条不可放宽。
        //
        // 但"已有零件产物不得改写"只在**不开识别**时成立：V3.6.5 起，开启识别时
        // 已有 SLDPRT 一律重做——旧产物多半是识别失败留下的哑实体，不重做识别就永远不会发生。
        // 继续断言它不变，等于要求"开了识别也别真去识别"。
        if (options.RecognizeFeatures)
        {
            result.ExistingPartOutputsUnchanged = false;
        }
        else
        {
            foreach (var pair in existingPartOutputHashes)
            {
                if (!File.Exists(pair.Key)
                    || !string.Equals(pair.Value, ComputeSha256(pair.Key), StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"安全重试改写了已有零件产物：{pair.Key}");
                }
            }

            result.ExistingPartOutputsUnchanged = true;
        }
        result.OutputFiles = Directory.EnumerateFiles(xtDirectory)
            .Concat(Directory.EnumerateFiles(swDirectory))
            .Select(path => new OutputFact(path, new FileInfo(path).Length, ComputeSha256(path)))
            .ToList();
        if (result.OutputFiles.Any(file => file.Length <= 0))
            throw new InvalidDataException("门禁产物存在空文件。");

        WaitForUnexpectedProcesses(edgeBefore, swBefore, TimeSpan.FromSeconds(20));
        result.EdgeProcessesAfter = GetPids("Edge");
        result.SolidWorksProcessesAfter = GetPids("SLDWORKS");
        if (!result.EdgeProcessesAfter.SequenceEqual(edgeBefore)
            || !result.SolidWorksProcessesAfter.SequenceEqual(swBefore))
        {
            throw new InvalidOperationException("真机门禁结束后存在新增 CAD 进程。");
        }
    }

    /// <summary>
    /// V3.3 嵌套核验。两件事：
    ///   1. 每个装配文件的一级组件数 == 该节点的直接子项数（层级结构与 SE 同构）；
    ///   2. 顶层里每个叶组件的 <c>GetTotalTransform</c> 与 SE 的世界矩阵一致（&lt; 1e-6 m）。
    ///
    /// 第 2 条是 20 号文档 §6.1 判据 1 的端到端形式：由 SolidWorks 自己把各层变换复合出来，
    /// 不采信我们自己的复合算法——两条独立路径得到同一个数才算数。
    /// </summary>
    private static void VerifyNestedAssembly(
        ISldWorks application,
        IReadOnlyList<AssemblyOccurrence> occurrences,
        IReadOnlyList<ConversionJob> jobs,
        GateResult result,
        AssemblyGraph graph,
        bool allowMated)
    {
        result.AssemblyNodeCount = graph.Nodes.Count;
        result.MaxDepth = graph.MaxDepth;

        // 子项 → 产物路径。子装配查图里同源的节点，零件查同源的任务。
        string? ResolveChildOutput(AssemblyChild child)
        {
            var source = Path.GetFullPath(child.SourcePath);
            if (child.IsSubAssembly)
            {
                return graph.Nodes
                    .FirstOrDefault(item => string.Equals(
                        Path.GetFullPath(item.SourceAssemblyPath), source, StringComparison.OrdinalIgnoreCase))
                    ?.OutputPath;
            }

            return jobs
                .FirstOrDefault(item => string.Equals(
                    Path.GetFullPath(item.SourcePath), source, StringComparison.OrdinalIgnoreCase))
                ?.SolidWorksPath;
        }

        foreach (var node in graph.Nodes)
        {
            if (!File.Exists(node.OutputPath))
                throw new FileNotFoundException("装配节点没有产出文件。", node.OutputPath);
            var (model, assembly) = OpenAssembly(application, node.OutputPath);
            try
            {
                var top = (assembly.GetComponents(true) as Array ?? Array.Empty<object>())
                    .Cast<object>().OfType<Component2>().ToArray();

                // ContinueWhenPartFails 默认开启后，个别零件转换失败时会**如实生成缺件装配体**
                // （本装配体固定有 1 个 .par 文件名编码损坏、Solid Edge 导不出）。
                // 因此期望的一级组件数是"产物真的存在的子项"，不是子项总数——
                // 否则门禁等于在要求"绝不缺件"，和已定的产品行为直接矛盾。
                var expectedChildren = node.Children.Count(child => File.Exists(ResolveChildOutput(child) ?? string.Empty));
                var missing = node.Children.Count - expectedChildren;
                result.AssemblyNodes.Add(new NodeFact(
                    node.OutputPath, node.Depth, node.IsRoot, expectedChildren, top.Length));
                if (top.Length != expectedChildren)
                {
                    throw new InvalidDataException(
                        $"{Path.GetFileName(node.OutputPath)} 一级组件数不一致：{top.Length}/{expectedChildren}"
                        + (missing > 0 ? $"（另有 {missing} 个子项因零件未转换而缺件）" : string.Empty));
                }

                // V3.5 起组件可以被配合约束而不固定；--rebuild-mates 档只统计不强制，
                // 位置正确性由下面的 GetTotalTransform 全量比对兜底——那才是真验收。
                var fixedCount = top.Count(component => component.IsFixed());
                var reused = result.PreexistingAssemblyOutputs.Any(item =>
                    string.Equals(Path.GetFullPath(item), Path.GetFullPath(node.OutputPath), StringComparison.OrdinalIgnoreCase));
                result.FixedComponentCounts.Add(
                    $"{Path.GetFileName(node.OutputPath)}:{fixedCount}/{top.Length}{(reused ? "(复用)" : string.Empty)}");
                // 复用的子装配不是本轮生成的，它的固定状态由生成它的那一次决定——
                // 例如在 UI 里开着"重建装配关系"跑出来的装配，组件本来就是浮动+配合。
                // 拿本轮的固定预期去要求它，测的是历史产物而不是这次的代码。
                if (!allowMated && !reused && fixedCount != top.Length)
                    throw new InvalidDataException($"组件未固定：{node.OutputPath}（{fixedCount}/{top.Length}）");
            }
            finally
            {
                CloseModel(application, model);
            }
        }

        var root = graph.Nodes.Single(node => node.IsRoot);
        var (rootModel, rootAssembly) = OpenAssembly(application, root.OutputPath);
        try
        {
            var leaves = (rootAssembly.GetComponents(false) as Array ?? Array.Empty<object>())
                .Cast<object>().OfType<Component2>()
                .Where(component => (component.GetChildren() as Array)?.Length is null or 0)
                .Select(component => new ActualComponent(
                    component.GetPathName(),
                    ReadTotalTransform(component),
                    component.IsFixed()))
                .ToList();

            var bySource = jobs.ToDictionary(
                job => Path.GetFullPath(job.SourcePath),
                job => Path.GetFullPath(job.SolidWorksPath),
                StringComparer.OrdinalIgnoreCase);
            var candidates = occurrences
                .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
                .Where(item => bySource.ContainsKey(Path.GetFullPath(item.SourcePath)))
                .Select(item => new ExpectedComponent(
                    bySource[Path.GetFullPath(item.SourcePath)],
                    ToSolidWorksTransform(item.WorldTransform)))
                .ToList();
            // 与一级组件同一口径：零件没转换出来（缺件装配体）时，它的每个实例
            // 都不该计入期望。否则门禁在要求"绝不缺件"，与已定的产品行为矛盾。
            var expected = candidates.Where(item => File.Exists(item.Path)).ToList();
            var missingInstances = candidates.Count - expected.Count;

            result.ComponentExpected = expected.Count;
            result.ComponentActual = leaves.Count;
            if (leaves.Count != expected.Count)
            {
                throw new InvalidDataException(
                    $"顶层展开后的叶组件数不一致：{leaves.Count}/{expected.Count}"
                    + (missingInstances > 0 ? $"（另有 {missingInstances} 个实例因零件未转换而缺件）" : string.Empty));
            }

            var maxTranslation = 0d;
            var maxRotation = 0d;
            foreach (var item in expected)
            {
                var matched = leaves
                    .Where(actual => string.Equals(
                        Path.GetFullPath(actual.Path), item.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(actual => MaxTransformDeviation(item.Transform, actual.Transform))
                    .FirstOrDefault()
                    ?? throw new FileNotFoundException("嵌套装配里找不到对应的叶组件。", item.Path);
                leaves.Remove(matched);
                maxRotation = Math.Max(maxRotation, MaxRotationDeviation(item.Transform, matched.Transform));
                maxTranslation = Math.Max(maxTranslation, MaxTranslationDeviation(item.Transform, matched.Transform));
            }

            result.LeafComponentsVerified = expected.Count;
            result.ComponentFixed = expected.Count;  // 语义：位置核验通过的叶组件数
            result.MaxRotationDeviation = maxRotation;
            result.MaxTranslationDeviationMeters = maxTranslation;
            result.MaxTotalTransformDeviationMeters = maxTranslation;
            if (maxRotation >= 1e-9 || maxTranslation >= 1e-6)
            {
                throw new InvalidDataException(
                    $"逐层复合后的世界变换超差：rotation={maxRotation:G6}, translation={maxTranslation:G6}m");
            }
        }
        finally
        {
            CloseModel(application, rootModel);
        }
    }

    private static (ModelDoc2 Model, AssemblyDoc Assembly) OpenAssembly(ISldWorks application, string path)
    {
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            path,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            string.Empty,
            ref errors,
            ref warnings);
        if (model is null || errors != 0)
            throw new InvalidDataException($"独立重开 SLDASM 失败：{path}，errors={errors}, warnings={warnings}");
        return (model, (AssemblyDoc)model);
    }

    /// <summary>列出会话里仍然打开、且带磁盘路径的文档。未保存的临时文档没有路径，不计。</summary>
    private static List<string> ListOpenDocuments(ISldWorks application)
    {
        var open = new List<string>();
        ModelDoc2? document = null;
        try
        {
            document = application.GetFirstDocument() as ModelDoc2;
            while (document is not null)
            {
                ModelDoc2? next = null;
                try
                {
                    next = document.GetNext() as ModelDoc2;
                    var path = document.GetPathName();
                    if (!string.IsNullOrWhiteSpace(path))
                        open.Add(path);
                }
                finally
                {
                    Release(document);
                }
                document = next;
            }
        }
        catch
        {
            // 读不出来就不断言——门禁不该因为诊断本身失败而误报。
        }

        return open;
    }

    private static void TryCloseByPath(ISldWorks application, string path)
    {
        try { application.CloseDoc(Path.GetFileName(path)); } catch { }
    }

    private static void CloseModel(ISldWorks application, ModelDoc2? model)
    {
        if (model is null)
            return;
        try { application.CloseDoc(model.GetTitle()); } catch { }
    }

    /// <summary>相对顶层装配的总变换。由 SolidWorks 自己复合，不采信本项目的算法。</summary>
    private static double[] ReadTotalTransform(Component2 component)
    {
        var transform = component.GetTotalTransform(true)
            ?? throw new InvalidDataException($"组件没有 GetTotalTransform：{component.Name2}");
        return (transform.ArrayData as double[])
            ?? throw new InvalidDataException($"组件总变换数据无效：{component.Name2}");
    }

    private static void VerifyAssembly(
        ISldWorks application,
        string outputPath,
        IReadOnlyList<AssemblyOccurrence> occurrences,
        IReadOnlyList<ConversionJob> jobs,
        GateResult result,
        AssemblyGraph? graph = null,
        bool allowMated = false)
    {
        if (graph is not null)
        {
            VerifyNestedAssembly(application, occurrences, jobs, result, graph, allowMated);
            return;
        }

        var errors = 0;
        var warnings = 0;
        ModelDoc2? model = null;
        try
        {
            model = application.OpenDoc6(
                outputPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref errors,
                ref warnings);
            if (model is null || errors != 0)
                throw new InvalidDataException($"独立重开 SLDASM 失败：errors={errors}, warnings={warnings}");
            var assembly = (AssemblyDoc)model;
            var rawComponents = assembly.GetComponents(false) as Array ?? Array.Empty<object>();
            var actual = rawComponents.Cast<object>().OfType<Component2>().Select(ReadComponent).ToList();
            var bySource = jobs.ToDictionary(
                job => Path.GetFullPath(job.SourcePath),
                job => Path.GetFullPath(job.SolidWorksPath),
                StringComparer.OrdinalIgnoreCase);
            var expected = occurrences
                .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
                .Where(item => bySource.ContainsKey(Path.GetFullPath(item.SourcePath)))
                .Select(item => new ExpectedComponent(
                    bySource[Path.GetFullPath(item.SourcePath)],
                    ToSolidWorksTransform(item.WorldTransform)))
                .ToList();
            result.ComponentExpected = expected.Count;
            result.ComponentActual = actual.Count;
            if (actual.Count != expected.Count)
                throw new InvalidDataException($"重开后组件数不一致：{actual.Count}/{expected.Count}");

            var maxTranslation = 0d;
            var maxRotation = 0d;
            foreach (var expectedComponent in expected)
            {
                var candidates = actual
                    .Where(item => string.Equals(
                        Path.GetFullPath(item.Path),
                        expectedComponent.Path,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => MaxTransformDeviation(expectedComponent.Transform, item.Transform))
                    .ToArray();
                if (candidates.Length == 0)
                    throw new FileNotFoundException("SLDASM 组件引用未解析。", expectedComponent.Path);
                var matched = candidates[0];
                actual.Remove(matched);
                if (!matched.IsFixed)
                    throw new InvalidDataException($"组件未固定：{matched.Path}");
                maxRotation = Math.Max(maxRotation, MaxRotationDeviation(expectedComponent.Transform, matched.Transform));
                maxTranslation = Math.Max(maxTranslation, MaxTranslationDeviation(expectedComponent.Transform, matched.Transform));
            }
            result.ComponentFixed = expected.Count;
            result.MaxRotationDeviation = maxRotation;
            result.MaxTranslationDeviationMeters = maxTranslation;
            if (maxRotation >= 1e-9 || maxTranslation >= 1e-6)
                throw new InvalidDataException($"重开后组件变换超差：rotation={maxRotation:G6}, translation={maxTranslation:G6}m");
        }
        finally
        {
            if (model is not null)
            {
                try { application.CloseDoc(model.GetTitle()); } catch { }
                Release(model);
            }
        }
    }

    private static ActualComponent ReadComponent(Component2 component)
    {
        MathTransform? transform = null;
        try
        {
            transform = component.Transform2;
            if (transform?.ArrayData is not Array values || values.Length != 16)
                throw new InvalidDataException($"组件变换无效：{component.Name2}");
            var path = component.GetPathName();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("组件引用未解析。", path);
            return new ActualComponent(
                Path.GetFullPath(path),
                values.Cast<object>().Select(Convert.ToDouble).ToArray(),
                component.IsFixed());
        }
        finally
        {
            Release(transform);
            Release(component);
        }
    }

    private static WorkerRun RunWorker<TRequest>(
        string workerPath,
        string verb,
        string batchId,
        TRequest request,
        string gateDirectory)
    {
        var requestPath = Path.Combine(gateDirectory, batchId + ".json");
        var cancellationPath = Path.Combine(gateDirectory, batchId + ".cancel");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(verb);
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add("--cancel");
        process.StartInfo.ArgumentList.Add(cancellationPath);
        if (!process.Start())
            throw new InvalidOperationException("无法启动生产 Worker。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        var events = stdout.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                try { return JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions); }
                catch { return null; }
            })
            .Where(item => item is not null)
            .Cast<WorkerEvent>()
            .ToArray();
        return new WorkerRun(process.ExitCode, events, stderr.Result.Trim());
    }

    private static double[] ToSolidWorksTransform(IReadOnlyList<double> source) =>
    [
        source[0], source[1], source[2],
        source[4], source[5], source[6],
        source[8], source[9], source[10],
        source[12], source[13], source[14],
        1, 0, 0, 0,
    ];

    private static double MaxRotationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Enumerable.Range(0, 9).Max(index => Math.Abs(expected[index] - actual[index]));

    private static double MaxTranslationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Enumerable.Range(9, 3).Max(index => Math.Abs(expected[index] - actual[index]));

    private static double MaxTransformDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
        => Math.Max(MaxRotationDeviation(expected, actual), MaxTranslationDeviation(expected, actual));

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateInput(GateOptions options)
    {
        if (!File.Exists(options.SourceAssembly)
            || !string.Equals(Path.GetExtension(options.SourceAssembly), ".asm", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("源装配体不存在。", options.SourceAssembly);
        if (!File.Exists(options.WorkerPath))
            throw new FileNotFoundException("生产 Worker 不存在。", options.WorkerPath);
        if (options.PhysicalAssemblyTemplate is not null
            && (!File.Exists(options.PhysicalAssemblyTemplate)
                || !string.Equals(Path.GetExtension(options.PhysicalAssemblyTemplate), ".asmdot", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException("物理装配模板不存在。", options.PhysicalAssemblyTemplate);
    }

    private static int[] GetPids(string processName)
        => Process.GetProcessesByName(processName)
            .Select(process => { using (process) return process.Id; })
            .OrderBy(id => id)
            .ToArray();

    private static void WaitForUnexpectedProcesses(int[] edgeBaseline, int[] swBaseline, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (GetPids("Edge").SequenceEqual(edgeBaseline) && GetPids("SLDWORKS").SequenceEqual(swBaseline))
                return;
            Thread.Sleep(200);
        }
    }

    private static void WaitForProcessesToExit(IEnumerable<int> processIds, TimeSpan timeout)
    {
        var ids = processIds.ToArray();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (ids.All(id => !IsAlive(id)))
                return;
            Thread.Sleep(200);
        }
    }

    private static bool IsAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}

internal sealed record GateOptions(
    string SourceAssembly,
    string WorkerPath,
    string? PhysicalAssemblyTemplate,
    bool ReuseExisting,
    bool RecognizeFeatures = false,
    bool RebuildMates = false,
    // 完全识别零件数的下限。0 表示不断言。识别档的实测基线见 41 号文档。
    int MinimumRecognizedParts = 0)
{
    public static GateOptions Parse(IReadOnlyList<string> args)
    {
        string? source = null;
        string? worker = null;
        string? template = null;
        var reuseExisting = false;
        var recognize = false;
        var rebuildMates = false;
        var minimumRecognized = 0;
        for (var index = 0; index < args.Count;)
        {
            if (string.Equals(args[index], "--reuse-existing", StringComparison.OrdinalIgnoreCase))
            {
                reuseExisting = true;
                index++;
                continue;
            }
            // V3.5 §5.1：在识别版几何上重跑门禁，用来裁决 A 路是否成立。
            if (string.Equals(args[index], "--recognize", StringComparison.OrdinalIgnoreCase))
            {
                recognize = true;
                index++;
                continue;
            }
            // V3.5 §6.2：配合重建档。
            if (string.Equals(args[index], "--rebuild-mates", StringComparison.OrdinalIgnoreCase))
            {
                rebuildMates = true;
                index++;
                continue;
            }
            if (index + 1 >= args.Count)
                throw new ArgumentException($"参数缺少值：{args[index]}");
            switch (args[index].ToLowerInvariant())
            {
                case "--min-recognized": minimumRecognized = int.Parse(args[index + 1]); break;
                case "--se-asm": source = args[index + 1]; break;
                case "--worker": worker = args[index + 1]; break;
                case "--assembly-template": template = args[index + 1]; break;
                default: throw new ArgumentException($"未知参数：{args[index]}");
            }
            index += 2;
        }
        if (!reuseExisting && string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("普通门禁缺少 --assembly-template；安全重试门禁请传入 --reuse-existing。");
        return new GateOptions(
            Path.GetFullPath(source ?? throw new ArgumentException("缺少 --se-asm")),
            Path.GetFullPath(worker ?? throw new ArgumentException("缺少 --worker")),
            string.IsNullOrWhiteSpace(template) ? null : Path.GetFullPath(template),
            reuseExisting,
            recognize,
            rebuildMates,
            minimumRecognized);
    }
}

internal sealed class GateResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string SourceAssembly { get; set; } = string.Empty;
    public string WorkerPath { get; set; } = string.Empty;
    public string PhysicalAssemblyTemplate { get; set; } = string.Empty;
    public bool ExistingPartOutputsUnchanged { get; set; }
    public string? OriginalAssemblyTemplate { get; set; }
    public string? GateDirectory { get; set; }
    public int OccurrenceCount { get; set; }
    public int UniquePartCount { get; set; }
    public int SuppressedCount { get; set; }
    public int UnresolvedCount { get; set; }
    public int WorkerExitCode { get; set; }
    public int RecognizedPartCount { get; set; }
    public int DumbSolidPartCount { get; set; }
    public int SketchTotal { get; set; }
    public int SketchFullyDefined { get; set; }
    public int ComponentExpected { get; set; }
    public int ComponentActual { get; set; }
    public int ComponentFixed { get; set; }
    public double MaxRotationDeviation { get; set; }
    public double MaxTranslationDeviationMeters { get; set; }
    public bool SourceHashesUnchanged { get; set; }
    public bool TemplateRestored { get; set; }
    // V3.3 嵌套。
    public int AssemblyNodeCount { get; set; }
    public int MaxDepth { get; set; } = 1;
    public int TransformSelfCheckCount { get; set; }
    public double TransformSelfCheckDeviation { get; set; }
    public List<NodeFact> AssemblyNodes { get; set; } = [];
    public List<string> FixedComponentCounts { get; set; } = [];
    public List<string> LeakedDocuments { get; set; } = [];
    public List<string> PreexistingAssemblyOutputs { get; set; } = [];
    public int LeafComponentsVerified { get; set; }
    public double MaxTotalTransformDeviationMeters { get; set; }
    public List<WorkerEvent> WorkerEvents { get; } = [];
    public List<OutputFact> OutputFiles { get; set; } = [];
    public int[] EdgeProcessesAfter { get; set; } = [];
    public int[] SolidWorksProcessesAfter { get; set; } = [];
}

internal sealed record WorkerRun(int ExitCode, IReadOnlyList<WorkerEvent> Events, string StandardError);
internal sealed record ExpectedComponent(string Path, double[] Transform);
internal sealed record ActualComponent(string Path, double[] Transform, bool IsFixed);
internal sealed record OutputFact(string Path, long Length, string Sha256);
internal sealed record NodeFact(string Output, int Depth, bool IsRoot, int ExpectedChildren, int ActualComponents);
internal sealed record AssemblyAuditFact(string Path, int ComponentCount, int FixedCount, IReadOnlyList<string> MateFeatures);
