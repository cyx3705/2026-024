using System.Security.Cryptography;
using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// V4.10 整体打包的 CAD 导出：零件 <c>→ .STEP</c>、工程图 <c>→ .DWG</c> 与 <c>→ .PDF</c>。
///
/// 与 <see cref="SolidWorksXtExporter"/> 的两点刻意不同：
///
/// <list type="bullet">
///   <item><b>不做工作副本，改为只读打开原件。</b> XT 那条路要复制，是因为随后的导入与
///         特征识别会改文档；这里只是另存为一份新格式，源文档全程不该被改。工程图更是
///         **不能**复制——把 <c>.SLDDRW</c> 复制到 DWG 目录再打开，它对零件的引用就断了，
///         导出来的是一张空图。</item>
///   <item><b>关闭后比对 SHA256。</b>「没有改动源文件」在这条链路上必须是可核验的事实，
///         而不是一句承诺——与 <see cref="SolidWorksAssemblyExplorer"/> 同一条规矩。</item>
/// </list>
///
/// 用户已经开着的文档一律复用、绝不替他关掉，也就不为它比对哈希：那份文档此刻归用户，
/// 他自己可能正在改。
/// </summary>
internal static class SolidWorksPackageExporter
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int DocumentTypeDrawing = 3;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;
    private const int SaveAsCopy = 2;
    private const int SaveAsSilentCopy = SaveAsSilent | SaveAsCopy;

    /// <returns>失败的作业数。0 表示整批导出完成。</returns>
    public static int Export(
        PackageRequest request,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);
        if (request.Jobs.Count == 0)
            return 0;

        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        object? pdfExportData = null;
        bool? originalCommandInProgress = null;
        var failed = 0;

        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered, "未检测到 SolidWorks COM 注册。");
            try
            {
                applicationObject = Activator.CreateInstance(applicationType)
                    ?? throw new InvalidOperationException("COM 返回了空实例。");
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
                    ConversionStage.PackageExport,
                    "正在使用你已打开的 SolidWorks 会话导出打包产物，本次不会关闭它。",
                    errorClass: ConversionErrorClass.CadProcessOwnershipUnknown);
            }
            else
            {
                originalCommandInProgress = TryGetBoolean(() => application.CommandInProgress);
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
                TryRun(() => application.CommandInProgress = true);
            }

            // 一份导出设置整批复用：每张图重建一次只是多几十次 COM 往返。
            pdfExportData = request.Jobs.Any(job => job.Artifact == PackageArtifact.Pdf)
                ? interop.CreatePdfExportData()
                : null;
            if (pdfExportData is null && request.Jobs.Any(job => job.Artifact == PackageArtifact.Pdf))
            {
                reporter.Report(
                    null,
                    ConversionStage.PackageExport,
                    "无法建立 PDF 导出设置，本轮 PDF 按 SolidWorks 会话的当前设置导出；"
                    + "多页工程图可能只导出当前页。");
            }

            foreach (var job in request.Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ExportOne(interop, job, request.Overwrite, pdfExportData, reporter, cancellationToken))
                    failed++;
            }

            return failed;
        }
        finally
        {
            ComRelease.Final(pdfExportData);
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
        PackageJob job,
        bool overwrite,
        object? pdfExportData,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var artifact = ArtifactKindOf(job.Artifact);
        reporter.Report(
            job.Id,
            ConversionStage.PackageExport,
            $"正在导出 {job.Artifact.ToString().ToUpperInvariant()}：{Path.GetFileName(job.SourcePath)}",
            artifact: artifact);

        string? temporaryOutput = null;
        object? model = null;
        object? extension = null;
        var openedHere = false;
        var title = string.Empty;

        try
        {
            if (!File.Exists(job.SourcePath))
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.InputMissing, $"源文件不存在：{job.SourcePath}");
            }

            var sourceHash = ComputeSha256(job.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
            temporaryOutput = TemporaryOutput.For(job.OutputPath);

            // 用户已经开着这个文档时直接复用，绝不替他关掉；自己打开的才由自己关。
            model = interop.FindOpenDocument(job.SourcePath);
            if (model is null)
            {
                int errors;
                int warnings;
                model = job.Artifact == PackageArtifact.Step
                    ? interop.OpenPartReadOnly(job.SourcePath, out errors, out warnings)
                    : interop.OpenDrawingReadOnly(job.SourcePath, out errors, out warnings);
                openedHere = true;
                if (model is null || errors != 0)
                {
                    var conflict = interop.DescribeTitleConflict(job.SourcePath);
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.InputLocked,
                        conflict is null
                            ? $"只读打开源文档失败：errors={errors}, warnings={warnings}"
                            : $"只读打开源文档失败。{conflict}");
                }
            }

            var expectedType = job.Artifact == PackageArtifact.Step ? DocumentTypePart : DocumentTypeDrawing;
            if (interop.GetDocumentType(model) != expectedType)
            {
                throw new InvalidDataException(
                    job.Artifact == PackageArtifact.Step
                        ? "打开的文档不是 SolidWorks 零件。"
                        : "打开的文档不是 SolidWorks 工程图。");
            }

            title = interop.GetTitle(model);
            extension = interop.GetExtension(model);
            // 另存为副本：不带 Copy 时 SolidWorks 会试图把活动文档本身换成目标格式，
            // 而这一路的源文档是只读打开的，那一步只会以一个没有原因的失败告终。
            if (!interop.SaveAs3(
                    extension,
                    temporaryOutput,
                    SaveAsCurrentVersion,
                    SaveAsSilentCopy,
                    job.Artifact == PackageArtifact.Pdf ? pdfExportData : null,
                    out var saveErrors,
                    out var saveWarnings)
                || saveErrors != 0)
            {
                throw new IOException($"导出失败，错误码 {saveErrors}，警告码 {saveWarnings}。");
            }

            var facts = FileProbe.WaitForStableNonEmptyFile(temporaryOutput, cancellationToken);

            if (openedHere)
            {
                ComRelease.Final(extension);
                extension = null;
                interop.CloseDocument(title);
                title = string.Empty;
                ComRelease.Final(model);
                model = null;
                openedHere = false;
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, ComputeSha256(job.SourcePath)))
                    throw new InvalidDataException($"导出后源文件内容发生变化：{job.SourcePath}");
            }

            TemporaryOutput.Commit(temporaryOutput, job.OutputPath, overwrite);
            temporaryOutput = null;

            reporter.Report(
                job.Id,
                ConversionStage.PackageExport,
                $"已导出 {Path.GetFileName(job.OutputPath)}，{facts.Length} 字节。",
                artifact: artifact);
            return true;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            TryClose(interop, openedHere ? title : string.Empty);
            throw new OperationCanceledException("整体打包导出已取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            TryClose(interop, openedHere ? title : string.Empty);
            reporter.Report(
                job.Id,
                ConversionStage.Failed,
                $"{job.Artifact.ToString().ToUpperInvariant()} 导出失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ComErrorClassifier.Classify(ex, ConversionErrorClass.PackageExportFailed),
                artifact: artifact);
            return false;
        }
        finally
        {
            TemporaryOutput.DeleteIfExists(temporaryOutput);
            ComRelease.Final(extension);
            ComRelease.Final(model);
        }
    }

    private static ConversionArtifactKind ArtifactKindOf(PackageArtifact artifact) => artifact switch
    {
        PackageArtifact.Step => ConversionArtifactKind.Step,
        PackageArtifact.Dwg => ConversionArtifactKind.Dwg,
        PackageArtifact.Pdf => ConversionArtifactKind.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null),
    };

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
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
