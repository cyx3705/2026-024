using BaseVariable;
using SE2SW.Contracts;

namespace SWuse;

/// <summary>
/// HistoryMinerva 模块入口。4.2.0 破坏性重构：Mapping（SE2SW）与 SWuse 合并为单一模块，
/// 模块名与命令域统一来自 <see cref="HistoryMinervaIdentity"/> 权威源；映射停靠页即模块唯一页面。
/// </summary>
public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => HistoryMinervaIdentity.Name;
    public override string Description => "HistoryMinerva CAD 模块：Solid Edge → SolidWorks 映射转换与 SolidWorks 建模后端";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("HistoryMinerva 主程序集未携带版本信息。");
    public override Type? MainClassType => typeof(SWuseCommands);
}
