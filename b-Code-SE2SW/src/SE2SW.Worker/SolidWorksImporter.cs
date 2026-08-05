using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class SolidWorksImporter
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;

    public static int Import(
        BatchRequest request,
        IReadOnlyList<ConversionJob> exported,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        bool resetFeatureWorksSession = false,
        bool useDedicatedSession = false)
    {
        if (exported.Count == 0)
            return 0;

        var ownership = CadProcessOwnership.Capture(ProcessName);
        var dedicatedProcessId = 0;
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        FeatureRecognizer? recognizer = null;
        bool? originalCommandInProgress = null;
        bool? featureWorksWasLoaded = null;
        var failed = 0;

        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered,
                    "未检测到 SolidWorks COM 注册。");
            try
            {
                if (useDedicatedSession)
                {
                    // 上一次尝试里 FeatureWorks 崩了。CreateInstance 只会再附着回同一个
                    // 已损坏的进程，所以这次自己起一个专属实例，别人的会话不受影响。
                    (applicationObject, dedicatedProcessId) =
                        SolidWorksSessionLauncher.StartDedicated(cancellationToken);
                    reporter.Report(
                        exported[0].Id,
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
            long windowHandle = 0;
            try
            {
                windowHandle = interop.GetWindowHandle();
            }
            catch
            {
                // 唯一新增 PID 仍可作为所有权后备证据。
            }
            ownership.Resolve(windowHandle);

            // SolidWorks 是单实例：用户已经开着 SolidWorks 时，CreateInstance 一定附着到那个会话，
            // 不会新建进程。此时继续转换但绝不 ExitApp、绝不改可见性，用户的未保存工作不受影响。
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

            if (ShouldResetFeatureWorksSession(request.RecognizeFeatures && resetFeatureWorksSession, ownership.OwnsInstance))
            {
                var resetSucceeded = FeatureRecognizer.TryBeginIsolatedSession(
                    interop,
                    out var wasLoaded,
                    out var resetDiagnostic);
                featureWorksWasLoaded = wasLoaded;
                reporter.Report(
                    exported[0].Id,
                    ConversionStage.FeatureRecognition,
                    resetDiagnostic,
                    errorClass: resetSucceeded
                        ? ConversionErrorClass.None
                        : ConversionErrorClass.FeatureWorksUnavailable);
            }
            else if (request.RecognizeFeatures && resetFeatureWorksSession && ownership.OwnsInstance)
            {
                reporter.Report(
                    exported[0].Id,
                    ConversionStage.FeatureRecognition,
                    "本零件使用全新 SolidWorks 会话，跳过 FeatureWorks 重载。",
                    errorClass: ConversionErrorClass.None);
            }

            var featureWorksLost = false;
            // 会话重建的总预算。实测教训：重建能"成功"（GetAddInObject 返回非空），
            // 但下一次识别立刻再次故障——不设上限就是死循环（现场在同一个零件上转了 348 次）。
            var sessionRecoveryBudget = resetFeatureWorksSession ? 0 : 2;
            recognizer = FeatureRecognizer.Prepare(interop);
            if (request.RecognizeFeatures)
            {
                if (!recognizer.IsAvailable && !request.ContinueWhenRecognitionFails)
                {
                    throw new ClassifiedConversionException(
                        recognizer.UnavailableReason,
                        recognizer.StatusMessage);
                }

                reporter.Report(
                    null,
                    ConversionStage.FeatureRecognition,
                    recognizer.StatusMessage,
                    errorClass: recognizer.IsAvailable
                        ? ConversionErrorClass.None
                        : recognizer.UnavailableReason);
            }

            for (var jobIndex = 0; jobIndex < exported.Count; jobIndex++)
            {
                var job = exported[jobIndex];
                cancellationToken.ThrowIfCancellationRequested();
                reporter.Report(
                    job.Id,
                    ConversionStage.SolidWorksImport,
                    "正在生成 SolidWorks 零件。",
                    artifact: ConversionArtifactKind.SolidWorksPart);
                object? importData = null;
                object? modelObject = null;
                object? extensionObject = null;
                string? temporaryPath = null;
                FeatureOutcome? featureOutcome = null;
                string? firstRecognitionDiagnostic = null;
                try
                {
                    temporaryPath = TemporaryOutput.For(job.SolidWorksPath);
                    var importAttempt = 0;
                    var recognitionDisabled = featureWorksLost;
                    FeatureOutcome? rejectedRecognition = null;
                    while (true)
                    {
                        try
                        {
                            importData = interop.GetImportFileData(job.XtPath);
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Parasolid 在部分 SW 版本中没有专用导入选项对象；LoadFile4 接受 null。
                            importData = null;
                        }
                        var loadErrors = 0;
                        try
                        {
                            modelObject = interop.LoadFile4(job.XtPath, "r", importData, out loadErrors);
                        }
                        catch (Exception ex)
                        {
                            throw ImportOperationFailed("LoadFile4", ex);
                        }
                        if (modelObject is null)
                            throw new InvalidDataException($"SolidWorks 未返回零件文档，错误码 {loadErrors}。");

                        int documentType;
                        try
                        {
                            documentType = interop.GetDocumentType(modelObject);
                        }
                        catch (Exception ex)
                        {
                            throw ImportOperationFailed("ModelDoc2.GetType", ex);
                        }
                        if (documentType != DocumentTypePart)
                            throw new InvalidDataException("SolidWorks 导入结果不是零件文档。");

                        // 导入身份校验。实测事故：FeatureWorks 的 COM 服务器故障后，
                        // 后续 LoadFile4 会交回**上一件的文档**，于是接下来每个零件都被存成同一个形状
                        // （现场是 5 个零件全部变成第 7 件那个方块）。
                        // 标题不匹配就当场失败——绝不能把别人的几何存成这个零件。
                        var identityFailure = DescribeImportIdentityFailure(
                            job.XtPath, interop.GetTitle(modelObject));
                        if (identityFailure is not null)
                            throw new InvalidDataException(identityFailure);

                        // ---- V2.0：特征识别 + 草图完全定义（在保存之前做完） ----
                        // 未启用识别时不产生 FeatureOutcome，行为与 V1.x 完全一致。
                        if (request.RecognizeFeatures && !recognitionDisabled)
                            featureOutcome = recognizer!.Process(
                                modelObject,
                                interop.GetTitle(modelObject),
                                request,
                                (stage, message) => reporter.Report(job.Id, stage, message),
                                cancellationToken);

                        // FeatureWorks 服务器故障：重建一次会话；重建不了就停用本批次的识别，
                        // 给用户一个"从第 N 件起没有特征"的明确结论，而不是每件慢 10 次重试。
                        if (featureOutcome is { SessionFaulted: true } && !recognitionDisabled)
                        {
                            if (sessionRecoveryBudget > 0 && recognizer!.TryRecoverSession())
                            {
                                sessionRecoveryBudget--;
                                reporter.Report(
                                    job.Id,
                                    ConversionStage.FeatureRecognition,
                                    $"FeatureWorks 会话故障，已重建并重新导入该零件（剩余重建预算 {sessionRecoveryBudget}）。",
                                    errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                            }
                            else
                            {
                                recognitionDisabled = true;
                                featureWorksLost = true;
                                reporter.Report(
                                    job.Id,
                                    ConversionStage.FeatureRecognition,
                                    sessionRecoveryBudget > 0
                                        ? "FeatureWorks 会话故障且无法重建，本批次剩余零件不再尝试识别，输出为哑实体。"
                                        : "FeatureWorks 会话反复故障，已用尽重建预算，本批次剩余零件不再尝试识别，输出为哑实体。",
                                    errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                            }

                            TryClose(interop, modelObject);
                            ComRelease.Final(modelObject);
                            modelObject = null;
                            ComRelease.Final(importData);
                            importData = null;
                            importAttempt++;
                            StaMessagePump.PumpAndWait(750, cancellationToken);
                            continue;
                        }

                        // 几何改变或特征树语义错误都不能交付。丢弃当前文档，关闭本件识别，
                        // 从同一 XT 重新导入完整哑实体。
                        if (featureOutcome is { } rejected
                            && ShouldReimportRejectedRecognition(rejected)
                            && !recognitionDisabled)
                        {
                            rejectedRecognition = rejected;
                            recognitionDisabled = true;
                            reporter.Report(
                                job.Id,
                                ConversionStage.FeatureRecognition,
                                rejected.SemanticMismatch
                                    ? "特征识别结果类型错误，正在丢弃并重新导入为哑实体。"
                                    : "特征识别改变了零件几何，正在丢弃并重新导入为哑实体。",
                                errorClass: ClassifyRejectedRecognition(rejected));
                            TryClose(interop, modelObject);
                            ComRelease.Final(modelObject);
                            modelObject = null;
                            ComRelease.Final(importData);
                            importData = null;
                            importAttempt++;
                            StaMessagePump.PumpAndWait(750, cancellationToken);
                            continue;
                        }

                        if (!ShouldRetryFirstRecognition(
                                jobIndex,
                                importAttempt,
                                recognizer!.IsAvailable,
                                featureOutcome))
                        {
                            if (firstRecognitionDiagnostic is not null && featureOutcome is not null)
                            {
                                var retryDiagnostic = string.IsNullOrWhiteSpace(featureOutcome.Diagnostic)
                                    ? "重试成功"
                                    : "重试诊断=" + featureOutcome.Diagnostic;
                                featureOutcome = featureOutcome with
                                {
                                    Diagnostic = $"首件重新导入恢复；首次诊断={firstRecognitionDiagnostic}；{retryDiagnostic}",
                                };
                            }
                            break;
                        }

                        firstRecognitionDiagnostic = featureOutcome?.Diagnostic ?? "识别返回 0";
                        reporter.Report(
                            job.Id,
                            ConversionStage.FeatureRecognition,
                            "首件识别尚未就绪，正在关闭未保存文档并重新导入一次。");
                        TryClose(interop, modelObject);
                        ComRelease.Final(modelObject);
                        modelObject = null;
                        ComRelease.Final(importData);
                        importData = null;
                        importAttempt++;
                        StaMessagePump.PumpAndWait(750, cancellationToken);
                    }

                    if (rejectedRecognition is not null)
                    {
                        featureOutcome = new FeatureOutcome(
                            0, false, 0, 0, [], true, 0,
                            "已重新导入为哑实体。原因：" + rejectedRecognition.Diagnostic,
                            GeometryChanged: rejectedRecognition.GeometryChanged,
                            SemanticMismatch: rejectedRecognition.SemanticMismatch);
                    }

                    if (featureOutcome is { DegradedToDumbSolid: true }
                        && !request.ContinueWhenRecognitionFails)
                    {
                        throw new ClassifiedConversionException(
                            ClassifyRejectedRecognition(featureOutcome),
                            "特征识别未产生结果，且当前设置不允许降级为哑实体。");
                    }

                    try
                    {
                        extensionObject = interop.GetExtension(modelObject);
                    }
                    catch (Exception ex)
                    {
                        throw ImportOperationFailed("ModelDoc2.Extension", ex);
                    }
                    int saveErrors;
                    int saveWarnings;
                    bool saved;
                    try
                    {
                        saved = interop.SaveAs3(
                            extensionObject,
                            temporaryPath,
                            SaveAsCurrentVersion,
                            SaveAsSilent,
                            out saveErrors,
                            out saveWarnings);
                    }
                    catch (Exception ex)
                    {
                        throw ImportOperationFailed("ModelDocExtension.SaveAs3", ex);
                    }
                    if (!saved || saveErrors != 0)
                        throw new IOException($"SolidWorks 保存失败，错误码 {saveErrors}，警告码 {saveWarnings}。");

                    var title = interop.GetTitle(modelObject);
                    if (!string.IsNullOrWhiteSpace(title))
                        interop.CloseDocument(title);
                    ComRelease.Final(extensionObject);
                    extensionObject = null;
                    ComRelease.Final(modelObject);
                    modelObject = null;

                    var output = FileProbe.WaitForStableNonEmptyFile(temporaryPath, cancellationToken);
                    TemporaryOutput.Commit(temporaryPath, job.SolidWorksPath, request.Overwrite);
                    temporaryPath = null;
                    reporter.Report(
                        job.Id,
                        ConversionStage.Completed,
                        $"转换完成，SolidWorks 零件 {output.Length} 字节。{DescribeFeatures(featureOutcome)}",
                        nativeError: saveErrors,
                        nativeWarning: saveWarnings,
                        errorClass: ClassifyCompletedFeatureOutcome(featureOutcome),
                        feature: featureOutcome,
                        artifact: ConversionArtifactKind.SolidWorksPart);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    TryClose(interop, modelObject);
                    throw new OperationCanceledException("SolidWorks 导入已取消。", ex, cancellationToken);
                }
                catch (Exception ex)
                {
                    failed++;
                    TryClose(interop, modelObject);
                    reporter.Report(
                        job.Id,
                        ConversionStage.Failed,
                        "SolidWorks 导入失败：" + ex.Message,
                        true,
                        ex.HResult,
                        errorClass: ClassifyImportError(ex));
                }
                finally
                {
                    TemporaryOutput.DeleteIfExists(temporaryPath);
                    ComRelease.Final(extensionObject);
                    ComRelease.Final(modelObject);
                    ComRelease.Final(importData);
                }
            }

            return failed;
        }
        finally
        {
            // 先还原 FeatureWorks 相关的用户设置，再退出应用。
            recognizer?.Dispose();
            if (interop is not null && featureWorksWasLoaded is bool wasLoaded)
                FeatureRecognizer.RestoreIsolatedSessionState(interop, wasLoaded);
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
                if (ownership.ForcedTerminationUsed)
                {
                    reporter.Report(
                        null,
                        ConversionStage.SolidWorksImport,
                        $"本批创建的 SolidWorks 进程 {ownership.OwnedProcessId} 未正常退出，已按所有权记录回收。",
                        errorClass: ConversionErrorClass.None);
                }
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

    /// <summary>
    /// 恒为 false：**任何情况下都不再重载 FeatureWorks 加载项**。
    ///
    /// 实测（2026-08-05，消解加热板.x_t 与 XJ10B-12导热块.x_t）：
    /// <c>RecognizeFeatureAutomatic</c> 只有在该 SolidWorks **进程**里由人工完成过一次
    /// 特征识别之后才会生效；未激活时恒返回 0，不抛异常、不改几何，是静默失败。
    /// 该激活状态是加载项的进程内状态——不跨进程、正常退出也不写盘。
    ///
    /// 于是本函数原来的两条分支都成了负收益：
    /// * 借用用户会话时重载 → <c>UnloadAddIn</c> 把人工激活一起清掉，整批退化成哑实体；
    /// * 自有新会话时重载 → 那个会话本来就未激活，重载不产生任何收益。
    ///
    /// 参数保留是为了不改调用点与既有测试的形状，语义已作废。
    /// </summary>
    internal static bool ShouldResetFeatureWorksSession(bool resetRequested, bool ownsFreshInstance)
        => false;

    internal static bool ShouldReimportRejectedRecognition(FeatureOutcome? outcome)
        => outcome is { GeometryChanged: true } or { SemanticMismatch: true };

    internal static ConversionErrorClass ClassifyRejectedRecognition(FeatureOutcome outcome)
        => outcome switch
        {
            { SemanticMismatch: true } => ConversionErrorClass.FeatureRecognitionSemanticMismatch,
            { RecognizedFeatureCount: 0 } => ConversionErrorClass.FeatureRecognitionEmpty,
            _ => ConversionErrorClass.FeatureCreationFailed,
        };

    internal static ConversionErrorClass ClassifyCompletedFeatureOutcome(FeatureOutcome? outcome)
        => outcome switch
        {
            { SemanticMismatch: true } => ConversionErrorClass.FeatureRecognitionSemanticMismatch,
            { SketchTotal: > 0 } value when value.SketchFullyDefined < value.SketchTotal
                => ConversionErrorClass.SketchNotFullyDefined,
            _ => ConversionErrorClass.None,
        };

    private static string DescribeFeatures(FeatureOutcome? outcome)
    {
        if (outcome is null)
            return string.Empty;
        if (outcome.DegradedToDumbSolid)
        {
            var reason = string.IsNullOrWhiteSpace(outcome.Diagnostic) ? "" : $"（{outcome.Diagnostic}）";
            return outcome.RecognizedFeatureCount == 0
                ? $" 未识别到特征，输出为导入实体。{reason}"
                : $" 识别 {outcome.RecognizedFeatureCount} 个特征但未能生成，输出为导入实体。{reason}";
        }
        var diagnostic = string.IsNullOrWhiteSpace(outcome.Diagnostic)
            ? string.Empty
            : $"（{outcome.Diagnostic}）";
        return $" 识别 {outcome.RecognizedFeatureCount} 个特征，草图完全定义 {outcome.SketchFullyDefined}/{outcome.SketchTotal}。{diagnostic}";
    }

    /// <summary>
    /// 导入身份校验：SolidWorks 交回的文档必须就是刚导入的那个 XT。
    ///
    /// 实测事故：FeatureWorks 的 COM 服务器故障后，后续 LoadFile4 会交回**上一件的文档**，
    /// 于是接下来每个零件都被存成同一个形状（现场 5 个零件全变成第 7 件那个方块，
    /// 体积与面数逐位相同）。返回 null 表示身份正确。
    /// </summary>
    internal static string? DescribeImportIdentityFailure(string xtPath, string? importedTitle)
    {
        var expected = Path.GetFileNameWithoutExtension(xtPath);
        if (string.IsNullOrWhiteSpace(expected))
            return null;
        if (string.IsNullOrWhiteSpace(importedTitle))
            return $"SolidWorks 未返回文档标题，无法确认导入的是 {expected}。";
        if (importedTitle.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"SolidWorks 交回的文档与导入目标不符：期望 {expected}，实得 {importedTitle}。"
            + " 这通常意味着上一件的文档没有正确关闭，继续下去会把错误几何存成本零件。";
    }

    /// <summary>
    /// V2.3 的首件重试：第一个零件识别为 0 时再导入识别一次。
    ///
    /// 不因 <see cref="FeatureOutcome.SessionNotActivated"/> 而跳过重试：那只是归因猜测，
    /// 一旦猜错就会连 V2.3 这条已被真机验证过的补救一起废掉。多花一件的时间，
    /// 换不误伤，值得。
    /// </summary>
    internal static bool ShouldRetryFirstRecognition(
        int jobIndex,
        int importAttempt,
        bool featureWorksAvailable,
        FeatureOutcome? outcome)
        => jobIndex == 0
           && importAttempt == 0
           && featureWorksAvailable
           && outcome is
           {
               RecognizedFeatureCount: 0,
               DegradedToDumbSolid: true,
           };

    private static ConversionErrorClass ClassifyImportError(Exception exception)
    {
        if (exception is FileNotFoundException missing)
            return ConversionPathLayout.HasExtension(missing.FileName ?? string.Empty, ConversionArtifactKind.SolidWorksPart)
                ? ConversionErrorClass.OutputEmpty
                : ConversionErrorClass.InputMissing;
        if (exception is TimeoutException)
            return ConversionErrorClass.OutputUnstable;
        if (exception is IOException io && io.Message.Contains("保存失败", StringComparison.Ordinal))
            return ConversionErrorClass.SaveFailed;
        return ComErrorClassifier.Classify(exception, ConversionErrorClass.ImportFailed);
    }

    private static ClassifiedConversionException ImportOperationFailed(string operation, Exception exception)
        => new(
            ComErrorClassifier.Classify(exception, ConversionErrorClass.ImportFailed),
            $"{operation} 调用失败：{exception.Message}",
            exception);

    private static void TryClose(SolidWorksInteropBridge? interop, object? modelObject)
    {
        if (interop is null || modelObject is null)
            return;
        TryRun(() =>
        {
            var title = interop.GetTitle(modelObject);
            if (!string.IsNullOrWhiteSpace(title))
                interop.CloseDocument(title);
        });
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private static bool? TryGetBoolean(Func<object> valueFactory)
    {
        try
        {
            return Convert.ToBoolean(valueFactory());
        }
        catch
        {
            return null;
        }
    }
}
