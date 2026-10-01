namespace HistoryMinerva;

public static class WorkerLocator
{
    public static string Locate(MappingRuntimePaths? runtimePaths = null)
        => (runtimePaths ?? MappingRuntimePaths.Unattached()).LocateWorker();

    public static IReadOnlyList<string> Candidates(MappingRuntimePaths? runtimePaths = null)
        => (runtimePaths ?? MappingRuntimePaths.Unattached()).WorkerCandidates();
}
