namespace HistoryMinerva;

public static class WorkerLocator
{
    public static string Locate(MappingRuntimePaths? runtimePaths = null)
        => (runtimePaths ?? MappingRuntimePaths.CreateAppShellFallback()).LocateWorker();

    public static IReadOnlyList<string> Candidates(MappingRuntimePaths? runtimePaths = null)
        => (runtimePaths ?? MappingRuntimePaths.CreateAppShellFallback()).WorkerCandidates();
}
