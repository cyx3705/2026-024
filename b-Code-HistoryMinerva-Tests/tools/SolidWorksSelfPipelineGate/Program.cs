using System.Diagnostics;
// WPF 项目的隐式 using 不含 System.IO（避免与 System.Windows.Shapes.Path 撞名），必须显式引入。
using System.IO;
using System.Text;
using System.Text.Json;
using HistoryMinerva;
using HistoryMinerva.Contracts;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksSelfPipelineGate;

/// <summary>
/// V4.3 的真机门禁：把一个 SolidWorks 源装配跑完整条自整备管线，再逐项核验产物。
///
/// 走的是生产的同一条路——探查用 Worker 的 <c>--probe-assembly</c>，计划用生产的
/// <see cref="AssemblyPlanner"/>，构建用 Worker 的 <c>--assembly</c>。门禁只负责
/// 把结果**量出来**，不替生产做任何事：
///
///   1. 零件是否真的有了特征树（而不是仍旧一个导入体），草图完全定义了几个；
///   2. 产物装配里每个实例的位置姿态与源装配逐元素一致到什么程度；
///   3. 源文件是否一字未改。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>与 V3.0 起的位置判据同口径。</summary>
    private const double TranslationTolerance = 1e-6;
    private const double RotationTolerance = 1e-9;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? assemblyPath = null;
        string? workerPath = null;
        string? outputDirectory = null;
        var rebuildMates = false;
        var recognize = true;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--assembly" when index + 1 < args.Length: assemblyPath = Path.GetFullPath(args[++index]); break;
                case "--worker" when index + 1 < args.Length: workerPath = Path.GetFullPath(args[++index]); break;
                case "--out-dir" when index + 1 < args.Length: outputDirectory = Path.GetFullPath(args[++index]); break;
                case "--rebuild-mates": rebuildMates = true; break;
                case "--no-recognize": recognize = false; break;
            }
        }

        if (assemblyPath is null || workerPath is null)
        {
            Console.Error.WriteLine(
                "用法：SolidWorksSelfPipelineGate --assembly <绝对 .SLDASM> --worker <HistoryMinerva.Worker.exe> "
                + "[--out-dir <输出目录>] [--rebuild-mates] [--no-recognize]");
            return 2;
        }

        var report = new GateReport { SourceAssembly = assemblyPath, Worker = workerPath };
        try
        {
            Run(assemblyPath, workerPath, outputDirectory, recognize, rebuildMates, report);
            report.Success = report.Failures.Count == 0;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.Failures.Add($"{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
        }

        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Success ? 0 : 1;
    }

    private static void Run(
        string assemblyPath,
        string workerPath,
        string? outputDirectory,
        bool recognize,
        bool rebuildMates,
        GateReport report)
    {
        var runDirectory = Path.Combine(Path.GetTempPath(), "HistoryMinerva-SwGate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var sourceHashBefore = Sha256(assemblyPath);

        // ---- 1. 探查源装配 ----
        var probePath = Path.Combine(runDirectory, "probe.json");
        var batchId = Guid.NewGuid().ToString("N");
        RunWorker(
            workerPath,
            WorkerProtocol.AssemblyProbeVerb,
            new AssemblyProbeRequest(batchId, assemblyPath, probePath, ConversionSourceFormat.SolidWorks),
            runDirectory,
            report);
        var probe = JsonSerializer.Deserialize<AssemblyProbeResult>(File.ReadAllText(probePath), JsonOptions)
            ?? throw new InvalidDataException("探查结果为空。");
        report.OccurrenceCount = probe.Occurrences.Count;
        report.UniquePartCount = probe.UniquePartPaths.Count;
        report.DocumentCount = probe.Documents?.Count ?? 0;
        report.RelationCount = probe.Documents?.Sum(item => item.Relations?.Count ?? 0) ?? 0;
        report.ProbeWarnings.AddRange(probe.Warnings);

        // ---- 2. 生产计划器 ----
        outputDirectory ??= Path.Combine(Path.GetDirectoryName(assemblyPath)!, "SW");
        var plan = AssemblyPlanner.Create(probe, null, outputDirectory, ConversionSourceFormat.SolidWorks);
        report.PlanWarnings.AddRange(plan.Warnings);
        report.AssemblyOutputPath = plan.AssemblyOutputPath;
        foreach (var issue in plan.BlockingIssues)
            report.Failures.Add($"计划阻断：{issue.ErrorClass} {issue.Message}");
        if (!plan.CanConvert)
            return;

        report.NodeCount = plan.Nodes?.Count ?? 0;
        report.MaxDepth = plan.MaxDepth;
        ExternalOutputLayout.EnsureDirectories(plan.XtDirectory, plan.SolidWorksDirectory);

        // ---- 3. 转换 ----
        var jobs = plan.Parts
            .Select(part => new ConversionJob(
                Path.GetFileNameWithoutExtension(part.SourcePath),
                part.SourcePath,
                part.XtPath,
                part.SolidWorksPath))
            .ToArray();
        var request = new AssemblyBatchRequest(
            batchId,
            ConversionMode.External,
            plan.SourceAssemblyPath,
            plan.AssemblyOutputPath,
            jobs,
            probe.Occurrences,
            Overwrite: true,
            RecognizeFeatures: recognize,
            FullyDefineSketches: recognize,
            ContinueWhenPartFails: true,
            RebuildMates: rebuildMates,
            FeatureRecognitionTimeoutSeconds: FeatureRecognitionPolicy.DefaultTimeoutSeconds,
            Nodes: plan.Nodes,
            Relations: plan.Relations,
            SourceFormat: ConversionSourceFormat.SolidWorks);
        var stopwatch = Stopwatch.StartNew();
        RunWorker(workerPath, WorkerProtocol.AssemblyBuildVerb, request, runDirectory, report);
        report.ConversionMilliseconds = stopwatch.ElapsedMilliseconds;

        // ---- 4. 源文件一字未改 ----
        if (!string.Equals(sourceHashBefore, Sha256(assemblyPath), StringComparison.Ordinal))
            report.Failures.Add("源装配在转换后发生变化。");

        // ---- 5. 每个零件都必须先落到交付用 XT，禁止「已有特征树所以跳过」----
        VerifyXtHandoff(jobs, report);

        // ---- 6. 核验产物 ----
        if (!File.Exists(plan.AssemblyOutputPath))
        {
            report.Failures.Add($"未生成装配产物：{plan.AssemblyOutputPath}");
            return;
        }

        Verify(plan, jobs, probe, report);
    }

    /// <summary>
    /// 特征整备的第一刀：Worker 事件不得声称跳过整备，每个任务都必须留下非空的交付用 .x_t。
    /// 这一步不打开 SolidWorks，旧 Worker 漏掉 XT 时立刻失败。
    /// </summary>
    private static void VerifyXtHandoff(IReadOnlyList<ConversionJob> jobs, GateReport report)
    {
        foreach (var line in report.WorkerEvents)
        {
            if (line.Contains("跳过整备", StringComparison.Ordinal)
                || line.Contains("源零件已有特征树", StringComparison.Ordinal))
            {
                report.Failures.Add($"Worker 事件禁止跳过整备：{line}");
            }
        }

        foreach (var job in jobs)
        {
            var directoryName = Path.GetFileName(Path.GetDirectoryName(job.XtPath));
            if (!string.Equals(directoryName, ConversionPathLayout.XtDirectoryName, StringComparison.OrdinalIgnoreCase)
                || !ConversionPathLayout.HasExtension(job.XtPath, ConversionArtifactKind.Xt))
            {
                report.Failures.Add($"XT 路径必须是 {ConversionPathLayout.XtDirectoryName}/ 下的 .x_t：{job.XtPath}");
            }

            if (!File.Exists(job.XtPath))
            {
                report.Failures.Add($"未生成交付用 XT：{job.XtPath}");
                continue;
            }

            if (new FileInfo(job.XtPath).Length == 0)
            {
                report.Failures.Add($"交付用 XT 为空：{job.XtPath}");
                continue;
            }

            var head = ReadHead(job.XtPath, 512);
            if (head.IndexOf("FORMAT=text", StringComparison.OrdinalIgnoreCase) < 0
                && !head.TrimStart().StartsWith("**", StringComparison.Ordinal))
            {
                report.Failures.Add($"交付用 XT 不是 Parasolid 文本：{job.XtPath}");
            }
        }
    }

    private static string ReadHead(string path, int charCount)
    {
        using var reader = new StreamReader(path, Encoding.ASCII, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[charCount];
        var read = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    /// <summary>打开产物零件与产物装配，把"到底做成了什么"量出来。</summary>
    private static void Verify(
        AssemblyConversionPlan plan,
        IReadOnlyList<ConversionJob> jobs,
        AssemblyProbeResult probe,
        GateReport report)
    {
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");

            foreach (var job in jobs)
            {
                var part = InspectPart(application, job, report);
                report.Parts.Add(part);
            }

            InspectAssembly(application, plan, probe, report);
        }
        finally
        {
            if (application is not null)
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(application);
        }
    }

    private static PartReport InspectPart(ISldWorks application, ConversionJob job, GateReport report)
    {
        var result = new PartReport { Name = Path.GetFileName(job.SolidWorksPath) };
        if (!File.Exists(job.SolidWorksPath))
        {
            result.Diagnostic = "产物不存在";
            report.Failures.Add($"零件产物缺失：{job.SolidWorksPath}");
            return result;
        }

        result.Bytes = new FileInfo(job.SolidWorksPath).Length;
        result.IdenticalToSource = File.Exists(job.SourcePath)
            && string.Equals(Sha256(job.SourcePath), Sha256(job.SolidWorksPath), StringComparison.Ordinal);
        if (result.IdenticalToSource)
            report.Failures.Add($"零件产物与源文件相同，特征整备不得拷回源零件：{job.SolidWorksPath}");

        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            job.SolidWorksPath,
            (int)swDocumentTypes_e.swDocPART,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
            string.Empty,
            ref errors,
            ref warnings);
        if (model is null)
        {
            result.Diagnostic = $"打开失败 errors={errors} warnings={warnings}";
            report.Failures.Add($"零件产物打不开：{job.SolidWorksPath}（errors={errors}）");
            return result;
        }

        try
        {
            var feature = (Feature?)model.FirstFeature();
            while (feature is not null)
            {
                var typeName = SafeTypeName(feature);
                result.Features.Add($"{feature.Name}[{typeName}]");
                if (string.Equals(typeName, "ProfileFeature", StringComparison.Ordinal))
                {
                    result.SketchTotal++;
                    if (feature.GetSpecificFeature2() is Sketch sketch
                        && sketch.GetConstrainedStatus() == (int)swConstrainedStatus_e.swFullyConstrained)
                    {
                        result.SketchFullyDefined++;
                    }
                }
                if (IsImportedBody(typeName))
                    result.HasImportedBody = true;
                if (IsSolidFeature(typeName))
                    result.SolidFeatureCount++;

                feature = (Feature?)feature.GetNextFeature();
            }

            if (model is PartDoc part && part.GetBodies2((int)swBodyType_e.swSolidBody, false) is object[] bodies)
            {
                foreach (var item in bodies)
                {
                    if (item is Body2 body && body.GetMassProperties(1) is double[] { Length: >= 4 } mass)
                    {
                        result.Volume += mass[3];
                        result.FaceCount += body.GetFaceCount();
                    }
                }
            }
        }
        finally
        {
            application.CloseDoc(model.GetTitle());
        }

        return result;
    }

    private static void InspectAssembly(
        ISldWorks application,
        AssemblyConversionPlan plan,
        AssemblyProbeResult probe,
        GateReport report)
    {
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            plan.AssemblyOutputPath,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
            string.Empty,
            ref errors,
            ref warnings);
        if (model is null)
        {
            report.Failures.Add($"装配产物打不开：{plan.AssemblyOutputPath}（errors={errors}）");
            return;
        }

        try
        {
            report.OutputOpenErrors = errors;
            report.OutputOpenWarnings = warnings;
            var assembly = (AssemblyDoc)model;
            var components = assembly.GetComponents(false) as object[] ?? [];
            report.OutputComponentCount = components.Length;

            // 源侧世界矩阵按实例名索引。产物里的实例名由 AddComponent5 生成，
            // 与源名可能带不同的实例序号，因此按"零件文件名 + 位置"配对：
            // 先按文件名分组，组内按最近距离配对，再逐元素比对姿态。
            var expected = probe.Occurrences
                .Where(item => !item.IsSuppressed)
                .GroupBy(item => Path.GetFileNameWithoutExtension(item.SourcePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var maxTranslation = 0d;
            var maxRotation = 0d;
            var matched = 0;
            foreach (var item in components)
            {
                if (item is not Component2 component)
                    continue;
                var name = Path.GetFileNameWithoutExtension(component.GetPathName());
                if (string.IsNullOrWhiteSpace(name) || !expected.TryGetValue(name, out var candidates)
                    || candidates.Count == 0)
                {
                    report.Failures.Add($"产物里出现源装配没有的组件：{component.Name2}");
                    continue;
                }

                if (component.Transform2?.ArrayData is not double[] { Length: 16 } actual)
                {
                    report.Failures.Add($"读不到产物组件变换：{component.Name2}");
                    continue;
                }

                var best = candidates
                    .Select(candidate => (Candidate: candidate, Deviation: Deviation(candidate.WorldTransform, actual)))
                    .OrderBy(pair => pair.Deviation.Translation + pair.Deviation.Rotation)
                    .First();
                candidates.Remove(best.Candidate);
                matched++;
                maxTranslation = Math.Max(maxTranslation, best.Deviation.Translation);
                maxRotation = Math.Max(maxRotation, best.Deviation.Rotation);
                if (best.Deviation.Translation >= TranslationTolerance || best.Deviation.Rotation >= RotationTolerance)
                {
                    report.Failures.Add(
                        $"实例位置超差：{component.Name2} ← {best.Candidate.OccurrenceId}，"
                        + $"平移 {best.Deviation.Translation:G6} m、旋转 {best.Deviation.Rotation:G6}");
                }
            }

            report.MatchedComponentCount = matched;
            report.MaxTranslationDeviation = maxTranslation;
            report.MaxRotationDeviation = maxRotation;
            var leftover = expected.Sum(pair => pair.Value.Count);
            if (leftover > 0)
            {
                report.Failures.Add(
                    $"{leftover} 个源实例在产物里没有对应组件："
                    + string.Join("、", expected.SelectMany(pair => pair.Value).Take(8)
                        .Select(item => item.OccurrenceId)));
            }

            var mateCount = 0;
            var feature = (Feature?)model.FirstFeature();
            while (feature is not null)
            {
                if (string.Equals(SafeTypeName(feature), "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    var sub = (Feature?)feature.GetFirstSubFeature();
                    while (sub is not null)
                    {
                        mateCount++;
                        sub = (Feature?)sub.GetNextSubFeature();
                    }
                }

                feature = (Feature?)feature.GetNextFeature();
            }

            report.OutputMateCount = mateCount;
        }
        finally
        {
            application.CloseDoc(model.GetTitle());
        }
    }

    /// <summary>
    /// 源侧矩阵是契约布局（旋转 0,1,2/4,5,6/8,9,10、平移 12..14），
    /// 产物侧是 SolidWorks 布局（旋转 0..8、平移 9..11）。这里按各自布局取值比对。
    /// </summary>
    private static (double Translation, double Rotation) Deviation(
        IReadOnlyList<double> contractMatrix,
        IReadOnlyList<double> solidWorksMatrix)
    {
        double[] expected =
        [
            contractMatrix[0], contractMatrix[1], contractMatrix[2],
            contractMatrix[4], contractMatrix[5], contractMatrix[6],
            contractMatrix[8], contractMatrix[9], contractMatrix[10],
            contractMatrix[12], contractMatrix[13], contractMatrix[14],
        ];
        var rotation = 0d;
        for (var index = 0; index < 9; index++)
            rotation = Math.Max(rotation, Math.Abs(expected[index] - solidWorksMatrix[index]));
        var translation = 0d;
        for (var index = 9; index < 12; index++)
            translation = Math.Max(translation, Math.Abs(expected[index] - solidWorksMatrix[index]));
        return (translation, rotation);
    }

    private static void RunWorker<T>(
        string workerPath,
        string verb,
        T request,
        string runDirectory,
        GateReport report)
    {
        var requestPath = Path.Combine(runDirectory, $"{verb.TrimStart('-')}-{Guid.NewGuid():N}.json");
        var cancellationPath = Path.Combine(runDirectory, "cancel.signal");
        File.WriteAllText(
            requestPath,
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonOptions) { WriteIndented = false }));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.ArgumentList.Add(verb);
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add(WorkerProtocol.CancellationArgument);
        process.StartInfo.ArgumentList.Add(cancellationPath);

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
                return;
            try
            {
                var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(eventArgs.Data, JsonOptions);
                if (workerEvent is null)
                    return;
                var line = $"[{workerEvent.Stage}] {workerEvent.JobId} {workerEvent.Message}";
                Console.WriteLine(line);
                lock (report.WorkerEvents)
                    report.WorkerEvents.Add(line);
                if (workerEvent.IsError)
                {
                    lock (report.Failures)
                        report.Failures.Add($"Worker 报错：{workerEvent.Stage} {workerEvent.Message}");
                }
            }
            catch (JsonException)
            {
                Console.WriteLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
                return;
            Console.Error.WriteLine(eventArgs.Data);
            lock (report.Failures)
                report.Failures.Add("Worker stderr：" + eventArgs.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        if (process.ExitCode != 0)
            report.Failures.Add($"Worker {verb} 退出码 {process.ExitCode}。");
    }

    private static bool IsImportedBody(string typeName)
        => typeName.Equals("BaseBody", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("ImportedBody", StringComparison.OrdinalIgnoreCase)
           || typeName.Equals("Imported", StringComparison.OrdinalIgnoreCase);

    private static bool IsSolidFeature(string typeName)
        => typeName is "Extrusion" or "Revolution" or "Cut" or "CutRevolve" or "Fillet" or "Chamfer"
            or "HoleWzd" or "Rib" or "Boss" or "BossThin" or "CutThin" or "Draft" or "Shell";

    private static string SafeTypeName(Feature feature)
    {
        try { return feature.GetTypeName2(); }
        catch { return "(读取失败)"; }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private sealed class GateReport
    {
        public bool Success { get; set; }
        public string SourceAssembly { get; set; } = string.Empty;
        public string Worker { get; set; } = string.Empty;
        public string AssemblyOutputPath { get; set; } = string.Empty;
        public int OccurrenceCount { get; set; }
        public int UniquePartCount { get; set; }
        public int DocumentCount { get; set; }
        public int RelationCount { get; set; }
        public int NodeCount { get; set; }
        public int MaxDepth { get; set; }
        public long ConversionMilliseconds { get; set; }
        public int OutputComponentCount { get; set; }
        public int MatchedComponentCount { get; set; }
        public int OutputMateCount { get; set; }
        public int OutputOpenErrors { get; set; }
        public int OutputOpenWarnings { get; set; }
        public double MaxTranslationDeviation { get; set; }
        public double MaxRotationDeviation { get; set; }
        public List<PartReport> Parts { get; } = [];
        public List<string> ProbeWarnings { get; } = [];
        public List<string> PlanWarnings { get; } = [];
        public List<string> WorkerEvents { get; } = [];
        public List<string> Failures { get; } = [];
    }

    private sealed class PartReport
    {
        public string Name { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public bool IdenticalToSource { get; set; }
        public int SketchTotal { get; set; }
        public int SketchFullyDefined { get; set; }
        public int SolidFeatureCount { get; set; }
        public bool HasImportedBody { get; set; }
        public double Volume { get; set; }
        public int FaceCount { get; set; }
        public string? Diagnostic { get; set; }
        public List<string> Features { get; } = [];
    }
}
