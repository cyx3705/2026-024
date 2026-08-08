namespace SE2SW.Contracts;

/// <summary>
/// HistoryMinerva 模块名称唯一权威源。展示名、命令域、窗口标识、部署槽、
/// 数据目录、Worker 文件名一律引用此类常量，代码中不再出现独立字面量。
/// </summary>
public static class HistoryMinervaIdentity
{
    /// <summary>模块名，同时是部署槽名、数据目录名与宿主反射命令域的根。</summary>
    public const string Name = "HistoryMinerva";

    /// <summary>命令域：与宿主按 ModuleName 反射生成的前缀保持一致。</summary>
    public const string CommandDomain = Name;

    /// <summary>映射停靠页标题（去 History 前缀的短形）。</summary>
    public const string WindowTitle = "Minerva";

    /// <summary>工具窗口内部标识与日志类别（不直接显示，保持小写稳定）。</summary>
    public const string WindowId = "historyminerva";

    /// <summary>合并后单个 Worker 可执行文件名。</summary>
    public const string WorkerFileName = Name + ".Worker.exe";

    /// <summary>宿主数据根与本机数据根下的模块数据目录名。</summary>
    public const string DataDirectoryName = Name;

    /// <summary>用户文档目录下的默认建模工作区目录名。</summary>
    public const string WorkspaceDirectoryName = Name + "Workspace";
}
