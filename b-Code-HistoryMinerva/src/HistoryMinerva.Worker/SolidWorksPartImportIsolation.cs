using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// FeatureWorks 的 COM 服务在同一 Worker 连续识别多个零件后会污染后续导入。
/// 启用识别时，每个零件使用一个短生命周期子 Worker；普通哑实体导入仍走原批量路径。
/// </summary>
internal static class SolidWorksPartImportIsolation
{
    private const int SessionFaultAttempts = 3;

    /// <summary>连续多少个零件识别到 0 个特征即判定会话未激活。</summary>
    internal const int UnactivatedSessionThreshold = 2;
    private static readonly JsonSerializerOptions JsonOptions = WorkerProtocol.CreateJsonOptions();

    public static int Import(
        BatchRequest request,
        IReadOnlyList<ConversionJob> jobs,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!ShouldIsolate(request))
            return SolidWorksImporter.Import(request, jobs, reporter, cancellationToken);

        var failed = 0;
        // 连续多少个零件识别到 0 个特征就判定"该 SolidWorks 会话未激活 FeatureWorks"。
        // 取 2 而不是 1：单个零件确实可能没有可识别特征，连着两个就不可能是巧合了。
        var consecutiveUnrecognized = 0;
        var effectiveRequest = request;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= SessionFaultAttempts; attempt++)
            {
                // 第一次尝试附着现有会话（多半是用户人工激活过的那个，识别质量最好）；
                // 一旦 FeatureWorks 崩了，后续尝试改用专属进程——继续附着只会拿到同一具尸体。
                var result = RunIsolated(
                    effectiveRequest,
                    job,
                    reporter,
                    cancellationToken,
                    out var recognizedFeatureCount,
                    out var sessionNotActivated,
                    useDedicatedSession: ShouldUseDedicatedSession(attempt));
                if (result == IsolatedImportResult.RecognitionTimedOut)
                {
                    var timeoutSeconds = FeatureRecognitionPolicy.NormalizeTimeoutSeconds(
                        effectiveRequest.FeatureRecognitionTimeoutSeconds);
                    var timeoutOutcome = new FeatureOutcome(
                        0, false, 0, 0, [], true, timeoutSeconds * 1000L,
                        $"特征识别连续 {timeoutSeconds} 秒无进度，已跳过并改用哑实体。",
                        SessionFaulted: false);
                    reporter.Report(
                        job.Id,
                        ConversionStage.FeatureRecognition,
                        "特征识别超时，正在使用普通导入管线重建哑实体。",
                        errorClass: ConversionErrorClass.FeatureRecognitionTimeout,
                        feature: timeoutOutcome,
                        artifact: ConversionArtifactKind.SolidWorksPart);

                    // 识别 Worker 被终止后，不能复用其中的 COM 文档；用独立的无 FeatureWorks
                    // 请求从同一 XT 重建完整 SLDPRT，再让装配构建器按普通组件路径处理。
                    var dumbRequest = effectiveRequest with
                    {
                        RecognizeFeatures = false,
                        FullyDefineSketches = false,
                        ContinueWhenRecognitionFails = true,
                    };
                    var dumbResult = RunIsolated(dumbRequest, job, reporter, cancellationToken);
                    if (dumbResult == IsolatedImportResult.Succeeded)
                    {
                        reporter.Report(
                            job.Id,
                            ConversionStage.Completed,
                            "已跳过无进度的特征识别，哑实体已生成并可参与装配。",
                            errorClass: ConversionErrorClass.FeatureRecognitionTimeout,
                            feature: timeoutOutcome,
                            artifact: ConversionArtifactKind.SolidWorksPart);
                        break;
                    }

                    failed++;
                    break;
                }
                if (result != IsolatedImportResult.SessionFaulted)
                {
                    failed += result == IsolatedImportResult.Failed ? 1 : 0;
                    if (result == IsolatedImportResult.Succeeded && effectiveRequest.RecognizeFeatures)
                    {
                        // 只统计"识别本身颗粒无收"（子 Worker 的 SessionNotActivated）。
                        //
                        // 不能用 RecognizedFeatureCount == 0 代替：语义守卫丢弃结果后
                        // 计数同样是 0，但那恰恰说明**识别是好的**——现场就因此在用户
                        // 已激活的会话里报出了"未激活"，把人指向完全错误的方向。
                        consecutiveUnrecognized = sessionNotActivated
                            ? consecutiveUnrecognized + 1
                            : 0;
                        // 定案只看"连续多件真的识别不出东西"这一个事实。
                        // sessionNotActivated 只是子 Worker 给出的归因猜测，用来润色诊断文案，
                        // 绝不单独定案——它曾因为把吞掉的异常当成 false，在用户已激活的会话里
                        // 把 53 个零件一次性判死。
                        if (consecutiveUnrecognized >= UnactivatedSessionThreshold)
                        {
                            reporter.Report(
                                job.Id,
                                ConversionStage.FeatureRecognition,
                                DescribeUnactivatedSession(consecutiveUnrecognized, sessionNotActivated),
                                errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                            // 继续对剩下的零件重试识别没有意义——激活状态在本批次内不会自己出现，
                            // 只会让每个零件白白多花一次识别的时间。直接降级为哑实体导入。
                            effectiveRequest = effectiveRequest with
                            {
                                RecognizeFeatures = false,
                                FullyDefineSketches = false,
                            };
                        }
                    }
                    break;
                }

                if (attempt == SessionFaultAttempts)
                {
                    reporter.Report(
                        job.Id,
                        ConversionStage.FeatureRecognition,
                        $"FeatureWorks 在 {SessionFaultAttempts} 次独立会话中均发生故障，保留最后一次生成的安全哑实体。",
                        errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                    break;
                }

                reporter.Report(
                    job.Id,
                    ConversionStage.FeatureRecognition,
                    $"FeatureWorks 会话故障，正在创建新的单零件 Worker 重试（{attempt + 1}/{SessionFaultAttempts}）。",
                    errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                try
                {
                    if (File.Exists(job.SolidWorksPath))
                        File.Delete(job.SolidWorksPath);
                }
                catch (Exception ex)
                {
                    reporter.Report(
                        job.Id,
                        ConversionStage.Failed,
                        "无法清理会话故障后的降级产物，停止隔离重试：" + ex.Message,
                        true,
                        ex.HResult,
                        errorClass: ConversionErrorClass.OutputNotWritable);
                    failed++;
                    break;
                }
            }
        }

        return failed;
    }

    internal static bool ShouldIsolate(BatchRequest request)
        => request.RecognizeFeatures;

    /// <summary>
    /// 本次尝试是否要自己启动一个专属 SolidWorks 进程。
    ///
    /// 首次尝试附着现有会话——人工激活过的那个识别质量最好，不该绕过它。
    /// 但 FeatureWorks 一旦在某个复杂零件上崩掉，同一个进程里的加载项就废了，
    /// 继续附着等于对着尸体重试（实测：第 14 件崩溃后剩下 39 件全退哑实体）。
    /// 所以重试一律换专属进程，把"一件搞崩整批"降级成"只坏这一件"。
    /// </summary>
    internal static bool ShouldUseDedicatedSession(int attempt) => attempt > 1;

    /// <summary>
    /// 未激活会话的诊断文案。
    ///
    /// 实测（2026-08-05）：FeatureWorks 的 <c>RecognizeFeatureAutomatic</c> 只有在该
    /// SolidWorks **进程**里由人工完成过一次特征识别之后才生效；未激活时对任何零件、
    /// 任何选项掩码都恒返回 0，且不抛异常、不改几何——是**静默失败**。
    /// 这个静默正是它此前长期没被定位的原因：产物看起来"成功"，只是全是哑实体。
    /// 该状态是加载项的进程内状态，不跨进程、SolidWorks 正常退出也不写盘。
    /// </summary>
    internal static string DescribeUnactivatedSession(int consecutiveUnrecognized, bool reportedByAddIn = false)
        => (reportedByAddIn
               ? "FeatureWorks 报告本 SolidWorks 会话未激活（SetAdvancedOptions 返回 false）。"
               : $"连续 {consecutiveUnrecognized} 个零件识别到 0 个特征，判定本 SolidWorks 会话未激活 FeatureWorks。")
           + "自动识别接口要求先在该 SolidWorks 进程里人工执行过一次特征识别才会生效，"
           + "且该状态不跨进程、退出后不保留。"
           + "请在这个 SolidWorks 窗口里对任意零件手工做一次 FeatureWorks 特征识别，再重跑本批次。"
           + "本批次剩余零件将按哑实体导入，几何与装配关系不受影响。";

    internal static bool HasRecognitionStalled(
        long lastProgressUtcTicks,
        DateTimeOffset observedAt,
        TimeSpan timeout)
        => lastProgressUtcTicks != 0
           && observedAt - new DateTimeOffset(lastProgressUtcTicks, TimeSpan.Zero) >= timeout;

    private static IsolatedImportResult RunIsolated(
        BatchRequest request,
        ConversionJob job,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
        => RunIsolated(request, job, reporter, cancellationToken, out _, out _);

    private static IsolatedImportResult RunIsolated(
        BatchRequest request,
        ConversionJob job,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        out int recognizedFeatureCount,
        out bool sessionNotActivated,
        bool useDedicatedSession = false)
    {
        var observedRecognized = 0;
        // 在 try 之外声明：finally 要读它，把结果交给父循环。
        var childSessionNotActivated = 0;
        var childFeatureWorksUnavailable = 0;
        recognizedFeatureCount = 0;
        sessionNotActivated = false;
        var runDirectory = Path.Combine(
            Path.GetTempPath(),
            "SE2SW-PartImport",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var requestPath = Path.Combine(runDirectory, "request.json");
        var cancellationPath = Path.Combine(runDirectory, "cancel.signal");
        try
        {
            var childRequest = new PartImportRequest(
                request.BatchId,
                request.Mode,
                job,
                request.Overwrite,
                request.RecognizeFeatures,
                request.FullyDefineSketches,
                request.FeatureRecognitionTimeoutSeconds,
                request.ContinueWhenRecognitionFails,
                useDedicatedSession);
            File.WriteAllText(requestPath, JsonSerializer.Serialize(childRequest, JsonOptions));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveWorkerExecutable(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.StartInfo.ArgumentList.Add(WorkerProtocol.PartImportVerb);
            process.StartInfo.ArgumentList.Add(requestPath);
            process.StartInfo.ArgumentList.Add(WorkerProtocol.CancellationArgument);
            process.StartInfo.ArgumentList.Add(cancellationPath);

            var childReportedError = 0;
            var childSessionFaulted = 0;
            long lastRecognitionProgressTicks = 0;
            var unparsedOutput = new List<string>();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                try
                {
                    var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(eventArgs.Data, JsonOptions)
                        ?? throw new JsonException("子 Worker 返回空事件。");
                    if (workerEvent.IsError)
                        Interlocked.Exchange(ref childReportedError, 1);
                    if (workerEvent.Feature is { SessionFaulted: true })
                        Interlocked.Exchange(ref childSessionFaulted, 1);
                    if (workerEvent.Feature is { SessionNotActivated: true })
                        Interlocked.Exchange(ref childSessionNotActivated, 1);
                    // 加载项在这个进程里已经废了（实测：连续 RPC_E_SERVERFAULT 之后
                    // LoadAddIn 仍返回成功、GetAddInObject 却恒为 null）。
                    // 继续附着同一个进程只会一直取不到，必须换会话。
                    if (workerEvent.ErrorClass == ConversionErrorClass.FeatureWorksUnavailable)
                        Interlocked.Exchange(ref childFeatureWorksUnavailable, 1);
                    // 取本零件所有事件里的最大识别数：父循环靠它判断会话是否已激活。
                    if (workerEvent.Feature is { } featureOutcome)
                    {
                        var seen = featureOutcome.RecognizedFeatureCount;
                        int previous;
                        while ((previous = Volatile.Read(ref observedRecognized)) < seen
                               && Interlocked.CompareExchange(ref observedRecognized, seen, previous) != previous)
                        {
                        }
                    }
                    // 任何一条事件都算"子 Worker 还活着"。
                    //
                    // 原来只认 FeatureRecognition 阶段的事件，于是子 Worker 一旦走过识别、
                    // 进入草图完全定义 / 保存 / 装配这些本来就慢的阶段，这个时钟就冻住了，
                    // 超过 180 秒即被当成"识别卡死"误杀。看门狗要防的是**完全没进展**，
                    // 而不是"没在识别"——用阶段筛选事件等于给慢阶段判了死刑。
                    Interlocked.Exchange(ref lastRecognitionProgressTicks, DateTimeOffset.UtcNow.Ticks);
                    reporter.Forward(workerEvent);
                }
                catch (JsonException)
                {
                    lock (unparsedOutput)
                        unparsedOutput.Add(eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                lock (standardError)
                    standardError.AppendLine(eventArgs.Data);
            };

            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("无法启动单零件导入子 Worker。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancellationRegistration = cancellationToken.Register(
                static state => TrySignalCancellation((string)state!),
                cancellationPath);
            var recognitionTimeout = TimeSpan.FromSeconds(
                FeatureRecognitionPolicy.NormalizeTimeoutSeconds(request.FeatureRecognitionTimeoutSeconds));
            var recognitionTimedOut = false;
            var cancellationObservedAt = DateTimeOffset.MinValue;
            while (!process.WaitForExit(100))
            {
                var progressTicks = Volatile.Read(ref lastRecognitionProgressTicks);
                if (request.RecognizeFeatures
                    && HasRecognitionStalled(progressTicks, DateTimeOffset.UtcNow, recognitionTimeout))
                {
                    recognitionTimedOut = true;
                    TryKill(process);
                    process.WaitForExit();
                    break;
                }
                if (!cancellationToken.IsCancellationRequested)
                    continue;
                if (cancellationObservedAt == DateTimeOffset.MinValue)
                    cancellationObservedAt = DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - cancellationObservedAt <= TimeSpan.FromSeconds(15))
                    continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                break;
            }
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();

            if (recognitionTimedOut)
                return IsolatedImportResult.RecognitionTimedOut;

            if (process.ExitCode == 0)
            {
                // 只有在还没换过专属会话时，"加载项不可用"才值得重试——
                // 专属会话里仍不可用就是真的没有，重试只是白跑。
                var sessionUnusable = Volatile.Read(ref childSessionFaulted) != 0
                    || (request.RecognizeFeatures
                        && !useDedicatedSession
                        && Volatile.Read(ref childFeatureWorksUnavailable) != 0);
                return sessionUnusable
                    ? IsolatedImportResult.SessionFaulted
                    : IsolatedImportResult.Succeeded;
            }

            if (Volatile.Read(ref childReportedError) == 0)
            {
                var diagnostic = DescribeChildFailure(process.ExitCode, standardError, unparsedOutput);
                reporter.Report(
                    job.Id,
                    ConversionStage.Failed,
                    diagnostic,
                    true,
                    errorClass: ConversionErrorClass.ImportFailed);
            }
            return IsolatedImportResult.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(
                job.Id,
                ConversionStage.Failed,
                "单零件隔离导入失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ComErrorClassifier.Classify(ex, ConversionErrorClass.ImportFailed));
            return IsolatedImportResult.Failed;
        }
        finally
        {
            recognizedFeatureCount = Volatile.Read(ref observedRecognized);
            sessionNotActivated = Volatile.Read(ref childSessionNotActivated) == 1;
            TryDeleteDirectory(runDirectory);
        }
    }

    private enum IsolatedImportResult
    {
        Succeeded,
        Failed,
        SessionFaulted,
        RecognitionTimedOut,
    }

    private static string ResolveWorkerExecutable()
    {
        var besideAssembly = Path.Combine(AppContext.BaseDirectory, "SE2SW.Worker.exe");
        if (File.Exists(besideAssembly))
            return besideAssembly;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "SE2SW.Worker",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        throw new FileNotFoundException("无法定位 SE2SW.Worker.exe，不能启动单零件隔离导入。", besideAssembly);
    }

    private static string DescribeChildFailure(
        int exitCode,
        StringBuilder standardError,
        IReadOnlyCollection<string> unparsedOutput)
    {
        string error;
        lock (standardError)
            error = standardError.ToString().Trim();
        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error;
        if (unparsedOutput.Count > 0)
            suffix += $" 子 Worker 另有 {unparsedOutput.Count} 行无法解析的输出。";
        return $"单零件导入子 Worker 异常退出（代码 {exitCode}）。{suffix}".TrimEnd();
    }

    private static void TrySignalCancellation(string cancellationPath)
    {
        try
        {
            File.WriteAllText(cancellationPath, string.Empty);
        }
        catch
        {
            // 父 Worker仍会在宽限期后终止自己创建的子进程。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时请求清理失败不覆盖真实的转换结果。
        }
    }
}
