using System.Security.Cryptography;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class SolidEdgeExporter
{
    private const string ProgId = "SolidEdge.Application";
    private const string ProcessName = "Edge";
    private const int SaveBodyAsParasolidText = 2;
    private const int ParasolidCurrentVersion = 0;

    public static IReadOnlyList<ConversionJob> Export(
        BatchRequest request,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        object? documentsObject = null;
        bool? originalDisplayAlerts = null;
        var exported = new List<ConversionJob>(request.Jobs.Count);

        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered,
                    "未检测到 Solid Edge COM 注册。");
            try
            {
                applicationObject = Activator.CreateInstance(applicationType)
                    ?? throw new InvalidOperationException("COM 返回了空实例。");
            }
            catch (Exception ex)
            {
                throw new ClassifiedConversionException(
                    ComErrorClassifier.Classify(ex, ConversionErrorClass.AppLaunchFailed),
                    "Solid Edge COM 实例创建失败：" + ex.Message,
                    ex);
            }
            dynamic application = applicationObject;
            application.DoIdle();
            ownership.Resolve(Convert.ToInt64(application.hWnd));
            if (!ownership.OwnsInstance)
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.CadProcessOwnershipUnknown,
                    "无法证明 Solid Edge 实例由本工作进程创建，已停止以保护用户会话。");
            }

            originalDisplayAlerts = Convert.ToBoolean(application.DisplayAlerts);
            application.DisplayAlerts = false;
            application.Visible = false;
            application.DoIdle();
            documentsObject = application.Documents;
            dynamic documents = documentsObject;

            foreach (var job in request.Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reporter.Report(
                    job.Id,
                    ConversionStage.SolidEdgeExport,
                    "正在导出 XT。",
                    artifact: ConversionArtifactKind.Xt);
                object? documentObject = null;
                string? temporaryPath = null;
                // Solid Edge 的 COM 失败大多只给一句 E_FAIL，不带任何上下文。
                // 不记步骤就无法区分"打不开这个 .par"和"打开了但导不出实体"，
                // 现场就出现过整批只有一个零件 E_FAIL、却查不出卡在哪一步。
                var step = "ComputeSourceHash";
                try
                {
                    var sourceHash = ComputeSha256(job.SourcePath);
                    temporaryPath = TemporaryOutput.For(job.XtPath);
                    step = "Documents.Open";
                    documentObject = documents.Open(job.SourcePath);
                    dynamic document = documentObject;
                    application.DoIdle();

                    step = "SaveBody";
                    document.SaveBody(
                        temporaryPath,
                        SaveBodyAsParasolidText,
                        ParasolidCurrentVersion,
                        Type.Missing,
                        Type.Missing);
                    application.DoIdle();
                    step = "Document.Close";
                    document.Close(false);
                    application.DoIdle();
                    ComRelease.Final(documentObject);
                    documentObject = null;

                    step = "VerifyParasolidText";
                    var output = FileProbe.VerifyParasolidText(temporaryPath, cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, ComputeSha256(job.SourcePath)))
                        throw new InvalidDataException("Solid Edge 导出后源 .par 文件内容发生变化。");

                    TemporaryOutput.Commit(temporaryPath, job.XtPath);
                    temporaryPath = null;
                    exported.Add(job);
                    reporter.Report(
                        job.Id,
                        ConversionStage.SolidEdgeExport,
                        $"XT 导出完成，{output.Length} 字节，FORMAT={output.ParasolidFormat}。",
                        artifact: ConversionArtifactKind.Xt);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    TryClose(documentObject, applicationObject);
                    throw new OperationCanceledException("Solid Edge 导出已取消。", ex, cancellationToken);
                }
                catch (Exception ex)
                {
                    TryClose(documentObject, applicationObject);
                    reporter.Report(
                        job.Id,
                        ConversionStage.Failed,
                        $"Solid Edge 导出失败（步骤 {step}，源 {Path.GetFileName(job.SourcePath)}）：{ex.Message}",
                        true,
                        ex.HResult,
                        errorClass: ClassifyExportError(ex));
                }
                finally
                {
                    TemporaryOutput.DeleteIfExists(temporaryPath);
                    ComRelease.Final(documentObject);
                }
            }

            return exported;
        }
        finally
        {
            if (applicationObject is not null)
            {
                dynamic application = applicationObject;
                if (originalDisplayAlerts is bool alerts)
                    TryRun(() => application.DisplayAlerts = alerts);
                TryRun(() => application.DoIdle());
                ComRelease.Final(documentsObject);
                documentsObject = null;
                if (ownership.OwnsInstance)
                    TryRun(() => application.Quit());
            }
            ComRelease.Final(documentsObject);
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private static ConversionErrorClass ClassifyExportError(Exception exception)
    {
        if (exception is FileNotFoundException missing)
            return ConversionPathLayout.HasExtension(missing.FileName ?? string.Empty, ConversionArtifactKind.Xt)
                ? ConversionErrorClass.OutputEmpty
                : ConversionErrorClass.InputMissing;
        if (exception is TimeoutException)
            return ConversionErrorClass.OutputUnstable;
        if (exception is InvalidDataException invalid && invalid.Message.Contains("FORMAT=", StringComparison.Ordinal))
            return ConversionErrorClass.OutputFormatInvalid;
        return ComErrorClassifier.Classify(exception, ConversionErrorClass.ExportFailed);
    }

    private static void TryClose(object? documentObject, object? applicationObject)
    {
        if (documentObject is null)
            return;
        TryRun(() => ((dynamic)documentObject).Close(false));
        if (applicationObject is not null)
            TryRun(() => ((dynamic)applicationObject).DoIdle());
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
}
