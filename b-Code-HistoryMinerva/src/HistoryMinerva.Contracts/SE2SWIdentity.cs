namespace SE2SW.Contracts;

/// <summary>
/// 不随宿主位置变化的内部 SE2SW 运行资产常量。
/// </summary>
public static class SE2SWIdentity
{
    public const string ModuleSlotName = "HistoryMinerva";
    public const string WorkerFileName = "HistoryMinerva.Worker.exe";
    public const string RequestsDirectoryName = "requests";
    public const string ProbesDirectoryName = "probes";
}
