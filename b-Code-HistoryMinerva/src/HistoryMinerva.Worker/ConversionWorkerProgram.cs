using System.Text.Json;
using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

internal static class ConversionWorkerProgram
{
    private static readonly JsonSerializerOptions JsonOptions = WorkerProtocol.CreateJsonOptions();

    // 入口在 HistoryMinervaWorkerEntry.cs（STA）。只处理转换动词。
    internal static int Run(string[] args)
    {
        var paths = ReadPaths(args);
        if (paths is null)
        {
            Console.Error.WriteLine(
                $"Usage: {HistoryMinervaIdentity.WorkerFileName} {WorkerProtocol.PartsRequestVerb}|{WorkerProtocol.PartImportVerb}|{WorkerProtocol.AssemblyProbeVerb}|{WorkerProtocol.AssemblyBuildVerb}|{WorkerProtocol.AssemblyRenameVerb}|{WorkerProtocol.AssemblyPackageVerb} "
                + $"<absolute-json-path> {WorkerProtocol.CancellationArgument} <absolute-signal-path>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var cancellationMonitor = MonitorCancellationFileAsync(paths.Value.CancellationPath, cancellation);
        try
        {
            using var messageFilter = OleMessageFilter.Register(TimeSpan.FromSeconds(60), cancellation.Token);
            return paths.Value.Verb.ToLowerInvariant() switch
            {
                WorkerProtocol.PartsRequestVerb => RunParts(Read<BatchRequest>(paths.Value.RequestPath), cancellation.Token),
                WorkerProtocol.PartImportVerb => RunPartImport(Read<PartImportRequest>(paths.Value.RequestPath), cancellation.Token),
                WorkerProtocol.AssemblyProbeVerb => RunProbe(Read<AssemblyProbeRequest>(paths.Value.RequestPath), cancellation.Token),
                WorkerProtocol.AssemblyBuildVerb => RunAssembly(Read<AssemblyBatchRequest>(paths.Value.RequestPath), cancellation.Token),
                WorkerProtocol.AssemblyRenameVerb => RunRename(Read<AssemblyRenameRequest>(paths.Value.RequestPath), cancellation.Token),
                WorkerProtocol.AssemblyPackageVerb => RunPackage(Read<PackageRequest>(paths.Value.RequestPath), cancellation.Token),
                _ => 2,
            };
        }
        catch (OperationCanceledException)
        {
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        finally
        {
            cancellation.Cancel();
            cancellationMonitor.GetAwaiter().GetResult();
        }
    }

    private static int RunParts(BatchRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            var exported = request.SourceFormat == ConversionSourceFormat.SolidWorks
                ? SolidWorksXtExporter.Export(request, reporter, cancellationToken)
                : SolidEdgeExporter.Export(request, reporter, cancellationToken);
            var failed = request.Jobs.Count - exported.Count;
            failed += SolidWorksPartImportIsolation.Import(request, exported, reporter, cancellationToken);
            return failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "批处理已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, ex.Message, true, ex.HResult,
                errorClass: ComErrorClassifier.Classify(ex, ConversionErrorClass.Unknown));
            return 4;
        }
    }

    private static int RunPartImport(PartImportRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            var batchRequest = new BatchRequest(
                request.BatchId,
                request.Mode,
                [request.Job],
                request.Overwrite,
                request.RecognizeFeatures,
                request.FullyDefineSketches,
                request.FeatureRecognitionTimeoutSeconds,
                request.ContinueWhenRecognitionFails,
                request.SourceFormat);
            return SolidWorksImporter.Import(
                batchRequest,
                [request.Job],
                reporter,
                cancellationToken,
                resetFeatureWorksSession: request.RecognizeFeatures,
                useDedicatedSession: request.UseDedicatedSession) == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(request.Job.Id, ConversionStage.Cancelled, "单零件导入已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(
                request.Job.Id,
                ConversionStage.Failed,
                "单零件导入失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ComErrorClassifier.Classify(ex, ConversionErrorClass.ImportFailed));
            return 4;
        }
    }

    private static int RunProbe(AssemblyProbeRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        var isSolidWorksSource = request.SourceFormat == ConversionSourceFormat.SolidWorks;
        reporter.Report(
            null,
            ConversionStage.AssemblyProbe,
            isSolidWorksSource ? "正在解析 SolidWorks 装配体。" : "正在解析 Solid Edge 装配体。");
        try
        {
            var result = isSolidWorksSource
                ? SolidWorksAssemblyExplorer.Probe(request.SourceAssemblyPath, cancellationToken)
                : SolidEdgeAssemblyExplorer.Probe(request.SourceAssemblyPath, cancellationToken);
            var temporary = request.ResultPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporary, request.ResultPath, overwrite: true);
            reporter.Report(null, ConversionStage.Completed,
                $"装配解析完成：{result.Occurrences.Count} 个实例，{result.UniquePartPaths.Count} 个唯一零件。");
            return result.UnresolvedCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "装配解析已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "装配解析失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ConversionErrorClass.AssemblyOpenFailed);
            return 4;
        }
    }

    private static int RunAssembly(AssemblyBatchRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            return AssemblyConverter.Convert(request, reporter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "装配转换已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "装配转换失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ConversionErrorClass.Unknown);
            return 4;
        }
    }

    private static int RunPackage(PackageRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            // 部分失败仍返回 1 而不是 4：几十个零件里有一个导不出来，不该让另外几十个
            // 已经落盘的产物被判成「这一轮什么都没有」。前端按事件逐行显示成败。
            return SolidWorksPackageExporter.Export(request, reporter, cancellationToken) == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "整体打包已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "整体打包失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ComErrorClassifier.Classify(ex, ConversionErrorClass.PackageExportFailed));
            return 4;
        }
    }

    private static int RunRename(AssemblyRenameRequest request, CancellationToken cancellationToken)
    {
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            return SolidWorksDocumentRenamer.Rename(request, reporter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "属性整备改名已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "属性整备改名失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ConversionErrorClass.RenameFailed);
            return 4;
        }
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Worker 请求为空。");

    private static (string Verb, string RequestPath, string CancellationPath)? ReadPaths(IReadOnlyList<string> args)
    {
        if (args.Count != 4 ||
            !WorkerProtocol.IsKnownVerb(args[0]) ||
            !string.Equals(args[2], WorkerProtocol.CancellationArgument, StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.IsPathFullyQualified(args[1]) && File.Exists(args[1]) && Path.IsPathFullyQualified(args[3])
            ? (args[0], args[1], args[3])
            : null;
    }

    private static async Task MonitorCancellationFileAsync(string cancellationPath, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(cancellationPath))
                {
                    cancellation.Cancel();
                    return;
                }
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
