using BaseVariable;

namespace SE2SW;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "mapping";
    public override string Description => "面向受控文件处理的映射与转换模块";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("SE2SW 主程序集未携带版本信息。");
    public override Type? MainClassType => typeof(SE2SWCommands);
}
