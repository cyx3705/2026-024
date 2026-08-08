using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SE2SW.Contracts;

namespace FeatureWorksBatchSmoke;

/// <summary>
/// Runs the production part Worker once per supplied part in isolated temporary directories.
/// The test is intentionally non-interactive: it diagnoses whether an individual part can
/// complete the SE -> XT -> SW -> FeatureWorks pipeline without relying on the UI session.
/// </summary>
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
        Console.OutputEncoding = Encoding.UTF8;
        FeatureWorksBatchSmokeOptions options;
        try
        {
            options = FeatureWorksBatchSmokeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(
                "Usage: FeatureWorksBatchSmoke --worker <SE2SW.Worker.exe> --part <part.par> [--part <part.par> ...] [--single-batch] [--require-healthy-recognition] [--timeout-seconds <30-600>] [--keep]");
            return 2;
        }

        var report = new FeatureWorksBatchSmokeReport
        {
            WorkerPath = options.WorkerPath,
            KeepArtifacts = options.KeepArtifacts,
            SingleBatch = options.SingleBatch,
            RequireHealthyRecognition = options.RequireHealthyRecognition,
            TimeoutSeconds = (int)options.Timeout.TotalSeconds,
        };

        try
        {
            Run(options, report);
            report.Success = report.Parts.All(part => part.Success);
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.Error = ex.Message;
            report.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            if (!options.KeepArtifacts && report.Success && report.RunDirectory is not null)
            {
                try
                {
                    Directory.Delete(report.RunDirectory, recursive: true);
                    report.ArtifactsDeleted = true;
                }
                catch (Exception ex)
                {
                    report.Success = false;
                    report.Error = "Smoke passed but temporary artifact cleanup failed: " + ex.Message;
                }
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Success ? 0 : 1;
    }

    private static void Run(FeatureWorksBatchSmokeOptions options, FeatureWorksBatchSmokeReport report)
    {
        var edgeBaseline = GetProcessIds("Edge");
        var solidWorksBaseline = GetProcessIds("SLDWORKS");
        report.EdgeProcessesBefore = edgeBaseline;
        report.SolidWorksProcessesBefore = solidWorksBaseline;

        report.RunDirectory = Directory.CreateTempSubdirectory("SE2SW-FeatureWorksBatchSmoke-").FullName;
        if (options.SingleBatch)
        {
            RunSingleBatch(options, report);
            CompleteProcessCheck(report, edgeBaseline, solidWorksBaseline);
            return;
        }

        for (var index = 0; index < options.PartPaths.Count; index++)
        {
            var inputPath = options.PartPaths[index];
            var partDirectory = Path.Combine(report.RunDirectory, $"part-{index + 1:D2}");
            var sourceDirectory = Path.Combine(partDirectory, "source");
            var xtDirectory = Path.Combine(partDirectory, "XT");
            var solidWorksDirectory = Path.Combine(partDirectory, "SW");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(xtDirectory);
            Directory.CreateDirectory(solidWorksDirectory);

            var part = new FeatureWorksBatchSmokePart
            {
                SourcePath = inputPath,
                SourceHashBefore = ComputeSha256(inputPath),
                WorkDirectory = partDirectory,
            };
            report.Parts.Add(part);

            var copiedSource = Path.Combine(sourceDirectory, Path.GetFileName(inputPath));
            File.Copy(inputPath, copiedSource, overwrite: false);
            part.CopiedSourcePath = copiedSource;
            part.CopiedSourceHash = ComputeSha256(copiedSource);
            if (!string.Equals(part.SourceHashBefore, part.CopiedSourceHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Copied fixture hash differs from source: {inputPath}");

            var name = Path.GetFileNameWithoutExtension(inputPath);
            var jobId = $"featureworks-part-{index + 1:D2}";
            var request = new BatchRequest(
                "featureworks-smoke-" + Guid.NewGuid().ToString("N"),
                ConversionMode.External,
                [new ConversionJob(
                    jobId,
                    copiedSource,
                    Path.Combine(xtDirectory, name + ".x_t"),
                    Path.Combine(solidWorksDirectory, name + ".SLDPRT"))],
                Overwrite: false,
                RecognizeFeatures: true,
                FullyDefineSketches: true,
                FeatureRecognitionTimeoutSeconds: (int)options.Timeout.TotalSeconds,
                ContinueWhenRecognitionFails: true);

            var run = RunWorker(options.WorkerPath, request, partDirectory, options.Timeout);
            part.WorkerExitCode = run.ExitCode;
            part.WorkerTimedOut = run.TimedOut;
            part.StandardError = run.StandardError;
            part.UnparsedStandardOutput = run.UnparsedStandardOutput.ToList();
            part.Events = run.Events.ToList();
            part.FeatureRecognitionAttempted = run.Events.Any(item =>
                string.Equals(item.JobId, jobId, StringComparison.Ordinal)
                && item.Stage == ConversionStage.FeatureRecognition);
            part.Feature = run.Events.LastOrDefault(item =>
                string.Equals(item.JobId, jobId, StringComparison.Ordinal)
                && item.Feature is not null)?.Feature;

            var completed = run.Events.LastOrDefault(item =>
                string.Equals(item.JobId, jobId, StringComparison.Ordinal)
                && item.Stage == ConversionStage.Completed
                && item.Artifact == ConversionArtifactKind.SolidWorksPart);
            part.WorkerCompleted = completed is not null;
            part.WorkerReportedFailure = run.Events.Any(item =>
                string.Equals(item.JobId, jobId, StringComparison.Ordinal)
                && item.IsError);

            var job = request.Jobs.Single();
            part.XtPath = job.XtPath;
            part.SolidWorksPath = job.SolidWorksPath;
            part.XtLength = File.Exists(job.XtPath) ? new FileInfo(job.XtPath).Length : 0;
            part.SolidWorksLength = File.Exists(job.SolidWorksPath) ? new FileInfo(job.SolidWorksPath).Length : 0;
            part.XtHash = part.XtLength > 0 ? ComputeSha256(job.XtPath) : null;
            part.SolidWorksHash = part.SolidWorksLength > 0 ? ComputeSha256(job.SolidWorksPath) : null;
            part.SourceHashAfter = ComputeSha256(inputPath);
            part.SourceUnchanged = string.Equals(part.SourceHashBefore, part.SourceHashAfter, StringComparison.Ordinal);
            part.CopiedSourceUnchanged = string.Equals(part.CopiedSourceHash, ComputeSha256(copiedSource), StringComparison.Ordinal);

            part.RecognitionHealthy = part.Feature is
            {
                RecognizedFeatureCount: > 0,
                FeaturesCreated: true,
                DegradedToDumbSolid: false,
                GeometryChanged: false,
                SessionFaulted: false,
            };
            part.Success = run.ExitCode == 0
                && !run.TimedOut
                && !part.WorkerReportedFailure
                && part.WorkerCompleted
                && part.FeatureRecognitionAttempted
                && part.XtLength > 0
                && part.SolidWorksLength > 0
                && part.SourceUnchanged
                && part.CopiedSourceUnchanged
                && (!options.RequireHealthyRecognition || part.RecognitionHealthy);

            if (!part.Success && string.IsNullOrWhiteSpace(part.Diagnostic))
                part.Diagnostic = DescribePartFailure(part);
        }

        CompleteProcessCheck(report, edgeBaseline, solidWorksBaseline);
    }

    private static void RunSingleBatch(
        FeatureWorksBatchSmokeOptions options,
        FeatureWorksBatchSmokeReport report)
    {
        var batchDirectory = Path.Combine(report.RunDirectory!, "single-batch");
        var sourceDirectory = Path.Combine(batchDirectory, "source");
        var xtDirectory = Path.Combine(batchDirectory, "XT");
        var solidWorksDirectory = Path.Combine(batchDirectory, "SW");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(xtDirectory);
        Directory.CreateDirectory(solidWorksDirectory);

        var jobs = new List<ConversionJob>();
        for (var index = 0; index < options.PartPaths.Count; index++)
        {
            var inputPath = options.PartPaths[index];
            var copiedSource = Path.Combine(sourceDirectory, Path.GetFileName(inputPath));
            File.Copy(inputPath, copiedSource, overwrite: false);
            var name = Path.GetFileNameWithoutExtension(inputPath);
            var job = new ConversionJob(
                $"featureworks-part-{index + 1:D2}",
                copiedSource,
                Path.Combine(xtDirectory, name + ".x_t"),
                Path.Combine(solidWorksDirectory, name + ".SLDPRT"));
            jobs.Add(job);
            report.Parts.Add(new FeatureWorksBatchSmokePart
            {
                SourcePath = inputPath,
                SourceHashBefore = ComputeSha256(inputPath),
                CopiedSourcePath = copiedSource,
                CopiedSourceHash = ComputeSha256(copiedSource),
                WorkDirectory = batchDirectory,
                XtPath = job.XtPath,
                SolidWorksPath = job.SolidWorksPath,
            });
        }

        var request = new BatchRequest(
            "featureworks-single-batch-" + Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            jobs,
            Overwrite: false,
            RecognizeFeatures: true,
            FullyDefineSketches: true,
            FeatureRecognitionTimeoutSeconds: (int)options.Timeout.TotalSeconds,
            ContinueWhenRecognitionFails: true);
        var batchTimeout = TimeSpan.FromTicks(checked(options.Timeout.Ticks * options.PartPaths.Count));
        var run = RunWorker(options.WorkerPath, request, batchDirectory, batchTimeout);

        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var part = report.Parts[index];
            part.WorkerExitCode = run.ExitCode;
            part.WorkerTimedOut = run.TimedOut;
            part.StandardError = run.StandardError;
            part.UnparsedStandardOutput = run.UnparsedStandardOutput.ToList();
            part.Events = run.Events.Where(item => item.JobId is null || string.Equals(item.JobId, job.Id, StringComparison.Ordinal)).ToList();
            part.FeatureRecognitionAttempted = run.Events.Any(item =>
                string.Equals(item.JobId, job.Id, StringComparison.Ordinal)
                && item.Stage == ConversionStage.FeatureRecognition);
            part.Feature = run.Events.LastOrDefault(item =>
                string.Equals(item.JobId, job.Id, StringComparison.Ordinal)
                && item.Feature is not null)?.Feature;
            part.WorkerCompleted = run.Events.Any(item =>
                string.Equals(item.JobId, job.Id, StringComparison.Ordinal)
                && item.Stage == ConversionStage.Completed
                && item.Artifact == ConversionArtifactKind.SolidWorksPart);
            part.WorkerReportedFailure = run.Events.Any(item =>
                (item.JobId is null || string.Equals(item.JobId, job.Id, StringComparison.Ordinal))
                && item.IsError);
            part.XtLength = File.Exists(job.XtPath) ? new FileInfo(job.XtPath).Length : 0;
            part.SolidWorksLength = File.Exists(job.SolidWorksPath) ? new FileInfo(job.SolidWorksPath).Length : 0;
            part.XtHash = part.XtLength > 0 ? ComputeSha256(job.XtPath) : null;
            part.SolidWorksHash = part.SolidWorksLength > 0 ? ComputeSha256(job.SolidWorksPath) : null;
            part.SourceHashAfter = ComputeSha256(part.SourcePath);
            part.SourceUnchanged = string.Equals(part.SourceHashBefore, part.SourceHashAfter, StringComparison.Ordinal);
            part.CopiedSourceUnchanged = string.Equals(part.CopiedSourceHash, ComputeSha256(part.CopiedSourcePath), StringComparison.Ordinal);
            part.RecognitionHealthy = part.Feature is
            {
                RecognizedFeatureCount: > 0,
                FeaturesCreated: true,
                DegradedToDumbSolid: false,
                GeometryChanged: false,
                SessionFaulted: false,
            };
            part.Success = run.ExitCode == 0
                && !run.TimedOut
                && !part.WorkerReportedFailure
                && part.WorkerCompleted
                && part.FeatureRecognitionAttempted
                && part.XtLength > 0
                && part.SolidWorksLength > 0
                && part.SourceUnchanged
                && part.CopiedSourceUnchanged
                && (!options.RequireHealthyRecognition || part.RecognitionHealthy);
            if (!part.Success)
                part.Diagnostic = DescribePartFailure(part);
        }
    }

    private static void CompleteProcessCheck(
        FeatureWorksBatchSmokeReport report,
        IReadOnlyList<int> edgeBaseline,
        IReadOnlyList<int> solidWorksBaseline)
    {
        report.EdgeProcessesAfter = WaitForBaseline("Edge", edgeBaseline, TimeSpan.FromSeconds(30));
        report.SolidWorksProcessesAfter = WaitForBaseline("SLDWORKS", solidWorksBaseline, TimeSpan.FromSeconds(30));
        report.CadProcessesReaped = report.EdgeProcessesAfter.SequenceEqual(edgeBaseline)
            && report.SolidWorksProcessesAfter.SequenceEqual(solidWorksBaseline);
        if (!report.CadProcessesReaped)
            throw new InvalidOperationException("Worker left an owned Solid Edge or SolidWorks process running.");
    }

    private static string DescribePartFailure(FeatureWorksBatchSmokePart part)
    {
        if (part.WorkerTimedOut)
            return "Worker timed out after cancellation was requested.";
        if (part.WorkerExitCode != 0)
            return $"Worker exited with code {part.WorkerExitCode}.";
        if (part.WorkerReportedFailure)
            return "Worker emitted an error event.";
        if (!part.WorkerCompleted)
            return "Worker did not emit a SolidWorks completion event.";
        if (!part.FeatureRecognitionAttempted)
            return "Worker did not attempt FeatureWorks recognition.";
        if (part.XtLength <= 0 || part.SolidWorksLength <= 0)
            return "Worker did not create both non-empty XT and SLDPRT outputs.";
        if (!part.SourceUnchanged || !part.CopiedSourceUnchanged)
            return "A source fixture hash changed during the smoke run.";
        if (!part.RecognitionHealthy)
            return "FeatureWorks did not complete healthy recognition.";
        return "Unknown smoke failure.";
    }

    private static WorkerRun RunWorker(
        string workerPath,
        BatchRequest request,
        string partDirectory,
        TimeSpan timeout)
    {
        var requestPath = Path.Combine(partDirectory, request.BatchId + ".json");
        var cancellationPath = Path.Combine(partDirectory, request.BatchId + ".cancel");
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
        process.StartInfo.ArgumentList.Add(WorkerProtocol.PartsRequestVerb);
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add(WorkerProtocol.CancellationArgument);
        process.StartInfo.ArgumentList.Add(cancellationPath);
        if (!process.Start())
            throw new InvalidOperationException("Unable to start the production Worker.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timedOut = !process.WaitForExit((int)timeout.TotalMilliseconds);
        if (timedOut)
        {
            File.WriteAllText(cancellationPath, "FeatureWorks smoke timeout.");
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                throw new TimeoutException("Worker did not exit within 30 seconds after cancellation was requested.");
        }

        Task.WaitAll(standardOutput, standardError);
        var events = new List<WorkerEvent>();
        var unparsed = new List<string>();
        foreach (var line in standardOutput.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions);
                if (workerEvent is not null)
                    events.Add(workerEvent);
                else
                    unparsed.Add(line);
            }
            catch (JsonException)
            {
                unparsed.Add(line);
            }
        }

        return new WorkerRun(process.ExitCode, timedOut, events, standardError.Result.Trim(), unparsed);
    }

    private static int[] WaitForBaseline(string processName, IReadOnlyList<int> baseline, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        int[] current;
        do
        {
            current = GetProcessIds(processName);
            if (current.SequenceEqual(baseline))
                return current;
            Thread.Sleep(200);
        }
        while (stopwatch.Elapsed < timeout);
        return current;
    }

    private static int[] GetProcessIds(string processName)
        => Process.GetProcessesByName(processName)
            .Select(process =>
            {
                using (process)
                    return process.Id;
            })
            .OrderBy(id => id)
            .ToArray();

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed class FeatureWorksBatchSmokeOptions
{
    public required string WorkerPath { get; init; }
    public required IReadOnlyList<string> PartPaths { get; init; }
    public required TimeSpan Timeout { get; init; }
    public bool KeepArtifacts { get; init; }
    public bool SingleBatch { get; init; }
    public bool RequireHealthyRecognition { get; init; }

    public static FeatureWorksBatchSmokeOptions Parse(IReadOnlyList<string> args)
    {
        string? workerPath = null;
        var partPaths = new List<string>();
        var timeout = TimeSpan.FromMinutes(3);
        var keepArtifacts = false;
        var singleBatch = false;
        var requireHealthyRecognition = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--worker" when index + 1 < args.Count:
                    workerPath = Path.GetFullPath(args[++index]);
                    break;
                case "--part" when index + 1 < args.Count:
                    partPaths.Add(Path.GetFullPath(args[++index]));
                    break;
                case "--timeout-seconds" when index + 1 < args.Count
                    && int.TryParse(args[++index], out var seconds)
                    && seconds is >= 30 and <= 600:
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--keep":
                    keepArtifacts = true;
                    break;
                case "--single-batch":
                    singleBatch = true;
                    break;
                case "--require-healthy-recognition":
                    requireHealthyRecognition = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or invalid argument: {args[index]}");
            }
        }

        if (workerPath is null || !File.Exists(workerPath))
            throw new FileNotFoundException("Production Worker executable was not found.", workerPath);
        if (partPaths.Count == 0)
            throw new ArgumentException("At least one --part argument is required.");
        if (partPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partPaths.Count)
            throw new ArgumentException("Each --part input must be unique.");
        foreach (var partPath in partPaths)
        {
            if (!File.Exists(partPath)
                || !string.Equals(Path.GetExtension(partPath), ".par", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("Each --part input must be an existing .par file.", partPath);
            }
        }

        return new FeatureWorksBatchSmokeOptions
        {
            WorkerPath = workerPath,
            PartPaths = partPaths,
            Timeout = timeout,
            KeepArtifacts = keepArtifacts,
            SingleBatch = singleBatch,
            RequireHealthyRecognition = requireHealthyRecognition,
        };
    }
}

internal sealed class FeatureWorksBatchSmokeReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string WorkerPath { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public bool KeepArtifacts { get; set; }
    public bool SingleBatch { get; set; }
    public bool RequireHealthyRecognition { get; set; }
    public string? RunDirectory { get; set; }
    public bool ArtifactsDeleted { get; set; }
    public bool CadProcessesReaped { get; set; }
    public int[] EdgeProcessesBefore { get; set; } = [];
    public int[] SolidWorksProcessesBefore { get; set; } = [];
    public int[] EdgeProcessesAfter { get; set; } = [];
    public int[] SolidWorksProcessesAfter { get; set; } = [];
    public List<FeatureWorksBatchSmokePart> Parts { get; } = [];
}

internal sealed class FeatureWorksBatchSmokePart
{
    public bool Success { get; set; }
    public string? Diagnostic { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceHashBefore { get; set; } = string.Empty;
    public string? SourceHashAfter { get; set; }
    public bool SourceUnchanged { get; set; }
    public string CopiedSourcePath { get; set; } = string.Empty;
    public string CopiedSourceHash { get; set; } = string.Empty;
    public bool CopiedSourceUnchanged { get; set; }
    public string WorkDirectory { get; set; } = string.Empty;
    public string XtPath { get; set; } = string.Empty;
    public long XtLength { get; set; }
    public string? XtHash { get; set; }
    public string SolidWorksPath { get; set; } = string.Empty;
    public long SolidWorksLength { get; set; }
    public string? SolidWorksHash { get; set; }
    public int WorkerExitCode { get; set; }
    public bool WorkerTimedOut { get; set; }
    public bool WorkerCompleted { get; set; }
    public bool WorkerReportedFailure { get; set; }
    public bool FeatureRecognitionAttempted { get; set; }
    public bool RecognitionHealthy { get; set; }
    public FeatureOutcome? Feature { get; set; }
    public string StandardError { get; set; } = string.Empty;
    public List<string> UnparsedStandardOutput { get; set; } = [];
    public List<WorkerEvent> Events { get; set; } = [];
}

internal sealed record WorkerRun(
    int ExitCode,
    bool TimedOut,
    IReadOnlyList<WorkerEvent> Events,
    string StandardError,
    IReadOnlyList<string> UnparsedStandardOutput);
