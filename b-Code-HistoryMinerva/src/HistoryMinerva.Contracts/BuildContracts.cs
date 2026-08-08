using System.Text.Json;
using System.Text.Json.Serialization;

namespace SWuse.Contracts;

public static class SWuseIdentity
{
    public const string ModuleName = "HistoryMinerva";
    public const string CommandDomain = "historyminerva";
    public const string WindowId = "historyminerva";
    public const string ApplicationDataDirectoryName = "HistoryMinerva";
    public const string ModuleApplicationDataDirectoryName = "HistoryMinerva";
}

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
