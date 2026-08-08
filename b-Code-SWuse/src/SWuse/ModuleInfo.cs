using BaseVariable;

namespace SWuse;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => SWuse.Contracts.SWuseIdentity.CommandDomain;
    public override string Description => "SolidWorks C# 静态零件构建实验环境";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("SWuse assembly version is missing.");
    public override Type? MainClassType => typeof(SWuseCommands);
}
