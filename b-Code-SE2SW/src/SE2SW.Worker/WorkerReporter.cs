using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed class WorkerReporter(string batchId, JsonSerializerOptions jsonOptions)
{
    private readonly object _gate = new();

    public void Report(
        string? jobId,
        ConversionStage stage,
        string message,
        bool isError = false,
        int? hResult = null,
        int? nativeError = null,
        int? nativeWarning = null,
        ConversionErrorClass errorClass = ConversionErrorClass.None,
        FeatureOutcome? feature = null,
        AssemblyOutcome? assembly = null,
        ConversionArtifactKind? artifact = null,
        ReuseKind? reuseKind = null,
        MateOutcome? mate = null)
    {
        var payload = new WorkerEvent(
            batchId,
            jobId,
            stage,
            message,
            isError,
            hResult,
            nativeError,
            nativeWarning,
            errorClass,
            feature,
            assembly,
            artifact,
            reuseKind,
            mate);
        Write(payload);
    }

    public void Forward(WorkerEvent workerEvent)
    {
        Write(workerEvent with { BatchId = batchId });
    }

    private void Write(WorkerEvent payload)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
            Console.Out.Flush();
        }
    }
}
