namespace SE2SW.Contracts;

public enum ConversionMode
{
    Ohs,
    External,
}

public enum ConversionStage
{
    Queued,
    SolidEdgeExport,
    SolidWorksImport,
    FeatureRecognition,
    SketchFullyDefine,
    Completed,
    Skipped,
    Failed,
    Cancelled,
    AssemblyProbe,
    AssemblyBuild,
}

public enum ConversionErrorClass
{
    None,
    ComNotRegistered,
    AppLaunchFailed,
    LicenseUnavailable,
    InputMissing,
    InputInvalid,
    InputLocked,
    OutputExists,
    OutputNotWritable,
    OutputEmpty,
    OutputUnstable,
    OutputFormatInvalid,
    CallRejected,
    Timeout,
    Cancelled,
    ExportFailed,
    ImportFailed,
    SaveFailed,
    CadProcessOwnershipUnknown,
    FeatureWorksUnavailable,
    FeatureRecognitionEmpty,
    FeatureCreationFailed,
    SketchNotFullyDefined,
    Unknown,
    AssemblyOpenFailed,
    OccurrenceUnresolved,
    DuplicateOutputName,
    AssemblyTemplateMissing,
    ComponentInsertFailed,
    ComponentTransformFailed,
    AssemblySaveFailed,
    PartStageFailed,
    // V3.3 装配嵌套。枚举按数值序列化，新值只能追加在末尾。
    SubAssemblyBuildFailed,
    SubAssemblyCycleDetected,
    SubAssemblyReuseStale,
    // V3.5 装配关系重建。枚举按数值序列化，新值只能追加在末尾。
    MateEntityUnmatched,
    MateEntityAmbiguous,
    MateRejected,
    MateTypeUnsupported,
    FeatureRecognitionTimeout,
    FeatureRecognitionSemanticMismatch,
}

public static class FeatureRecognitionPolicy
{
    public const int DefaultTimeoutSeconds = 180;
    public const int MaximumTimeoutSeconds = 3600;

    public static int NormalizeTimeoutSeconds(int value)
        => value <= 0 ? DefaultTimeoutSeconds : Math.Min(value, MaximumTimeoutSeconds);
}

public sealed record ConversionJob(
    string Id,
    string SourcePath,
    string XtPath,
    string SolidWorksPath);

public sealed record BatchRequest(
    string BatchId,
    ConversionMode Mode,
    IReadOnlyList<ConversionJob> Jobs,
    bool Overwrite = false,
    // V2.0：FeatureWorks 自动特征识别。不可用时按 ContinueWhenRecognitionFails 决定是否降级。
    bool RecognizeFeatures = true,
    // V2.0：对识别出的每个草图执行"完全定义草图"。依赖 RecognizeFeatures。
    bool FullyDefineSketches = true,
    int FeatureRecognitionTimeoutSeconds = FeatureRecognitionPolicy.DefaultTimeoutSeconds,
    bool ContinueWhenRecognitionFails = true);

/// <summary>
/// V3.5.2：父 Worker 向单零件导入子 Worker 发送的内部请求。
/// 每个请求只允许一个已经导出 XT 的零件，以隔离 FeatureWorks 的 COM 生命周期。
/// </summary>
public sealed record PartImportRequest(
    string BatchId,
    ConversionMode Mode,
    ConversionJob Job,
    bool Overwrite = false,
    bool RecognizeFeatures = true,
    bool FullyDefineSketches = true,
    int FeatureRecognitionTimeoutSeconds = FeatureRecognitionPolicy.DefaultTimeoutSeconds,
    bool ContinueWhenRecognitionFails = true,
    // V3.6.5：上一次尝试里 FeatureWorks 崩了就置为 true，子 Worker 会自己启动一个
    // 专属 SolidWorks 进程，而不是再附着回那个已经损坏的会话。
    bool UseDedicatedSession = false);

/// <summary>V2.0 单个零件的特征识别与草图定义结果，随 Completed 事件回传。</summary>
public sealed record FeatureOutcome(
    int RecognizedFeatureCount,
    bool FeaturesCreated,
    int SketchTotal,
    int SketchFullyDefined,
    IReadOnlyList<string> SketchStatuses,
    bool DegradedToDumbSolid,
    long ElapsedMilliseconds,
    string? Diagnostic = null,
    // 特征识别把实体几何改坏了（例如只认出基体、重建成一个方块）。
    // 与 DegradedToDumbSolid 不同：那只是"没识别出来，实体原样保留"，
    // 这个是"实体已经被改了，必须重新导入才能拿回正确几何"。
    bool GeometryChanged = false,
    // FeatureWorks 的 COM 服务器已故障。调用方必须重建会话，
    // 否则后续每个零件都会对着同一个死对象重试到批次结束。
    bool SessionFaulted = false,
    // FeatureWorks 返回成功，但特征树包含钣金类型或仍只有导入体。
    // 几何可能未改变，调用方仍必须丢弃该文档并重新导入为哑实体。
    bool SemanticMismatch = false,
    // 本 SolidWorks 会话未激活 FeatureWorks：SetAdvancedOptions 返回 false。
    // 实测该返回值与识别成败逐次吻合，且重试 12 次也翻不过来（见 40 号文档）。
    // 调用方应当立即停止对本批次继续尝试识别，而不是每个零件白跑一遍。
    bool SessionNotActivated = false);

public sealed record WorkerEvent(
    string BatchId,
    string? JobId,
    ConversionStage Stage,
    string Message,
    bool IsError = false,
    int? HResult = null,
    int? NativeError = null,
    int? NativeWarning = null,
    ConversionErrorClass ErrorClass = ConversionErrorClass.None,
    FeatureOutcome? Feature = null,
    AssemblyOutcome? Assembly = null,
    ConversionArtifactKind? Artifact = null,
    ReuseKind? ReuseKind = null,
    // V3.5：配合重建结果。与 Feature / Assembly 并列，可空追加不影响既有反序列化。
    MateOutcome? Mate = null);

/// <summary>装配树中的一个实例。Solid Edge GetMatrix 已返回顶层世界矩阵。</summary>
public sealed record AssemblyOccurrence(
    string OccurrenceId,
    string? ParentId,
    string SourcePath,
    bool IsSubAssembly,
    bool IsSuppressed,
    bool IsHidden,
    double[] WorldTransform,
    string? Diagnostic);

/// <summary>
/// V3.3：某个装配文档里的一个直接子项。
///
/// <paramref name="LocalTransform"/> 的元素布局与 <see cref="AssemblyOccurrence.WorldTransform"/>
/// 完全一致（16 元素行主序，旋转 0,1,2/4,5,6/8,9,10，平移 12..14，米），
/// 差别只在参考系：它相对**本装配自己的原点**，不是顶层世界系。
/// 顶层节点的局部矩阵恰好等于世界矩阵，因为顶层的参考系就是世界系。
/// </summary>
public sealed record AssemblyChild(
    string Name,
    string SourcePath,
    bool IsSubAssembly,
    bool IsHidden,
    double[] LocalTransform,
    string? Diagnostic = null);

/// <summary>
/// V3.3：一个装配文档的一级读数。每个唯一 <c>.asm</c> 一条，含顶层。
///
/// 由 Worker 把该 <c>.asm</c> 作为**独立顶层文档**打开后读出——这样读到的矩阵天然就是
/// 该文档坐标系下的局部矩阵，不需要用父级世界矩阵求逆换算。
/// </summary>
public sealed record AssemblyDocumentReading(
    string SourceAssemblyPath,
    IReadOnlyList<AssemblyChild> Children,
    IReadOnlyList<string> Warnings,
    // V3.5：该文档自己那一层的装配关系。为 null 表示本次未采集关系。
    IReadOnlyList<AssemblyRelation>? Relations = null);

/// <summary>
/// V3.5：<c>GetGeometryN</c> 的产物。
///
/// 参考系是**所属装配文档自身的坐标系**——因为该 <c>.asm</c> 是作为独立顶层文档打开的，
/// 它的"世界系"就是它自己（25 号文档 §3.2）。因此这些值可以直接用来在对应的
/// <c>.SLDASM</c> 里定位实体，不需要任何换算。
/// </summary>
/// <param name="GeometryType">1 = 平面（面上一点 + 法向）；2 = 轴（轴上一点 + 方向）。</param>
public sealed record RelationGeometry(
    int GeometryType,
    double[] Point,
    double[] Direction);

/// <summary>
/// V3.5：一条 Solid Edge 装配关系。字段照抄 19 号报告的实测清单，不发明。
///
/// <paramref name="Offset"/> / <paramref name="ParallelOffset"/> 存在条件可读性：
/// Axial 关系在 <c>ParallelOffset == false</c> 时读 <c>Offset</c> 会抛 0x80004005。
/// 采集侧必须先读开关再读值，读不到时留默认值并在 <paramref name="Diagnostic"/> 说明。
/// </summary>
public sealed record AssemblyRelation(
    string SourceAssemblyPath,
    int Index,
    string InterfaceName,
    string? Occurrence1,
    string? Occurrence2,
    RelationGeometry? Geometry1,
    RelationGeometry? Geometry2,
    double Offset = 0,
    bool NormalsAligned = false,
    bool ParallelOffset = false,
    bool IsSuppressed = false,
    string? Diagnostic = null);

/// <summary>V3.5：配合重建结果，随 Completed 事件回传。</summary>
public sealed record MateOutcome(
    int RelationTotal,
    int MateRebuilt,
    int SkippedSuppressed,
    int SkippedUnsupported,
    int FailedUnmatched,
    int FailedAmbiguous,
    int FailedRejected,
    int ComponentsLeftFixed,
    double MaxDriftMeters,
    IReadOnlyList<string> Diagnostics,
    // 接地关系的去向：不产生配合，落为"固定该组件"。
    int GroundApplied = 0)
{
    /// <summary>§6.1 判据 3：每条关系都必须有确定去向，不允许凭空消失。</summary>
    public bool IsSelfConsistent =>
        MateRebuilt + GroundApplied + SkippedSuppressed + SkippedUnsupported
            + FailedUnmatched + FailedAmbiguous + FailedRejected == RelationTotal;
}

/// <summary>
/// V3.3：一个待生成的 <c>.SLDASM</c> 及其直接子项。
/// 列表由 <c>AssemblyGraphBuilder</c> 按拓扑序产出：被依赖者在前，顶层在最后。
/// </summary>
public sealed record AssemblyNode(
    string SourceAssemblyPath,
    string OutputPath,
    bool IsRoot,
    int Depth,
    IReadOnlyList<AssemblyChild> Children,
    IReadOnlyList<string> Dependencies);

public sealed record AssemblyProbeRequest(
    string BatchId,
    string SourceAssemblyPath,
    string ResultPath);

public sealed record AssemblyProbeResult(
    string SourceAssemblyPath,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    IReadOnlyList<string> UniquePartPaths,
    int SuppressedCount,
    int UnresolvedCount,
    int OrderedPartCount,
    int SynchronousPartCount,
    IReadOnlyList<string> Warnings,
    // V3.3：逐文档的一级读数。为 null 表示 V3.0 格式的旧结果（只能展平）。
    IReadOnlyList<AssemblyDocumentReading>? Documents = null);

public sealed record AssemblyBatchRequest(
    string BatchId,
    ConversionMode Mode,
    string SourceAssemblyPath,
    string AssemblyOutputPath,
    IReadOnlyList<ConversionJob> PartJobs,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    bool Overwrite = false,
    bool RecognizeFeatures = false,
    bool FullyDefineSketches = false,
    // V3.6.5：默认**生成缺件装配体**。个别零件转换失败（例如源 .par 文件名编码损坏、
    // Solid Edge SaveBody 直接 E_FAIL）不该让整个装配体颗粒无收——
    // 现场 54 个零件里只有 1 个导不出，却因此不生成任何 SLDASM。
    bool ContinueWhenPartFails = true,
    // V3.5：把 SE 装配关系翻译成 SW 配合。默认关闭，不改既有行为。
    bool RebuildMates = false,
    int FeatureRecognitionTimeoutSeconds = FeatureRecognitionPolicy.DefaultTimeoutSeconds,
    // V3.3：拓扑序的装配节点。为 null 时退化为 V3.0 的展平行为。
    IReadOnlyList<AssemblyNode>? Nodes = null,
    // V3.5：全部层的装配关系，按 SourceAssemblyPath 分派到各层。
    IReadOnlyList<AssemblyRelation>? Relations = null);

/// <summary>
/// 装配转换结果。
///
/// V3.3 起 <paramref name="ComponentTotal"/> / <paramref name="ComponentInserted"/> 的语义变了：
/// 从"全部叶零件"变为"**全部装配节点的直接子项之和**"。嵌套装配里同一个叶零件只被它的直接父级计一次。
/// </summary>
public sealed record AssemblyOutcome(
    int ComponentTotal,
    int ComponentInserted,
    int ComponentFixed,
    int PartConverted,
    int PartFailed,
    int SkippedSuppressed,
    double MaxOriginDeviationMeters,
    long ElapsedMilliseconds,
    string? Diagnostic,
    // V3.3 装配嵌套。
    int SubAssemblyTotal = 0,
    int SubAssemblyBuilt = 0,
    int MaxDepth = 1,
    // V3.5：配合重建结果，按层合并。
    MateOutcome? Mate = null,
    // V3.3.2: Reused assemblies are not reopened, inserted, or fixed during this run.
    int ReusedAssemblyCount = 0,
    int ReusedAssemblyPlannedComponentCount = 0);
