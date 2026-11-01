using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// 读零件「配置特定」自定义属性的那一段 COM 面（探查时读外购件名称）。写属性归 HistoryStrenua。
///
/// 从 <c>SolidWorksInteropBridge</c> 主体里分出来，是因为那个文件已经贴着生产源码
/// 千行上限；这一段本身也是一块自洽的东西——它只和属性标签模板打交道，
/// 与装配创建、配合重建、特征识别互不相干。
/// </summary>
internal sealed partial class SolidWorksInteropBridge
{
    /// <summary>
    /// 取属性管理器。<paramref name="configuration"/> 传空串是文档级「自定义」标签，
    /// 传配置名是「配置特定」标签——**那是两批互不相干的槽**。
    ///
    /// 属性标签模板（`.prtprp`）里每个控件的 <c>ApplyTo</c> 决定它落在哪一批。
    /// 本项目的「精密零件属性」模板 17 个控件全是 <c>ApplyTo="Config"</c>，
    /// 所以必须按配置写；写成文档级不会报任何错，只是那个标签页永远是空的。
    /// </summary>
    public object GetCustomPropertyManager(object extension, string configuration)
        => Invoke(_extensionInterface, extension, "get_CustomPropertyManager", configuration)
            ?? throw new InvalidOperationException("SolidWorks 未返回 CustomPropertyManager。");

    /// <summary>活动配置名。零件通常只有「默认」一个，取它即可与用户手填时落到同一批槽。</summary>
    public string GetActiveConfigurationName(object model)
    {
        object? manager = null;
        object? configuration = null;
        try
        {
            manager = Invoke(_modelInterface, model, "get_ConfigurationManager");
            if (manager is null)
                return string.Empty;
            configuration = Invoke(_configurationManagerInterface, manager, "get_ActiveConfiguration");
            return configuration is null
                ? string.Empty
                : Convert.ToString(Invoke(_configurationInterface, configuration, "get_Name")) ?? string.Empty;
        }
        finally
        {
            ComRelease.One(configuration);
            ComRelease.One(manager);
        }
    }

    /// <summary>
    /// 读一个自定义属性的解析值。
    /// <c>Get6</c> 的 out 参数顺序按官方 Interop 定义，缺一个就是 MissingMethod。
    /// </summary>
    public string GetCustomProperty(object customPropertyManager, string name)
    {
        // Get6(FieldName, UseCached, out ValOut, out ResolvedValOut, out WasResolved, out LinkToProperty)
        // 最后一个 out 是 **bool**（LinkToProperty）。这里曾经传 0，反射按 Boolean& 绑不上，
        // 每个零件都在写完读回那一步抛 ArgumentException——现场表现是"整备全部失败但看不到原因"。
        object?[] parameters = [name, false, string.Empty, string.Empty, false, false];
        _ = Invoke(_customPropertyInterface, customPropertyManager, "Get6", parameters);
        return Convert.ToString(parameters[3]) ?? string.Empty;
    }
}
