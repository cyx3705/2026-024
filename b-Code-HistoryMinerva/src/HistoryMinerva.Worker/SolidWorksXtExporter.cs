using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// SolidWorks 源零件的 Parasolid 批量导出：<c>.SLDPRT → .x_t</c>。
///
/// 与 <see cref="SolidEdgeExporter"/> 对位——那边是 <c>.par → .x_t</c>，这边是
/// <c>.SLDPRT → .x_t</c>，签名和契约刻意保持一致，好让两种源汇入同一条
/// XT → SW 导入管线，不必为 SolidWorks 源另设一套整备实现。
///
/// 为什么要绕这一圈（2026-08-12 真机实测，12 位消解器样件）：
///
///   同一个会话、同一组识别选项、同一次 <c>RecognizeFeatureAutomatic</c>，
///   从 <c>OpenDoc6</c> 直接打开的 <c>.SLDPRT</c> 哑实体，识别返回 1 却
///   <c>CreateFeatures</c> 建不出任何东西，特征树纹丝不动；把同一个实体导出
///   <c>.x_t</c> 再 <c>LoadFile4</c> 进来，识别同样返回 1，却能干净地建出
///   <c>Sketch1 + Boss-Extrude1</c>——与人工在界面里点出来的结果一致。
///   四块保温棉逐件复现，且生产原有的 <c>CreateFeatures(1)</c> 选项就够用。
///
///   也就是说 Parasolid 往返把 B-rep 重新推导了一遍，FeatureWorks 才认。
///   这不是性能取舍，是识别能不能成立的前提。
///
/// 只导出，不识别、不落 SLDPRT：识别与产物落盘仍归 <see cref="SolidWorksImporter"/>。
/// 源文件全程只读——工作副本走 <see cref="TemporaryOutput.For"/> 的唯一临时名打开，
/// 因此不会与会话里可能开着的同名源零件撞标题
/// （<c>swFileWithSameTitleAlreadyOpen</c>）。
/// </summary>
internal static class SolidWorksXtExporter
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;
    private const int SaveAsCopy = 2;
    private const int SaveAsSilentCopy = SaveAsSilent | SaveAsCopy;
    private const int ParasolidOutputVersionPreference = 89;
    private const int ParasolidOutputVersionLatest = 0;

    /// <returns>成功导出 XT 的任务。失败项已通过 <paramref name="reporter"/> 如实上报并被剔除。</returns>
    public static IReadOnlyList<ConversionJob> Export(
        BatchRequest request,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        bool useDedicatedSession = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);
        if (request.Jobs.Count == 0)
            return [];
        if (request.SourceFormat != ConversionSourceFormat.SolidWorks)
        {
            throw new InvalidOperationException(
                $"{nameof(SolidWorksXtExporter)} 只处理 SolidWorks 源，实得 {request.SourceFormat}。");
        }

        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        bool? originalCommandInProgress = null;
        var exported = new List<ConversionJob>(request.Jobs.Count);

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
                        request.Jobs[0].Id,
                        ConversionStage.SolidWorksImport,
                        $"本批 XT 导出改用专属 SolidWorks 进程（PID {dedicatedProcessId}）。");
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
                    "正在使用你已打开的 SolidWorks 会话导出 XT，本次转换不会关闭它。",
                    errorClass: ConversionErrorClass.CadProcessOwnershipUnknown);
            }
            else
            {
                originalCommandInProgress = TryGetBoolean(() => application.CommandInProgress);
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
                TryRun(() => application.CommandInProgress = true);
            }

            foreach (var job in request.Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ExportOne(interop, job, request.Overwrite, reporter, cancellationToken))
                    exported.Add(job);
            }

            return exported;
        }
        finally
        {
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
            if (ownership.OwnsInstance && !ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)))
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

    private static bool ExportOne(
        SolidWorksInteropBridge interop,
        ConversionJob job,
        bool overwrite,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Report(
            job.Id,
            ConversionStage.SolidWorksExport,
            "正在从 SolidWorks 零件导出 Parasolid。",
            artifact: ConversionArtifactKind.Xt);

        string? workingCopy = null;
        string? temporaryXt = null;
        object? model = null;
        var title = string.Empty;

        try
        {
            // 源零件全程只读：SolidWorks 打开的永远是这份唯一命名的副本。
            workingCopy = TemporaryOutput.For(Path.ChangeExtension(job.SolidWorksPath, ".SLDPRT"));
            File.Copy(job.SourcePath, workingCopy, overwrite: false);
            temporaryXt = TemporaryOutput.For(job.XtPath);

            model = interop.OpenPart(workingCopy, out var openErrors, out var openWarnings);
            if (model is null || openErrors != 0)
            {
                var conflict = interop.DescribeTitleConflict(workingCopy);
                throw new ClassifiedConversionException(
                    ConversionErrorClass.ImportFailed,
                    conflict is null
                        ? $"打开源零件副本失败：errors={openErrors}, warnings={openWarnings}"
                        : $"打开源零件副本失败。{conflict}");
            }
            if (interop.GetDocumentType(model) != DocumentTypePart)
                throw new InvalidDataException("SolidWorks 打开的副本不是零件文档。");

            title = interop.GetTitle(model);
            var identityFailure = SolidWorksPartPreparer.DescribeIdentityFailure(workingCopy, title);
            if (identityFailure is not null)
                throw new InvalidDataException(identityFailure);

            // 与整备链路共用同一份导出实现：非空、稳定、且真的是 Parasolid 文本。
            SaveAsParasolid(interop, model, temporaryXt, cancellationToken);

            interop.CloseDocument(title);
            title = string.Empty;
            ComRelease.Final(model);
            model = null;

            var facts = new FileInfo(temporaryXt);
            TemporaryOutput.Commit(temporaryXt, job.XtPath, overwrite);
            temporaryXt = null;

            reporter.Report(
                job.Id,
                ConversionStage.SolidWorksExport,
                $"Parasolid 导出完成，{facts.Length} 字节。",
                artifact: ConversionArtifactKind.Xt);
            return true;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            TryClose(interop, title);
            throw new OperationCanceledException("SolidWorks Parasolid 导出已取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            TryClose(interop, title);
            reporter.Report(
                job.Id,
                ConversionStage.Failed,
                "SolidWorks Parasolid 导出失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ComErrorClassifier.Classify(ex, ConversionErrorClass.ExportFailed),
                artifact: ConversionArtifactKind.Xt);
            return false;
        }
        finally
        {
            TemporaryOutput.DeleteIfExists(temporaryXt);
            TemporaryOutput.DeleteIfExists(workingCopy);
            ComRelease.Final(model);
        }
    }

    /// <summary>
    /// 把**已经打开**的零件文档导出为 Parasolid 文本。调用方保有文档所有权，本方法不关闭它。
    ///
    /// <see cref="SolidWorksPartPreparer"/> 的整备链路和上面的批量导出共用这一份实现——
    /// 导出这一步的失败判据（SaveAs3 返回值、错误码、落盘稳定性、Parasolid 文本校验）
    /// 只能有一套，否则两条路会在"什么算导出成功"上慢慢分叉。
    /// </summary>
    public static void SaveAsParasolid(
        SolidWorksInteropBridge interop,
        object model,
        string xtPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interop);
        ArgumentNullException.ThrowIfNull(model);
        object? extension = null;
        try
        {
            // 当前文档是零件。另存为 .x_t 必须带 Copy，否则 SW 会试图把活动文档
            // 换成 Parasolid，会话偏好若是二进制就会写出非文本，校验报「格式导出失败」。
            _ = interop.SetUserPreferenceInteger(
                ParasolidOutputVersionPreference, ParasolidOutputVersionLatest);
            extension = interop.GetExtension(model);
            if (!interop.SaveAs3(
                    extension, xtPath, SaveAsCurrentVersion, SaveAsSilentCopy,
                    out var saveErrors, out var saveWarnings)
                || saveErrors != 0)
            {
                throw new IOException($"Parasolid 导出失败，错误码 {saveErrors}，警告码 {saveWarnings}。");
            }
        }
        finally
        {
            ComRelease.Final(extension);
        }

        _ = FileProbe.WaitForStableNonEmptyFile(xtPath, cancellationToken);
        _ = FileProbe.VerifyParasolidText(xtPath, cancellationToken, TimeSpan.FromSeconds(5));
    }

    private static void TryClose(SolidWorksInteropBridge? interop, string title)
    {
        if (interop is null || string.IsNullOrWhiteSpace(title))
            return;
        TryRun(() => interop.CloseDocument(title));
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
