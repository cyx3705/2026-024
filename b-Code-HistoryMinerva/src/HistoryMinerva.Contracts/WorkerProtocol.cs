using System.Text.Json;

namespace HistoryMinerva.Contracts;

/// <summary>
/// UI 与独立 Worker 共同消费的命令行和 JSON 协议。
/// </summary>
public static class WorkerProtocol
{
    public const string PartsRequestVerb = "--request";
    public const string PartImportVerb = "--import-part";
    public const string AssemblyProbeVerb = "--probe-assembly";
    public const string AssemblyBuildVerb = "--assembly";
    public const string AssemblyRenameVerb = "--rename-assembly";

    /// <summary>V4.10：整体打包的 CAD 导出（STEP / DWG / PDF）。两张 BOM 不走 Worker。</summary>
    public const string AssemblyPackageVerb = "--package-assembly";
    public const string CancellationArgument = "--cancel";

    public static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static bool IsKnownVerb(string? value)
        => value is PartsRequestVerb or PartImportVerb or AssemblyProbeVerb or AssemblyBuildVerb
            or AssemblyRenameVerb or AssemblyPackageVerb;
}
