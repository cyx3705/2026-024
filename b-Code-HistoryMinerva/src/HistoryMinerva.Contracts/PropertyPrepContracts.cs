namespace HistoryMinerva.Contracts;

/// <summary>
/// 一个零件要写进 SolidWorks 配置特定属性的那几个值。装配体不写属性，因此只挂在零件条目上。
/// 空串表示这一槽本轮不动，Worker 会跳过它，不会把模板里已有的值抹成空。
/// </summary>
/// <param name="Material">
/// 材质名，例如 <c>304不锈钢</c>。它**不会**被当成文本写进「材料」槽——那一槽是链接，
/// 见 <see cref="PartPropertyNames.Material"/>。必须与 <paramref name="MaterialDatabase"/> 成对出现。
/// </param>
/// <param name="MaterialDatabase">
/// 材质所在的材料库，原样取自 SolidWorks 收藏材料的注册表记录：可能是库名
/// （<c>SOLIDWORKS 材料</c>）也可能是 <c>.sldmat</c> 全路径，两种都能直接喂给
/// <c>SetMaterialPropertyName2</c>，无需归一化。V4.8 追加，缺省空串＝不动零件材质。
/// </param>
/// <param name="Name">
/// 「名称」槽的值，即文件名里图号之后的那一段原零件名称，由
/// <see cref="RenameEntry.PartName"/> 带下来。V4.8 追加，缺省空串＝不动这一槽。
/// 它没有界面入口：名称就是改名用的那个名称，再让用户填一遍只会制造两份真话。
/// </param>
public sealed record PartPropertyWrite(
    string DrawingNumber,
    string Category,
    string Date,
    string Designer,
    string Material,
    string SurfaceTreatment,
    string HeatTreatment,
    string MaterialDatabase = "",
    string Name = "")
{
    /// <summary>材质名与材料库齐备，可以应用到零件。</summary>
    public bool HasMaterial
        => !string.IsNullOrEmpty(Material) && !string.IsNullOrEmpty(MaterialDatabase);

    /// <summary>
    /// 只有材质名没有材料库。<c>SetMaterialPropertyName2</c> 传空库名不会报错，只是**什么都不做**，
    /// 于是又变成「跑成功了但材料还是未指定」。Worker 拿它当硬错误，不让这种请求悄悄过去。
    /// </summary>
    public bool MaterialIsIncomplete
        => !string.IsNullOrEmpty(Material) && string.IsNullOrEmpty(MaterialDatabase);

    /// <summary>全部槽都没得写时，没有必要为它打开一次 SolidWorks 文档。</summary>
    public bool IsEmpty
        => string.IsNullOrEmpty(DrawingNumber)
           && string.IsNullOrEmpty(Name)
           && string.IsNullOrEmpty(Category)
           && string.IsNullOrEmpty(Date)
           && string.IsNullOrEmpty(Designer)
           && string.IsNullOrEmpty(Material)
           && string.IsNullOrEmpty(SurfaceTreatment)
           && string.IsNullOrEmpty(HeatTreatment);

    /// <summary>
    /// 按 <see cref="PartPropertyNames"/> 的顺序展开成「槽名 → 值」，只含非空槽。
    ///
    /// 「材料」一槽给出的是链接记号 <see cref="PartPropertyNames.MaterialLinkValue"/> 而不是材质名：
    /// 材质本身由 <see cref="Material"/> / <see cref="MaterialDatabase"/> 走
    /// <c>SetMaterialPropertyName2</c> 应用，这里写的只是让属性标签认得那条链接。
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Pairs()
    {
        if (!string.IsNullOrEmpty(DrawingNumber)) yield return new(PartPropertyNames.DrawingNumber, DrawingNumber);
        if (!string.IsNullOrEmpty(Name)) yield return new(PartPropertyNames.Name, Name);
        if (!string.IsNullOrEmpty(Category)) yield return new(PartPropertyNames.Category, Category);
        if (!string.IsNullOrEmpty(Date)) yield return new(PartPropertyNames.Date, Date);
        if (!string.IsNullOrEmpty(Designer)) yield return new(PartPropertyNames.Designer, Designer);
        if (HasMaterial) yield return new(PartPropertyNames.Material, PartPropertyNames.MaterialLinkValue);
        if (!string.IsNullOrEmpty(SurfaceTreatment)) yield return new(PartPropertyNames.SurfaceTreatment, SurfaceTreatment);
        if (!string.IsNullOrEmpty(HeatTreatment)) yield return new(PartPropertyNames.HeatTreatment, HeatTreatment);
    }
}

/// <summary>
/// 属性整备清单中的一个唯一文件。<see cref="Properties"/> 是 V4.7 追加的可选字段，
/// 放在末尾且缺省 <c>null</c>：没有它的历史请求 JSON 仍是一次纯改名。
/// </summary>
/// <param name="PartName">
/// 图号之后的那一段原零件名称，与 <see cref="TargetPath"/> 的文件名取自同一次计算。
/// V4.8 追加并写进「名称」属性槽；缺省空串，没有它的历史请求 JSON 语义不变。
/// </param>
public sealed record RenameEntry(
    string Id,
    string SourcePath,
    string TargetPath,
    string DrawingNumber,
    bool AssignsDrawingNumber,
    IReadOnlyList<string> ParentSourcePaths,
    int Depth,
    PartPropertyWrite? Properties = null,
    string PartName = "");

/// <summary>
/// V4.8：解析装配体时从零件身上**读回来**的三个槽的现值。
///
/// 为什么在探查时读而不是写入时读：这三栏本来就大多已经填过——用户是在既有零件上补图号，
/// 不是从空白开始建库。表格开出来一片空白，用户唯一能做的就是把已有的值再选一遍；
/// 选错一格就把零件上原本正确的材质换掉了，而且看不出来。探查时读回来，
/// 表格显示的才是零件的事实，用户只改需要改的那几格。
///
/// 读数走的是装配里已经载入的组件文档，不为此另开零件——一个几百件的装配那样要开几百次。
/// </summary>
/// <param name="Material">
/// 零件当前材质名。取自 <c>GetMaterialPropertyName2</c> 而不是「材料」槽的文本：
/// 那一槽存的是链接记号 <see cref="PartPropertyNames.MaterialLinkValue"/>，不是材质名。
/// </param>
/// <param name="MaterialDatabase">材质所在的材料库，与 <paramref name="Material"/> 同一次调用读出。</param>
public sealed record PartPropertyReading(
    string SourcePath,
    string Material,
    string MaterialDatabase,
    string SurfaceTreatment,
    string HeatTreatment);

/// <summary>按所选装配体层级生成的改名计划。纯内存，不碰 CAD。</summary>
public sealed record AssemblyRenamePlan(
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    IReadOnlyList<RenameEntry> Unnumbered,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    public bool CanRename =>
        BlockingIssues.Count == 0
        && Entries.Any(entry => !SamePath(entry.SourcePath, entry.TargetPath));

    /// <summary>
    /// 「写入」= 改名 + 写属性，因此不能沿用 <see cref="CanRename"/>。
    ///
    /// 第二次点写入时文件名早就对了，<see cref="CanRename"/> 是 false；若拿它当门，
    /// 用户改完材料再点写入就会被拒，只能先把文件改回旧名才能写属性。
    /// </summary>
    public bool CanWrite =>
        BlockingIssues.Count == 0
        && (CanRename || Entries.Any(IsWritablePart));

    /// <summary>属性只写识别出的零件：装配体和未编号的内部件都不碰。</summary>
    public static bool IsWritablePart(RenameEntry entry)
        => entry.AssignsDrawingNumber
           && ConversionPathLayout.HasExtension(
               entry.SourcePath, ConversionPathLayout.SolidWorksPartExtension);

    public static bool SamePath(string left, string right)
        => Path.IsPathFullyQualified(left)
           && Path.IsPathFullyQualified(right)
           && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Worker <c>--rename-assembly</c> 请求。字段只追加，不插入；
/// <see cref="SourceFormat"/> 与 <see cref="StripBySpace"/> 缺省 SolidWorks / 按图号改名，
/// V4.7 追加的 <see cref="WriteProperties"/> 缺省 false，历史请求仍是一次纯改名。
/// </summary>
/// <param name="WriteProperties">
/// 为 true 时，改名与引用更新全部成功后再逐个打开零件写 <see cref="RenameEntry.Properties"/>。
/// 与 <see cref="StripBySpace"/> 互斥：洗图号是反向操作，只清空「图号」槽，不写其余六槽。
/// </param>
public sealed record AssemblyRenameRequest(
    string BatchId,
    string SourceAssemblyPath,
    string DrawingPrefix,
    IReadOnlyList<RenameEntry> Entries,
    ConversionSourceFormat SourceFormat = ConversionSourceFormat.SolidWorks,
    bool StripBySpace = false,
    bool WriteProperties = false);
