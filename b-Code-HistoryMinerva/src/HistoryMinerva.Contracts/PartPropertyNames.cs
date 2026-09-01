namespace HistoryMinerva.Contracts;

/// <summary>
/// SolidWorks 零件属性标签模板里那八个属性槽的名字，以及两个固定值。
///
/// 权威来源是属性标签模板文件本身，而不是界面上看到的标题：
/// `2026-025-实验室测绘/z-SW模板配置/03 属性模板/精密零件属性.prtprp`。
/// 该文件是 XML，每个 <c>&lt;Control&gt;</c> 的 <c>Label</c>（界面标题）与
/// <c>PropName</c>（真实属性名）是**两个字段**，二者可以不同——「类型选择」就是这样一例。
///
/// **为什么必须集中在这里**：写属性用的是 <c>CustomPropertyManager.Add3</c>，它按名字找槽。
/// 名字差一个字不会报错，而是**新建一个重名槽**——模板槽依旧是空的，工程图明细表照样
/// 取不到值，现场看到的现象是「整备跑成功了但图框还是空的」。
///
/// 同样要紧的是**写去哪一批槽**：该模板 17 个控件全部是 <c>ApplyTo="Config"</c>，
/// 因此七个槽都写**配置特定**属性，不是文档级「自定义」属性。见
/// <c>SolidWorksInteropBridge.GetCustomPropertyManager</c>。
/// </summary>
public static class PartPropertyNames
{
    /// <summary>图号。值与文件名前缀部分完全一致，由 <see cref="DrawingNumber.Text"/> 给出。</summary>
    public const string DrawingNumber = "图号";

    /// <summary>
    /// 名称。V4.8 追加，值是文件名里图号之后的那一段**原零件名称**，与
    /// <see cref="DrawingNumber.FormatFileName"/> 拼出来的名字取自同一个来源。
    ///
    /// 这一槽没有界面入口，也不该有：名称就是改名时用的那个名称，让用户在旁边再填一遍
    /// 只会制造「文件名叫阀体、属性里写着阀盖」的两份真话。因此它随改名一起写，
    /// 由 <see cref="RenameEntry.PartName"/> 带下来。模板里它是自由文本 TextBox
    /// （`Control Label="名称" PropName="名称" ApplyTo="Config" Type="TextBox"`）。
    /// </summary>
    public const string Name = "名称";

    /// <summary>
    /// 类型。本模块只整备机加件，恒为 <see cref="MachinedCategory"/>。
    ///
    /// 名字是「类型选择」而不是「类型」——这一条取自属性标签模板本身
    /// （`精密零件属性.prtprp` 里 <c>Control Label="类型选择" PropName="类型选择"</c>），
    /// 不是从界面标题上猜的。曾经按「类型」写过一版，模板槽因此一直是空的。
    /// </summary>
    public const string Category = "类型选择";

    /// <summary>日期。文本型，格式见 <see cref="DateFormat"/>。</summary>
    public const string Date = "日期";

    /// <summary>设计。页面上「图号前缀」同一行的文本框一次刷满全部零件。</summary>
    public const string Designer = "设计";

    /// <summary>
    /// 材料。**这一槽不是自由文本**。
    ///
    /// 模板里它是 <c>Mode="SWProperty"</c>、默认值 <c>SW-Material</c>，也就是一条指向零件
    /// 实体材质的链接。往它写「45钢」这样的字符串，属性标签上什么都不会变——那一栏读的是
    /// 零件材质，不是这个字符串——而链接反倒被换成了静态文本。
    ///
    /// 因此写材料是**两步**：先 <c>IPartDoc.SetMaterialPropertyName2</c> 把材质应用到零件，
    /// 再把这一槽写回 <see cref="MaterialLinkValue"/> 让链接成立。候选只取用户在 SolidWorks 里
    /// 收藏的材料，见 <c>SolidWorksPropertyOptions</c>。
    /// </summary>
    public const string Material = "材料";

    /// <summary>
    /// 表面处理。模板里是 <c>ComboBox</c>，候选写死在模板的 <c>&lt;Data SourceType="List"&gt;</c> 里；
    /// 界面同样只让选不让填，见 <see cref="FallbackSurfaceTreatments"/>。
    /// </summary>
    public const string SurfaceTreatment = "表面处理";

    /// <summary>热处理。同 <see cref="SurfaceTreatment"/>，候选见 <see cref="FallbackHeatTreatments"/>。</summary>
    public const string HeatTreatment = "热处理";

    /// <summary>
    /// 「材料」槽的链接值。SolidWorks 认得这个记号，取值时解析成零件当前的材质名。
    /// 与 <c>SW-Mass</c>（模板里「质量」槽用的）同一族。
    /// </summary>
    public const string MaterialLinkValue = "SW-Material";

    /// <summary>
    /// 下拉里表示「这一槽本轮不写」的那一项。
    ///
    /// 三个下拉都必须有它：Aurora 的选项框只能在候选之间轮换，没有「清空」这个动作，
    /// 少了这一项用户点错一次就再也改不回不写了。落到模型层它一律折成空串。
    /// </summary>
    public const string NoWriteOption = "（不写）";

    /// <summary>
    /// 「类型」的固定值。本模块只整备机加件，不给界面开这个选项。
    /// 模板里这一栏是单选组，四个候选为 部件\组件 / 标准件 / 机加件 / 工件。
    /// </summary>
    public const string MachinedCategory = "机加件";

    /// <summary>
    /// 日期格式。写的是**文本型**属性而不是 <c>swCustomInfoDate</c>：
    /// 日期型属性被明细表取值时会带上时间与本地化格式，图框上就变成了两种写法。
    /// </summary>
    public const string DateFormat = "yyyy/MM/dd";

    /// <summary>按写入顺序列出全部八个槽名，供门禁核对与文档生成。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DrawingNumber,
        Name,
        Category,
        Date,
        Designer,
        Material,
        SurfaceTreatment,
        HeatTreatment,
    ];

    /// <summary>系统当日日期文本。「一键设置日期」与 Worker 侧回填共用同一格式。</summary>
    public static string Today() => DateTime.Now.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 「表面处理」候选的兜底副本，抄自属性标签模板 <c>精密零件属性.prtprp</c>。
    ///
    /// 正常路径是运行时去读模板本身（模板目录记在 SolidWorks 系统选项里，见
    /// <c>SolidWorksPropertyOptions</c>），这份常量只在读不到时顶上——例如这台机器没装
    /// SolidWorks、或用户把模板目录指到了别处。**不要拿它当权威**：模板改了它不会跟着改。
    /// </summary>
    public static IReadOnlyList<string> FallbackSurfaceTreatments { get; } =
    [
        "无",
        "本色阳极氧化",
        "镀硬铬",
        "镀化学镍",
        "镀白锌",
        "喷塑 RAL9003",
        "喷塑 RAL7035",
        "喷塑 RAL1012",
        "喷塑 RAL1013",
        "喷塑 RAL1018",
        "喷塑 RAL3020",
        "表面拉丝",
    ];

    /// <inheritdoc cref="FallbackSurfaceTreatments"/>
    public static IReadOnlyList<string> FallbackHeatTreatments { get; } =
    [
        "无",
        "退火",
        "淬火HRC58-63",
        "调质处理",
    ];
}
