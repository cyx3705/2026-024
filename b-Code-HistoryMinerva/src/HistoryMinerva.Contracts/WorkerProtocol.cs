using System.Text.Json;

namespace SE2SW.Contracts;

/// <summary>
/// UI 与独立 Worker 共同消费的命令行和 JSON 协议。
/// </summary>
public static class WorkerProtocol
{
    public const string PartsRequestVerb = "--request";
    public const string PartImportVerb = "--import-part";
    public const string AssemblyProbeVerb = "--probe-assembly";
    public const string AssemblyBuildVerb = "--assembly";
    public const string CancellationArgument = "--cancel";

    public static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static bool IsKnownVerb(string? value)
        => value is PartsRequestVerb or PartImportVerb or AssemblyProbeVerb or AssemblyBuildVerb;
}
