using System.IO;
using System.Reflection;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class MappingRuntimePaths
{
    public MappingRuntimePaths(string hostDataDirectory, string? configuredModuleDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDataDirectory);
        HostDataDirectory = Path.GetFullPath(hostDataDirectory);
        ModuleDataDirectory = Path.Combine(HostDataDirectory, "HistoryMinerva");
        ModuleDirectory = string.IsNullOrWhiteSpace(configuredModuleDirectory)
            ? Path.Combine(HostDataDirectory, "Modules", SE2SWIdentity.ModuleSlotName)
            : Path.Combine(Path.GetFullPath(configuredModuleDirectory), SE2SWIdentity.ModuleSlotName);
    }

    public string HostDataDirectory { get; }
    public string ModuleDataDirectory { get; }
    public string ModuleDirectory { get; }
    public string RequestsDirectory => Path.Combine(ModuleDataDirectory, SE2SWIdentity.RequestsDirectoryName);
    public string ProbesDirectory => Path.Combine(ModuleDataDirectory, SE2SWIdentity.ProbesDirectoryName);

    public string LocateWorker()
    {
        foreach (var candidate in WorkerCandidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return WorkerCandidates()[0];
    }

    public IReadOnlyList<string> WorkerCandidates()
    {
        var candidates = new List<string>();
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            candidates.Add(Path.Combine(Path.GetDirectoryName(assemblyLocation)!, SE2SWIdentity.WorkerFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, SE2SWIdentity.WorkerFileName));
        candidates.Add(Path.Combine(ModuleDirectory, SE2SWIdentity.WorkerFileName));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static MappingRuntimePaths CreateAppShellFallback()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppShell"));
}
