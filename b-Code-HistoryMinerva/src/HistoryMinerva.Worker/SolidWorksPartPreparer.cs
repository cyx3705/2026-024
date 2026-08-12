using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// V4.3：SolidWorks 源零件的"整备"。
///
/// 源已经是 <c>.SLDPRT</c>，没有格式转换可做——要做的只有一件事：
/// 把源零件复制到输出目录，在**副本**上跑既有的 FeatureWorks 识别与草图完全定义，存盘。
/// 源文件从头到尾只被读取一次（<c>File.Copy</c>），SolidWorks 永远打不开它。
///
/// 与 Solid Edge 管线的关键差别，也是这条路更稳的原因：
///
///   · 没有 Parasolid 往返，几何天然逐位保真；
///   · 识别失败时的降级产物不需要"重新导入"——直接把源文件再复制一遍就是完美的哑实体，
///     字节与源完全相同。V2.0 那套"丢弃文档、从 XT 重来"的补救在这里根本用不上。
///
/// 工作副本用 <see cref="TemporaryOutput.For"/> 的唯一临时名打开，因此它的文档标题
/// 绝不会和会话里可能开着的同名源零件相撞（<c>swFileWithSameTitleAlreadyOpen</c>），
/// 落盘后再改名成最终名。
/// </summary>
internal static class SolidWorksPartPreparer
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;

    public static int Prepare(
        BatchRequest request,
        IReadOnlyList<ConversionJob> jobs,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        bool useDedicatedSession = false)
    {
        if (jobs.Count == 0)
            return 0;

        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        FeatureRecognizer? recognizer = null;
        bool? originalCommandInProgress = null;
        var failed = 0;

        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered, "未检测到 SolidWorks COM 注册。");
            try
            {
                if (useDedicatedSession)
                {
                    (applicationObject, var dedicatedProcessId) =
                        SolidWorksSessionLauncher.StartDedicated(cancellationToken);
                    reporter.Report(
                        jobs[0].Id,
                        ConversionStage.SolidWorksImport,
                        $"上一次 FeatureWorks 会话故障，本零件改用专属 SolidWorks 进程（PID {dedicatedProcessId}）。");
                }
                else
                {
                    applicationObject = Activator.CreateInstance(applicationType)
                        ?? throw new InvalidOperationException("COM 返回了空实例。");
                }
            }
            catch (Exception ex) when (ex is not ClassifiedConversionException and not OperationCanceledException)
            {
                throw new ClassifiedConversionException(
                    ComErrorClassifier.Classify(ex, ConversionErrorClass.AppLaunchFailed),
                    "SolidWorks COM 实例创建失败：" + ex.Message,
                    ex);
            }

            dynamic application = applicationObject;
            try
            {
                interop = SolidWorksInteropBridge.Create(applicationObject, applicationType);
            }
            catch (Exception ex)
            {
                ownership.Resolve(0);
                throw new ClassifiedConversionException(
                    ConversionErrorClass.AppLaunchFailed,
                    "SolidWorks 官方 Interop 初始化失败：" + ex.Message,
                    ex);
            }

            ownership.Resolve(TryGetHandle(interop));
            if (!ownership.OwnsInstance)
            {
                reporter.Report(
                    null,
                    ConversionStage.SolidWorksImport,
                    "正在使用你已打开的 SolidWorks 会话，本次转换不会关闭它。",
                    errorClass: ConversionErrorClass.CadProcessOwnershipUnknown);
            }
            else
            {
                originalCommandInProgress = TryGetBoolean(() => application.CommandInProgress);
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
                TryRun(() => application.CommandInProgress = true);
            }

            recognizer = FeatureRecognizer.Prepare(interop);
            if (request.RecognizeFeatures)
            {
                if (!recognizer.IsAvailable && !request.ContinueWhenRecognitionFails)
                    throw new ClassifiedConversionException(recognizer.UnavailableReason, recognizer.StatusMessage);
                reporter.Report(
                    null,
                    ConversionStage.FeatureRecognition,
                    recognizer.StatusMessage,
                    errorClass: recognizer.IsAvailable ? ConversionErrorClass.None : recognizer.UnavailableReason);
            }

            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PrepareOne(interop, recognizer, request, job, reporter, cancellationToken))
                    failed++;
            }

            return failed;
        }
        finally
        {
            recognizer?.Dispose();
            if (applicationObject is not null)
            {
                dynamic application = applicationObject;
                if (originalCommandInProgress is bool commandInProgress)
                    TryRun(() => application.CommandInProgress = commandInProgress);
                if (ownership.OwnsInstance)
                    TryRun(() =>
                    {
                        if (interop is not null)
                            interop.ExitApplication();
                        else
                            application.ExitApp();
                    });
            }
            interop?.Dispose();
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
            {
                var exited = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
                if (!exited)
                {
                    reporter.Report(
                        null,
                        ConversionStage.Failed,
                        $"本批创建的 SolidWorks 进程 {ownership.OwnedProcessId} 无法退出。",
                        true,
                        errorClass: ConversionErrorClass.AppLaunchFailed);
                }
            }
        }
    }

    private static bool PrepareOne(
        SolidWorksInteropBridge interop,
        FeatureRecognizer recognizer,
        BatchRequest request,
        ConversionJob job,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Report(
            job.Id,
            ConversionStage.SolidWorksImport,
            "正在整备 SolidWorks 零件。",
            artifact: ConversionArtifactKind.SolidWorksPart);

        string? temporaryPath = null;
        object? model = null;
        object? extension = null;
        FeatureOutcome? featureOutcome = null;

        try
        {
            temporaryPath = TemporaryOutput.For(job.SolidWorksPath);
            File.Copy(job.SourcePath, temporaryPath, overwrite: false);

            if (!request.RecognizeFeatures)
            {
                // 不识别就没有任何理由打开 SolidWorks：复制出来的副本已经是最终产物。
                CommitCopy(job, ref temporaryPath, request.Overwrite, cancellationToken, reporter, null);
                return true;
            }

            model = interop.OpenPart(temporaryPath, out var openErrors, out var openWarnings);
            if (model is null || openErrors != 0)
            {
                var conflict = interop.DescribeTitleConflict(temporaryPath);
                throw new ClassifiedConversionException(
                    ConversionErrorClass.ImportFailed,
                    conflict is null
                        ? $"打开零件副本失败：errors={openErrors}, warnings={openWarnings}"
                        : $"打开零件副本失败。{conflict}");
            }

            if (interop.GetDocumentType(model) != DocumentTypePart)
                throw new InvalidDataException("SolidWorks 打开的副本不是零件文档。");

            var title = interop.GetTitle(model);
            var identityFailure = DescribeIdentityFailure(temporaryPath, title);
            if (identityFailure is not null)
                throw new InvalidDataException(identityFailure);

            // 源零件里没有导入体 = 已经有完整特征树 = 没有可整备的东西。
            //
            // 必须在识别之前拦掉，否则会连累整批：对这种零件
            // RecognizeFeatureAutomatic 必然返回 0，而 SetAdvancedOptions 也返回 false
            // ——后者被当成"本会话未激活 FeatureWorks"的证据。实测（2026-08-12，
            // 12位消解器样件）：批次头两个零件恰好都已整备好，于是连续两次"未识别"
            // 触发降级，真正需要整备的 BJ10B-04/05 反而一次都没被识别，整批交付哑实体。
            //
            // SetAdvancedOptions 返回 false 有两种成因——会话未激活、活动文档里没有
            // 可识别的导入体——它从来就不是一个干净的激活信号，不能让第二种冒充第一种。
            var sourceTree = interop.ReadTopLevelFeatureTree(model);
            if (FeatureRecognizer.CountResidualImportedBodies(sourceTree) == 0)
            {
                var existingFeatures = FeatureRecognizer.CountBuiltSolidFeatures(sourceTree);
                TryClose(interop, model);
                ComRelease.Final(model);
                model = null;
                CommitCopy(job, ref temporaryPath, request.Overwrite, cancellationToken, reporter, null,
                    $"源零件已有特征树（{existingFeatures} 个造型特征），没有待整备的导入体；"
                        + "产物与源逐字节相同。");
                return true;
            }

            featureOutcome = recognizer.Process(
                model,
                title,
                request,
                (stage, message) => reporter.Report(job.Id, stage, message),
                cancellationToken);

            // 几何被改坏、特征树语义错误、或识别整体降级：一律丢弃这个文档，
            // 把源文件重新复制一份作为产物。这份产物与源逐字节相同，不存在"半成品"。
            if (ShouldFallBackToSourceCopy(featureOutcome))
            {
                TryClose(interop, model);
                ComRelease.Final(model);
                model = null;
                reporter.Report(
                    job.Id,
                    ConversionStage.FeatureRecognition,
                    DescribeFallback(featureOutcome),
                    errorClass: SolidWorksImporter.ClassifyRejectedRecognition(featureOutcome));
                TemporaryOutput.DeleteIfExists(temporaryPath);
                File.Copy(job.SourcePath, temporaryPath, overwrite: false);
                CommitCopy(
                    job,
                    ref temporaryPath,
                    request.Overwrite,
                    cancellationToken,
                    reporter,
                    featureOutcome with { DegradedToDumbSolid = true });
                return true;
            }

            extension = interop.GetExtension(model);
            if (!interop.SaveAs3(
                    extension, temporaryPath, SaveAsCurrentVersion, SaveAsSilent,
                    out var saveErrors, out var saveWarnings)
                || saveErrors != 0)
            {
                throw new IOException($"SolidWorks 保存失败，错误码 {saveErrors}，警告码 {saveWarnings}。");
            }

            ComRelease.Final(extension);
            extension = null;
            interop.CloseDocument(title);
            ComRelease.Final(model);
            model = null;

            var output = FileProbe.WaitForStableNonEmptyFile(temporaryPath, cancellationToken);
            TemporaryOutput.Commit(temporaryPath, job.SolidWorksPath, request.Overwrite);
            temporaryPath = null;
            reporter.Report(
                job.Id,
                ConversionStage.Completed,
                $"整备完成，SolidWorks 零件 {output.Length} 字节。{DescribeFeatures(featureOutcome)}",
                nativeError: saveErrors,
                nativeWarning: saveWarnings,
                errorClass: SolidWorksImporter.ClassifyCompletedFeatureOutcome(featureOutcome),
                feature: featureOutcome,
                artifact: ConversionArtifactKind.SolidWorksPart);
            return true;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            TryClose(interop, model);
            throw new OperationCanceledException("SolidWorks 零件整备已取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            TryClose(interop, model);
            reporter.Report(
                job.Id,
                ConversionStage.Failed,
                "SolidWorks 零件整备失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ComErrorClassifier.Classify(ex, ConversionErrorClass.ImportFailed),
                // 会话故障必须原样上报：父进程据此换一个专属 SolidWorks 会话重试这一件。
                feature: featureOutcome ?? (FeatureRecognizer.IsServerFault(ex)
                    ? new FeatureOutcome(0, false, 0, 0, [], true, 0, ex.Message, SessionFaulted: true)
                    : null));
            return false;
        }
        finally
        {
            TemporaryOutput.DeleteIfExists(temporaryPath);
            ComRelease.Final(extension);
            ComRelease.Final(model);
        }
    }

    /// <summary>把已经就位的副本改名成最终产物，并报告完成。</summary>
    private static void CommitCopy(
        ConversionJob job,
        ref string? temporaryPath,
        bool overwrite,
        CancellationToken cancellationToken,
        WorkerReporter reporter,
        FeatureOutcome? featureOutcome,
        string? completionNote = null)
    {
        var output = FileProbe.WaitForStableNonEmptyFile(temporaryPath!, cancellationToken);
        TemporaryOutput.Commit(temporaryPath!, job.SolidWorksPath, overwrite);
        temporaryPath = null;
        reporter.Report(
            job.Id,
            ConversionStage.Completed,
            completionNote is not null
                ? $"跳过整备：{completionNote}"
                : featureOutcome is null
                ? $"整备完成（未启用特征识别），SolidWorks 零件 {output.Length} 字节，与源逐字节相同。"
                : $"整备完成，SolidWorks 零件 {output.Length} 字节。{DescribeFeatures(featureOutcome)}",
            errorClass: featureOutcome is null
                ? ConversionErrorClass.None
                : SolidWorksImporter.ClassifyRejectedRecognition(featureOutcome),
            feature: featureOutcome,
            artifact: ConversionArtifactKind.SolidWorksPart);
    }

    /// <summary>
    /// 什么时候丢弃识别结果、退回源文件副本。
    ///
    /// 除了几何被改坏与语义错误，"识别整体降级"也退回副本——因为此时 SolidWorks 里那个
    /// 文档已经被 FeatureWorks 动过一轮，存下来不如源文件干净，而两者的价值完全相同。
    /// </summary>
    internal static bool ShouldFallBackToSourceCopy(FeatureOutcome outcome)
        => outcome.GeometryChanged || outcome.SemanticMismatch || outcome.DegradedToDumbSolid;

    private static string DescribeFallback(FeatureOutcome outcome)
    {
        if (outcome.GeometryChanged)
            return "特征识别改变了零件几何，已丢弃并回退为源零件副本。";
        if (outcome.SemanticMismatch)
            return "特征识别结果类型错误，已丢弃并回退为源零件副本。";
        var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
        return $"未识别到可用特征，产物为源零件副本。{reason}";
    }

    private static string DescribeFeatures(FeatureOutcome? outcome)
    {
        if (outcome is null)
            return string.Empty;
        if (outcome.DegradedToDumbSolid)
        {
            var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
            return $" 未生成特征，产物为源零件副本。{reason}";
        }

        var diagnostic = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
        return $" 识别 {outcome.RecognizedFeatureCount} 个特征，草图完全定义 "
            + $"{outcome.SketchFullyDefined}/{outcome.SketchTotal}。{diagnostic}";
    }

    /// <summary>
    /// 身份校验：SolidWorks 交回的文档必须就是刚复制出来的那个副本。
    /// 与导入管线同一条规矩——绝不把别人的几何存成这个零件。
    /// </summary>
    internal static string? DescribeIdentityFailure(string workingPath, string? openedTitle)
    {
        var expected = Path.GetFileNameWithoutExtension(workingPath);
        if (string.IsNullOrWhiteSpace(expected))
            return null;
        if (string.IsNullOrWhiteSpace(openedTitle))
            return $"SolidWorks 未返回文档标题，无法确认打开的是 {expected}。";
        return openedTitle.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SolidWorks 交回的文档与目标不符：期望 {expected}，实得 {openedTitle}。";
    }

    private static void TryClose(SolidWorksInteropBridge? interop, object? model)
    {
        if (interop is null || model is null)
            return;
        TryRun(() =>
        {
            var title = interop.GetTitle(model);
            if (!string.IsNullOrWhiteSpace(title))
                interop.CloseDocument(title);
        });
    }

    private static long TryGetHandle(SolidWorksInteropBridge interop)
    {
        try { return interop.GetWindowHandle(); } catch { return 0; }
    }

    private static bool? TryGetBoolean(Func<object> valueFactory)
    {
        try { return Convert.ToBoolean(valueFactory()); }
        catch { return null; }
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
