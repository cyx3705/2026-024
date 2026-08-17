using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// V4.3：SolidWorks 源零件的"整备"。
///
/// 源已经是 <c>.SLDPRT</c>。特征整备不检测原零件有没有特征树：副本一律导出
/// 交付用 <c>.x_t</c>，再 <c>LoadFile4</c> 成导入体后按开关识别。识别失败也保留
/// 导入体，不得把源零件原来的特征树拷回去。
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
        string? xtPath = null;
        object? model = null;
        object? extension = null;
        FeatureOutcome? featureOutcome = null;

        try
        {
            temporaryPath = TemporaryOutput.For(job.SolidWorksPath);
            File.Copy(job.SourcePath, temporaryPath, overwrite: false);

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

            // 不读源特征树、不按导入体/普通零件分叉：一律先转 XT 再识别。
            xtPath = Path.ChangeExtension(temporaryPath, ".x_t");
            SolidWorksXtExporter.SaveAsParasolid(interop, model, xtPath, cancellationToken);
            interop.CloseDocument(title);
            ComRelease.Final(model);
            model = null;
            TemporaryOutput.DeleteIfExists(temporaryPath);

            title = LoadFlattenedImport(interop, xtPath, cancellationToken, out model);

            if (request.RecognizeFeatures)
            {
                featureOutcome = recognizer.Process(
                    model,
                    title,
                    request,
                    (stage, message) => reporter.Report(job.Id, stage, message),
                    cancellationToken);

                if (ShouldKeepFlattenedImport(featureOutcome))
                {
                    reporter.Report(
                        job.Id,
                        ConversionStage.FeatureRecognition,
                        DescribeKeepFlattened(featureOutcome),
                        errorClass: SolidWorksImporter.ClassifyRejectedRecognition(featureOutcome));
                    TryClose(interop, model);
                    ComRelease.Final(model);
                    model = null;
                    title = LoadFlattenedImport(interop, xtPath, cancellationToken, out model);
                    featureOutcome = featureOutcome with { DegradedToDumbSolid = true };
                }
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
            TemporaryOutput.DeleteIfExists(xtPath);
            ComRelease.Final(extension);
            ComRelease.Final(model);
        }
    }

    /// <summary>
    /// 识别结果不能交付时，丢弃 FeatureWorks 改过的文档，重新载入 XT。
    /// 几何被改坏、识别失败或语义不合规都走这条路，不得把源零件原来的特征树拷回去。
    /// </summary>
    internal static bool ShouldKeepFlattenedImport(FeatureOutcome outcome)
        => outcome.GeometryChanged || outcome.DegradedToDumbSolid || outcome.SemanticMismatch;

    private static string DescribeKeepFlattened(FeatureOutcome outcome)
    {
        if (outcome.GeometryChanged)
            return "特征识别改坏了几何，已重新载入 Parasolid 导入体，未退回源零件特征树。";
        if (outcome.SemanticMismatch && !string.IsNullOrWhiteSpace(outcome.Diagnostic))
            return outcome.Diagnostic + " 已保留 Parasolid 压平后的导入体。";
        var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
        return $"未识别到可用特征，已保留 Parasolid 压平后的导入体。{reason}";
    }

    private static string DescribeFeatures(FeatureOutcome? outcome)
    {
        if (outcome is null)
            return " 已压平为导入体（未启用特征识别）。";
        if (outcome.DegradedToDumbSolid)
        {
            var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
            return $" 未生成特征，已保留 Parasolid 压平后的导入体。{reason}";
        }

        var diagnostic = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? string.Empty : $"（{outcome.Diagnostic}）";
        return $" 识别 {outcome.RecognizedFeatureCount} 个特征，草图完全定义 "
            + $"{outcome.SketchFullyDefined}/{outcome.SketchTotal}。{diagnostic}";
    }

    private static string LoadFlattenedImport(
        SolidWorksInteropBridge interop,
        string xtPath,
        CancellationToken cancellationToken,
        out object model)
    {
        object? importData = null;
        try
        {
            importData = interop.GetImportFileData(xtPath);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            importData = null;
        }

        try
        {
            model = interop.LoadFile4(xtPath, "r", importData, out var loadErrors)
                ?? throw new InvalidDataException($"SolidWorks 未从 Parasolid 交回零件文档，错误码 {loadErrors}。");
        }
        finally
        {
            ComRelease.Final(importData);
        }

        if (interop.GetDocumentType(model) != DocumentTypePart)
            throw new InvalidDataException("Parasolid 导入结果不是零件文档。");
        var title = interop.GetTitle(model);
        var importIdentityFailure = SolidWorksImporter.DescribeImportIdentityFailure(xtPath, title);
        if (importIdentityFailure is not null)
            throw new InvalidDataException(importIdentityFailure);
        return title;
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
