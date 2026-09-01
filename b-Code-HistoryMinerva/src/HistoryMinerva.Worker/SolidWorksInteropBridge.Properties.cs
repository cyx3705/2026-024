using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// 属性整备用到的那一段 COM 面：读写零件「配置特定」自定义属性，以及应用/读回材质。
///
/// 从 <c>SolidWorksInteropBridge</c> 主体里分出来，是因为那个文件已经贴着生产源码
/// 千行上限；这一段本身也是一块自洽的东西——它只和属性标签模板打交道，
/// 与装配创建、配合重建、特征识别互不相干。
/// </summary>
internal sealed partial class SolidWorksInteropBridge
{
    // ---------------- V4.7：属性整备写自定义属性 ----------------

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

    // swCustomInfoType_e.swCustomInfoText
    private const int CustomInfoText = 30;

    // swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd
    private const int CustomPropertyDeleteAndAdd = 2;

    /// <summary>
    /// 按名字写一个文本型自定义属性。
    ///
    /// 用 <c>swCustomPropertyDeleteAndAdd</c> 而不是 <c>swCustomPropertyOnlyIfNew</c>：
    /// 模板已经把七个槽建好且留空，OnlyIfNew 会认为槽已存在而**一个字都不写**。
    /// 返回值语义按官方文档：0 = 成功，2 = 名字已存在且未替换，其余为失败。
    /// </summary>
    public int AddCustomProperty(object customPropertyManager, string name, string value)
        => Convert.ToInt32(Invoke(
            _customPropertyInterface,
            customPropertyManager,
            "Add3",
            name,
            CustomInfoText,
            value,
            CustomPropertyDeleteAndAdd));

    /// <summary>
    /// 读回一个自定义属性的解析值，用于写入后的自校验。
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

    /// <summary>
    /// 模板已经建好的槽名。
    ///
    /// 写之前拿它对一次：<c>Add3</c> 遇到不存在的名字会**新建**一个槽而不是报错，
    /// 于是模板槽仍旧是空的、明细表照样取不到值，现场看到的是「跑成功了但图框还是空的」。
    /// 这是本功能唯一一种会静默失败的方式，必须在写之前抓住。
    /// </summary>
    public IReadOnlyCollection<string> GetCustomPropertyNames(object customPropertyManager)
    {
        var names = Invoke(_customPropertyInterface, customPropertyManager, "GetNames") as Array;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names is null)
            return result;
        foreach (var name in names)
        {
            var text = Convert.ToString(name);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }

    // swSaveAsOptions_e.swSaveAsOptions_Silent
    private const int SaveSilent = 1;

    /// <summary>就地保存已打开的文档。写完属性必须落盘，否则关文档时那次写入随之丢弃。</summary>
    public bool Save3(object model, out int errors, out int warnings)
    {
        object?[] parameters = [SaveSilent, 0, 0];
        var saved = Convert.ToBoolean(Invoke(_modelInterface, model, "Save3", parameters));
        errors = Convert.ToInt32(parameters[1]);
        warnings = Convert.ToInt32(parameters[2]);
        return saved;
    }

    // ---------------- V4.8：属性整备应用材质 ----------------

    /// <summary>
    /// 把材质应用到零件的某个配置。
    ///
    /// 「材料」属性槽是链接（<c>SW-Material</c>），写文本不改变零件材质，也就改不动属性标签上
    /// 那一栏。真正管用的是这个：<paramref name="database"/> 原样传收藏材料里记的那一串，
    /// 库名（<c>SOLIDWORKS 材料</c>）和 <c>.sldmat</c> 全路径**两种都认**，实测过，不要自作主张归一化。
    ///
    /// 它没有返回值，传错库名或材质名也不抛异常，只是静默不生效——所以调用方必须
    /// 用 <see cref="GetMaterialPropertyName"/> 读回来核一次。
    /// </summary>
    public void SetMaterialPropertyName(object model, string configuration, string database, string name)
        => Invoke(_partInterface, model, "SetMaterialPropertyName2", configuration, database, name);

    /// <summary>读回某配置当前的材质名。返回空串表示「未指定」。</summary>
    public string GetMaterialPropertyName(object model, string configuration)
        => GetMaterialPropertyName(model, configuration, out _);

    /// <summary>
    /// 读回某配置当前的材质名**和它所在的材料库**。
    ///
    /// 库名是 <c>GetMaterialPropertyName2</c> 顺手给出的 out 参数，探查时必须一起收下：
    /// 只有名字的材质应用不回零件（见 <c>SetMaterialPropertyName2</c>），
    /// 表格里预填一个应用不上去的材质，等于把「不用改」写成了「改不了」。
    /// </summary>
    public string GetMaterialPropertyName(object model, string configuration, out string database)
    {
        // GetMaterialPropertyName2(ConfigName, out Database) -> Name
        object?[] parameters = [configuration, string.Empty];
        var name = Convert.ToString(Invoke(_partInterface, model, "GetMaterialPropertyName2", parameters));
        database = Convert.ToString(parameters[1]) ?? string.Empty;
        return name ?? string.Empty;
    }
}
