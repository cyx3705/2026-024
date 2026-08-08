using System.Text.Json;
using System.Text.Json.Serialization;

namespace SWuse.Contracts;

// 模块名称常量已并入 SE2SW.Contracts.HistoryMinervaIdentity 唯一权威源，本文件不再持有身份字面量。

public enum BuildDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record BuildDiagnostic(
    BuildDiagnosticSeverity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null,
    int? Column = null);

public sealed record SWuseBuildRequest(
    string WorkspacePath,
    string OutputPartPath,
    IReadOnlyList<string> SourceFiles,
    string? EntryType = null,
    bool DryRun = false,
    bool Overwrite = false);

public sealed record SWuseBuildResult(
    bool Success,
    string Summary,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    string? OutputPartPath = null,
    long ElapsedMilliseconds = 0);

public static class SWuseJson
{
    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}
