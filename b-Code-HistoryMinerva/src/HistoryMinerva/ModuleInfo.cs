using BaseVariable;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// HistoryMinerva 模块入口。停靠页只承载四种 CAD 转换；Worker 状态命令由
/// <see cref="WorkerCommands"/> 显式注册。
/// </summary>
public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => HistoryMinervaIdentity.Name;
    public override string Description => "CAD 装配转换";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("HistoryMinerva 主程序集未携带版本信息。");
    public override Type? MainClassType => null;
}
