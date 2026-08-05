using BaseVariable;

namespace SE2SW;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "se2sw";
    public override string Description => "Solid Edge 零件与嵌套装配体转换为 SolidWorks";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("SE2SW 主程序集未携带版本信息。");
    public override Type? MainClassType => typeof(SE2SWCommands);
}
