using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RelationProbe;

/// <summary>
/// SE2SW V3.2 装配关系只读探针。
///
/// 两个动词共用一次 Solid Edge 会话：
///   阶段 A（默认）  读 .asm 的装配关系，dump 类型库成员表 + 实测值 + 几何引用；
///   阶段 B（--export-check）  把 .asm 当独立顶层文档整体导出为 .x_t，可选用 SolidWorks 核验体数与坐标系。
///
/// 不修改样件。默认在样件副本上操作，两种模式都对样件目录做 SHA-256 前后比对。
/// 设计依据见 docs/18-V3.2-装配关系探针与子装配XT链路.md。
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitNoRelations = 3;

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
            Console.Error.WriteLine(ProbeOptions.Usage);
            return ExitUsage;
        }

        var result = new ProbeResult
        {
            SampleDirectory = options.SampleDirectory,
            InPlace = options.InPlace,
        };

        var sampleHashBefore = Workspace.Hash(options.SampleDirectory);
        Dictionary<string, string> workingHashBefore = [];

        try
        {
            string workingSamples;
            if (options.InPlace)
            {
                workingSamples = options.SampleDirectory;
                result.Notes.Add("--in-place：直接打开样件原件。任何内容变化都会被哈希比对抓到。");
            }
            else
            {
                workingSamples = Path.Combine(options.WorkRoot, "samples");
                Workspace.CopyDirectory(options.SampleDirectory, workingSamples);
                result.Notes.Add($"已把样件整目录复制到 {workingSamples}；.asm 内是绝对路径引用，只复制装配文件等于没隔离。");
            }

            result.WorkingDirectory = workingSamples;
            workingHashBefore = Workspace.Hash(workingSamples);

            var targets = options.ResolveTargets(workingSamples);
            if (targets.Count == 0)
                throw new FileNotFoundException($"在 {workingSamples} 里没有找到任何 .asm。");
            result.Notes.Add("待探查装配体：" + string.Join("、", targets.Select(Path.GetFileName)));

            using (var session = SolidEdgeSession.Start(result.Notes))
            {
                foreach (var target in targets)
                    result.Documents.Add(SolidEdgeProbe.Probe(session, target, options.MaxDepth));

                if (options.ExportCheck)
                {
                    var exportDirectory = Path.Combine(options.WorkRoot, "export");
                    foreach (var target in targets)
                        result.ExportChecks.Add(AssemblyExport.Run(
                            session,
                            target,
                            exportDirectory,
                            options.VerifyWithSolidWorks,
                            result.Notes));
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }

        result.ChangedSampleFiles = Workspace.Compare(sampleHashBefore, Workspace.Hash(options.SampleDirectory));
        result.SampleHashVerified = result.ChangedSampleFiles.Count == 0;
        if (!options.InPlace && result.WorkingDirectory.Length > 0)
        {
            result.ChangedWorkingFiles = Workspace.Compare(workingHashBefore, Workspace.Hash(result.WorkingDirectory));
            result.WorkingCopyHashVerified = result.ChangedWorkingFiles.Count == 0;
        }
        else
        {
            result.WorkingCopyHashVerified = result.SampleHashVerified;
            result.ChangedWorkingFiles = result.ChangedSampleFiles;
        }

        result.EdgeProcessesAfter = SolidEdgeSession.GetPids();
        result.SolidWorksProcessesAfter = System.Diagnostics.Process
            .GetProcessesByName("SLDWORKS")
            .Select(process => process.Id)
            .OrderBy(id => id)
            .ToArray();

        var exitCode = Judge(result);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.Error.WriteLine($"[判定] {result.Verdict}（退出码 {exitCode}）");
        return exitCode;
    }

    /// <summary>
    /// 判定规则见 docs/18 §2.6：
    /// "样件没有显式关系"是结论不是故障，用**普遍性**与真故障区分——
    /// 全部文档的集合都能打开、Count 都读到 0，才是"无关系"；集合打不开是故障。
    /// </summary>
    private static int Judge(ProbeResult result)
    {
        if (result.Error is not null)
        {
            result.Verdict = "探针自身异常：" + result.Error;
            return ExitFailure;
        }

        if (!result.SampleHashVerified)
        {
            result.Verdict = "样件目录发生了变化，只读保证被破坏：" + string.Join("；", result.ChangedSampleFiles);
            return ExitFailure;
        }

        if (result.Documents.Count == 0)
        {
            result.Verdict = "没有探查到任何装配文档。";
            return ExitFailure;
        }

        var broken = result.Documents.Where(document => document.Error is not null).ToArray();
        if (broken.Length > 0)
        {
            result.Verdict = "文档打开或读取失败：" + string.Join("；",
                broken.Select(document => Path.GetFileName(document.Path) + " → " + document.Error));
            return ExitFailure;
        }

        var noEntry = result.Documents.Where(document => document.RelationCollection is null).ToArray();
        if (noEntry.Length > 0)
        {
            result.Verdict = "关系集合入口取不到：" + string.Join("；",
                noEntry.Select(document => Path.GetFileName(document.Path)
                    + $"（候选成员 {document.RelationEntryPoints.Count} 个）"));
            return ExitFailure;
        }

        var untyped = result.Documents
            .SelectMany(document => document.Relations)
            .Count(relation => relation.InterfaceName is "<无类型信息>" or "");
        if (untyped > 0)
        {
            result.Verdict = $"有 {untyped} 条关系读不出接口类型，成员表不可信。";
            return ExitFailure;
        }

        var exportErrors = result.ExportChecks.Where(check => check.Error is not null).ToArray();
        if (exportErrors.Length > 0)
        {
            result.Verdict = "装配级导出失败：" + string.Join("；",
                exportErrors.Select(check => Path.GetFileName(check.SourceAssembly) + " → " + check.Error));
            return ExitFailure;
        }

        // 体数不符不判失败——它是一条要写进实测报告的事实（例如隐藏件是否进入产物），
        // 由 §6.1.6 的生产门禁去卡，不由探针替它下结论。
        var mismatched = result.ExportChecks
            .Where(check => check.OutputPath is not null && !check.BodyCountMatches)
            .Select(check => $"{Path.GetFileName(check.SourceAssembly)} 体数 {check.SolidWorksBodyCount} ≠ 叶零件 {check.LeafOccurrenceCount}")
            .ToArray();

        var summary = string.Join("；", result.Documents.Select(document =>
            $"{Path.GetFileName(document.Path)} 关系 {document.RelationCount} 条"));
        if (mismatched.Length > 0)
            summary += "；体数差异：" + string.Join("、", mismatched);

        if (result.Documents.All(document => document.RelationCount == 0))
        {
            result.Verdict = "全部文档的关系集合均可正常打开且 Count 为 0——样件靠拖放定位、没有显式关系。"
                + "这是结论不是故障：这类装配在 V3.5 里走固定。" + summary;
            return ExitNoRelations;
        }

        result.Verdict = "关系采集成功。" + summary;
        return ExitSuccess;
    }
}

internal sealed class ProbeOptions
{
    public const string Usage =
        """
        用法: RelationProbe --samples <样件目录> [选项]
          --samples <目录>     必填。含 .asm 与 .par 的样件目录。
          --asm <文件名>       可重复。默认探查目录下全部 .asm。
          --work-dir <目录>    副本与导出产物的落位，默认 %TEMP%\SE2SW-RelationProbe-<时间戳>。
          --in-place           直接读样件原件，不复制。仍做哈希前后比对。
          --export-check       追加阶段 B：把每个 .asm 整体导出为 .x_t。
          --sw-verify          阶段 B 用 SolidWorks 导入产物核验体数与坐标系。
          --max-depth <n>      COM 对象递归展开深度，默认 3（关系→几何元素→曲面）。
        退出码: 0 成功 / 1 故障 / 2 用法错误 / 3 样件无显式关系（结论，非故障）
        """;

    public string SampleDirectory { get; private init; } = string.Empty;

    public string WorkRoot { get; private init; } = string.Empty;

    public List<string> AssemblyNames { get; private init; } = [];

    public bool InPlace { get; private init; }

    public bool ExportCheck { get; private init; }

    public bool VerifyWithSolidWorks { get; private init; }

    public int MaxDepth { get; private init; } = 3;

    public static ProbeOptions Parse(string[] args)
    {
        string? samples = null;
        string? workRoot = null;
        var names = new List<string>();
        var inPlace = false;
        var exportCheck = false;
        var swVerify = false;
        var maxDepth = 3;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--samples":
                    samples = Next(args, ref index);
                    break;
                case "--asm":
                    names.Add(Next(args, ref index));
                    break;
                case "--work-dir":
                    workRoot = Next(args, ref index);
                    break;
                case "--in-place":
                    inPlace = true;
                    break;
                case "--export-check":
                    exportCheck = true;
                    break;
                case "--sw-verify":
                    swVerify = true;
                    break;
                case "--max-depth":
                    maxDepth = int.Parse(Next(args, ref index));
                    break;
                default:
                    throw new ArgumentException($"无法识别的参数：{args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(samples))
            throw new ArgumentException("--samples 必填。");
        if (!Directory.Exists(samples))
            throw new DirectoryNotFoundException($"样件目录不存在：{samples}");
        if (maxDepth is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "--max-depth 取值范围 1..6。");
        if (swVerify && !exportCheck)
            throw new ArgumentException("--sw-verify 需要同时指定 --export-check。");

        return new ProbeOptions
        {
            SampleDirectory = Path.GetFullPath(samples),
            WorkRoot = Path.GetFullPath(workRoot ?? Path.Combine(
                Path.GetTempPath(),
                "SE2SW-RelationProbe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))),
            AssemblyNames = names,
            InPlace = inPlace,
            ExportCheck = exportCheck,
            VerifyWithSolidWorks = swVerify,
            MaxDepth = maxDepth,
        };
    }

    public List<string> ResolveTargets(string workingSamples)
    {
        if (AssemblyNames.Count == 0)
        {
            return Directory
                .EnumerateFiles(workingSamples, "*.asm", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        var targets = new List<string>();
        foreach (var name in AssemblyNames)
        {
            var candidate = Path.Combine(workingSamples, Path.GetFileName(name));
            if (!File.Exists(candidate))
                throw new FileNotFoundException($"指定的装配体不在样件目录里：{name}");
            targets.Add(candidate);
        }

        return targets;
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{args[index]} 缺少取值。");
        return args[++index];
    }
}
