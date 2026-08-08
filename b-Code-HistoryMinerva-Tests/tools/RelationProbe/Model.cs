namespace RelationProbe;

/// <summary>
/// 类型库里的一个成员。名称与种类来自 ITypeInfo，不是猜测。
/// </summary>
internal sealed class MemberRecord
{
    public string Name { get; set; } = string.Empty;

    /// <summary>get / put / putref / method。</summary>
    public string Kind { get; set; } = string.Empty;

    public int ParamCount { get; set; }

    public int MemberId { get; set; }

    /// <summary>形参签名。带参方法靠它决定怎么调——按真实 VT 给 byref 变体播种，而不是传空变体。</summary>
    public List<ParameterRecord> Parameters { get; set; } = [];

    public string ReturnType { get; set; } = string.Empty;
}

/// <summary>一个形参。VarType 是解引用后的真实类型，不是 VT_PTR。</summary>
internal sealed class ParameterRecord
{
    public string Name { get; set; } = string.Empty;

    /// <summary>可读签名，例如 <c>VT_PTR→VT_R8 [out]</c>。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>解引用后的 VARTYPE；SAFEARRAY 时是元素类型。</summary>
    public int VarType { get; set; }

    public bool IsSafeArray { get; set; }

    public bool IsIn { get; set; }

    public bool IsOut { get; set; }

    public bool IsOptional { get; set; }
}

/// <summary>
/// 一个 COM 对象的完整快照：它是什么接口、有哪些成员、无参属性读到了什么。
/// </summary>
internal sealed class ComDump
{
    public string InterfaceName { get; set; } = string.Empty;

    /// <summary>接口继承链（含自身），按 ITypeInfo 的 ImplType 顺序展开。</summary>
    public List<string> TypeChain { get; set; } = [];

    public List<MemberRecord> Members { get; set; } = [];

    /// <summary>无参属性读取成功的值，已格式化为字符串。</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>读取失败的成员及其 HRESULT，用于判断"没有这个成员"还是"读不出来"。</summary>
    public Dictionary<string, string> Unreadable { get; set; } = [];

    /// <summary>嵌套对象，例如面的 Geometry。</summary>
    public Dictionary<string, ComDump> Children { get; set; } = [];

    /// <summary>带参方法的调用试探：调用形态 → 结果描述。通用取值只读无参属性，这里补带参方法。</summary>
    public Dictionary<string, string> MethodProbes { get; set; } = [];

    /// <summary>GetElement1/2、GetGeometry1/2 拿到的几何引用对象。</summary>
    public Dictionary<string, ComDump> Elements { get; set; } = [];

    /// <summary>关系两侧的组件归属，标量形式，避免把整个 Occurrence 对象图塞进输出。</summary>
    public Dictionary<string, string> Occurrences { get; set; } = [];
}

/// <summary>装配文档里的一个一级 occurrence，用于导出坐标系核对。</summary>
internal sealed class OccurrenceRecord
{
    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public bool IsSubAssembly { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>16 元素世界矩阵，与 V3.0 口径一致：旋转 0,1,2/4,5,6/8,9,10，平移 12..14。</summary>
    public double[] Matrix { get; set; } = [];

    public double[] Origin { get; set; } = [];
}

/// <summary>装配级 Parasolid 导出的实测结果。</summary>
internal sealed class ExportCheck
{
    public string SourceAssembly { get; set; } = string.Empty;

    /// <summary>实际生效的导出成员名（SaveCopyAs / SaveAs / ...）。</summary>
    public string? ExportMember { get; set; }

    public List<string> Attempts { get; set; } = [];

    public string? OutputPath { get; set; }

    public long OutputBytes { get; set; }

    /// <summary>Parasolid 文件头里读到的 FORMAT 值，V1.1 起全线要求 text。</summary>
    public string? ParasolidFormat { get; set; }

    public int LeafOccurrenceCount { get; set; }

    /// <summary>SolidWorks 导入后落地成什么文档：零件（多体）还是装配体。受 SW 导入选项影响。</summary>
    public string? SolidWorksDocumentType { get; set; }

    /// <summary>用户原本的 swImportMultBodyAsPartData 设置。探针临时置真后必须恢复。</summary>
    public bool? ImportMultiBodyAsPartOriginal { get; set; }

    /// <summary>用户原本的 swImportDissolveTopLevelAssemblyOnOpen 设置。</summary>
    public bool? ImportDissolveAssemblyOriginal { get; set; }

    /// <summary>用户原本的 swImportIgnoreHiddenEntities 设置，关系到隐藏件是否进入产物。</summary>
    public bool? ImportIgnoreHiddenOriginal { get; set; }

    public bool ImportPreferenceRestored { get; set; }

    public int SolidWorksBodyCount { get; set; }

    /// <summary>落地为装配体时的组件数。</summary>
    public int SolidWorksComponentCount { get; set; }

    /// <summary>每个实体的包围盒中心，装配坐标系，米。</summary>
    public List<double[]> BodyCenters { get; set; } = [];

    /// <summary>每个叶 occurrence 的原点，装配坐标系，米。</summary>
    public List<double[]> OccurrenceOrigins { get; set; } = [];

    /// <summary>
    /// 体中心均值 − occurrence 原点均值。接近 0 说明导出几何位于子装配自身坐标系；
    /// 出现整体偏移则说明坐标系判断错了，插入矩阵必须改。
    /// </summary>
    public double[] MeanOffset { get; set; } = [];

    public double MeanOffsetMagnitude { get; set; }

    public bool BodyCountMatches { get; set; }

    public string? Error { get; set; }
}

/// <summary>单个 .asm 文档的探查结果。</summary>
internal sealed class DocumentProbe
{
    public string Path { get; set; } = string.Empty;

    /// <summary>AssemblyDocument 上名字里含 Relation 的成员，用来定位关系集合入口。</summary>
    public List<MemberRecord> RelationEntryPoints { get; set; } = [];

    public string? RelationCollectionMember { get; set; }

    public ComDump? RelationCollection { get; set; }

    public int RelationCount { get; set; }

    public List<ComDump> Relations { get; set; } = [];

    public List<OccurrenceRecord> Occurrences { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public string? Error { get; set; }
}

/// <summary>探针总输出。</summary>
internal sealed class ProbeResult
{
    public string SampleDirectory { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool InPlace { get; set; }

    public bool SampleHashVerified { get; set; }

    public List<string> ChangedSampleFiles { get; set; } = [];

    public bool WorkingCopyHashVerified { get; set; }

    public List<string> ChangedWorkingFiles { get; set; } = [];

    public List<DocumentProbe> Documents { get; set; } = [];

    public List<ExportCheck> ExportChecks { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public string Verdict { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }

    public string? HResult { get; set; }

    public int[] EdgeProcessesAfter { get; set; } = [];

    public int[] SolidWorksProcessesAfter { get; set; } = [];
}
