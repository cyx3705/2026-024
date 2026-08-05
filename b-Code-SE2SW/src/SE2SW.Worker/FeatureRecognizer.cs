using System.Diagnostics;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V2.0：对导入后的哑实体执行 FeatureWorks 自动特征识别，再对识别出的每个草图执行完全定义。
/// 设计依据与实测数据见 docs/05-V2.0-特征识别与草图完全定义.md。
/// </summary>
internal sealed class FeatureRecognizer : IDisposable
{
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";

    // swUserPreferenceToggle_e
    private const int SwUseEnglishLanguageFeatureNames = 262;

    // swLoadAddinError_e
    private const int SwLoadAddinSuccess = 0;
    private const int SwLoadAddinAlreadyLoaded = 2;
    private const int SwLoadAddinLicenseError = 7;

    // fwAdvancedOptions_e
    private const short FwAdvAddConstraintsToSketch = 1;
    private const short FwAdvAllowWizardHoleRecognition = 4;

    // fwFeatureCreationOptions_e：不加 fwAllowFailFeatureCreation，
    // 否则会生成带重建错误的特征，宁可整体降级为哑实体。
    private const short FwAddConstraintsToSketch = 1;

    // 普通 .par 只允许机械特征：拉伸、体积、旋转、孔、倒角/圆角、筋。
    // 0x3FF 还会打开 BaseFlange/Bend/EdgeFlange/Hem，实测会把普通零件误识别成钣金。
    internal const int ExtrudeRecognitionOption = 0x01;
    internal const int VolumeRecognitionOption = 0x02;
    internal const int RevolveRecognitionOption = 0x04;
    internal const int HoleRecognitionOption = 0x08;
    internal const int ChamferAndFilletRecognitionOption = 0x10;
    internal const int RibRecognitionOption = 0x20;
    internal const int StandardPartRecognitionOptions =
        ExtrudeRecognitionOption
        | VolumeRecognitionOption
        | RevolveRecognitionOption
        | HoleRecognitionOption
        | ChamferAndFilletRecognitionOption
        | RibRecognitionOption;
    internal const int SheetMetalRecognitionOptions = 0x3C0;
    // swSketchFullyDefineRelationType_e 全部关系类型
    private const int AllSketchRelations = 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 | 512;

    // swConstrainedStatus_e
    private const int SwFullyConstrained = 3;

    private const int DocumentActivationAttempts = 5;
    private const int DocumentActivationDelayMilliseconds = 250;
    private const int RecognitionAttempts = 10;
    private const int RecognitionRetryDelayMilliseconds = 300;

    /// <summary>
    /// FeatureWorks 会按识别结果重建实体，实测有效结果仍可能产生约 1.42e-5 的体积偏差。
    /// 此上限覆盖已验收样件，同时继续拒绝明显改变零件形状的错误识别。
    /// </summary>
    internal const double MaximumFeatureWorksVolumeRelativeDeviation = 2e-5;

    /// <summary>
    /// COM 服务器已死的 HRESULT。实测事故：FeatureWorks 在批次中途故障后，
    /// 每个后续零件都会拿着同一个死对象重试 10 次，全部返回 0x80010105。
    /// </summary>
    private static readonly int[] ServerFaultHResults =
    [
        unchecked((int)0x80010105), // RPC_E_SERVERFAULT
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x80004005), // E_FAIL：FeatureWorks 崩溃后也会以它出现
    ];

    internal static bool IsServerFault(Exception exception)
        => ServerFaultHResults.Contains(exception.HResult);

    private const string SketchFeatureTypeName = "ProfileFeature";
    private const int MarkHorizontalDatum = 2;
    private const int MarkVerticalDatum = 4;

    private readonly SolidWorksInteropBridge _interop;
    private readonly bool? _originalEnglishFeatureNames;
    private object? _featureWorks;

    private FeatureRecognizer(
        SolidWorksInteropBridge interop,
        object? featureWorks,
        bool? originalEnglishFeatureNames,
        ConversionErrorClass unavailableReason,
        string statusMessage)
    {
        _interop = interop;
        _featureWorks = featureWorks;
        _originalEnglishFeatureNames = originalEnglishFeatureNames;
        UnavailableReason = unavailableReason;
        StatusMessage = statusMessage;
    }

    public bool IsAvailable => _featureWorks is not null;

    public ConversionErrorClass UnavailableReason { get; }

    public string StatusMessage { get; }

    /// <summary>
    /// 批次开始时准备一次：加载 FeatureWorks 加载项，并把特征名切到英文。
    /// 不抛异常——不可用时返回一个 IsAvailable=false 的实例，由调用方决定降级还是失败。
    /// </summary>
    public static FeatureRecognizer Prepare(SolidWorksInteropBridge interop)
    {
        bool? originalEnglishFeatureNames = null;
        try
        {
            // "Point1@Origin" 是英文名。中文界面下不切换会选不中基准点。
            // 必须在打开任何文档之前设置：实测该开关不追溯已打开的文档。
            originalEnglishFeatureNames = interop.GetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames);
            interop.SetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames, true);
        }
        catch
        {
            originalEnglishFeatureNames = null;
        }

        object? featureWorks;
        try
        {
            featureWorks = interop.GetAddInObject(FeatureWorksProgId);
        }
        catch
        {
            featureWorks = null;
        }

        if (featureWorks is not null)
            return new FeatureRecognizer(
                interop, featureWorks, originalEnglishFeatureNames,
                ConversionErrorClass.None, "FeatureWorks 已就绪。");

        // FeatureWorks 随产品安装但默认不启用，此时 GetAddInObject 返回 null 且不抛异常。
        var path = interop.ResolveFeatureWorksPath();
        if (path is null || !File.Exists(path))
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "未找到 FeatureWorks 加载项，本批次不做特征识别。");
        }

        int loadResult;
        try
        {
            loadResult = interop.LoadAddIn(path);
        }
        catch (Exception ex)
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "加载 FeatureWorks 失败：" + ex.Message);
        }

        if (loadResult == SwLoadAddinLicenseError)
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "当前 SolidWorks 授权不含 FeatureWorks，本批次不做特征识别。");
        }

        if (loadResult is not (SwLoadAddinSuccess or SwLoadAddinAlreadyLoaded))
        {
            return new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                $"加载 FeatureWorks 返回 {loadResult}，本批次不做特征识别。");
        }

        try
        {
            featureWorks = interop.GetAddInObject(FeatureWorksProgId);
        }
        catch
        {
            featureWorks = null;
        }

        return featureWorks is not null
            ? new FeatureRecognizer(
                interop, featureWorks, originalEnglishFeatureNames,
                ConversionErrorClass.None, "FeatureWorks 已按需加载。")
            : new FeatureRecognizer(
                interop, null, originalEnglishFeatureNames,
                ConversionErrorClass.FeatureWorksUnavailable,
                "FeatureWorks 加载后仍无法获取自动化对象，本批次不做特征识别。");
    }

    /// <summary>对一个已导入的零件文档执行识别与草图完全定义。不抛异常，结果全部落在返回值里。</summary>
    public FeatureOutcome Process(
        object model,
        string documentTitle,
        BatchRequest request,
        Action<ConversionStage, string> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var statuses = new List<string>();

        if (!IsAvailable || !request.RecognizeFeatures)
        {
            return Degraded(0, false, stopwatch, statuses, "未启用特征识别或 FeatureWorks 不可用。");
        }

        var recognized = 0;
        var created = false;
        var step = "开始";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(ConversionStage.FeatureRecognition, "正在识别特征。");

            // FeatureWorks 只作用于活动文档。附着用户会话时必须确认导入零件真的
            // 成为 ActiveDoc，不能把识别命令误发到用户原有装配体。
            step = "ActivateDoc3";
            var activationFailure = ActivateExpectedDocument(documentTitle, cancellationToken);
            if (activationFailure is not null)
            {
                return Degraded(0, false, stopwatch, statuses, activationFailure);
            }

            // GetAddInObject 在没有活动零件时取得的对象可能仍处于加载态。文档激活后
            // 重新获取一次，使首件与后续零件走完全相同的 FeatureWorks 会话状态。
            step = "GetAddInObject";
            var refreshFailure = RefreshFeatureWorksForActiveDocument();
            if (refreshFailure is not null)
            {
                return Degraded(0, false, stopwatch, statuses, refreshFailure);
            }

            step = "SetFeatureWorksOptions";
            // SetAdvancedOptions 的返回值只作**诊断**用，绝不据此跳过识别。
            //
            // 教训：独立探针里它 true/false 与识别成败逐次吻合，于是一度被用来短路——
            // 但生产走的是后期绑定的 Invoke，且外面套着吞异常的 TryRun，
            // 「false」既可能是真的返回 false，也可能是调用抛了异常被吞掉。
            // 用这个分不清的信号否决整条链路，结果是用户已人工激活的会话里
            // 53 个零件全部被判「未激活」而跳过识别。识别该不该跑，只由识别自己回答。
            var optionsAccepted = false;
            string? optionsFailure = null;
            try
            {
                optionsAccepted = _interop.SetAdvancedOptions(
                    _featureWorks!,
                    FwAdvAddConstraintsToSketch | FwAdvAllowWizardHoleRecognition);
            }
            catch (Exception ex)
            {
                optionsFailure = $"0x{ex.HResult:X8} {ex.Message}";
            }

            TryRun(() => _ = _interop.SetPerformanceOptions(_featureWorks!, 0));

            // 识别前的几何基准。FeatureWorks 的 CreateFeatures 会用识别出的特征**重建实体**——
            // 只认出一部分特征时，重建结果可能只剩基体（一个方块），而 CreateFeatures 照样返回 true。
            // 这条管线此前从不校验几何，方块会被当成正常产物存盘。
            var baselineGeometry = _interop.MeasureSolidGeometry(model);

            step = "RecognizeFeatureAutomatic";
            var recognition = RunRecognitionAttempts(
                () => _interop.ClearSelection(model),
                () => _interop.RecognizeFeatureAutomatic(
                    _featureWorks!,
                    StandardPartRecognitionOptions),
                () => StaMessagePump.PumpAndWait(RecognitionRetryDelayMilliseconds, cancellationToken),
                cancellationToken);

            recognized = recognition.RecognizedFeatureCount;
            if (recognized <= 0)
            {
                // 识别确实没结果时，才回头看选项调用留下的线索：
                // 「干净地返回 false」是未激活会话的特征（冷会话实测重试 12 次恒 false）；
                // 「抛了异常」说明是别的问题，必须把 HRESULT 带出去，不能冒充未激活。
                var optionsNote = optionsFailure is null
                    ? $"SetAdvancedOptions 返回 {optionsAccepted}。"
                    : $"SetAdvancedOptions 抛出 {optionsFailure}。";
                var diagnostic = $"RecognizeFeatureAutomatic 连续 {recognition.Attempts} 次返回 0。{optionsNote}";
                return !optionsAccepted && optionsFailure is null
                    ? NotActivated(stopwatch, statuses, diagnostic)
                    : Degraded(0, false, stopwatch, statuses, diagnostic);
            }

            step = "VerifyRecognitionSideEffects";
            IReadOnlyList<FeatureTreeEntry> recognizedTree;
            try
            {
                recognizedTree = _interop.ReadTopLevelFeatureTree(model);
            }
            catch (Exception ex)
            {
                return Degraded(
                    recognized,
                    false,
                    stopwatch,
                    statuses,
                    "FeatureWorks 调用后无法确认特征树未被污染：" + ex.Message) with
                {
                    SemanticMismatch = true,
                };
            }
            var recognitionSideEffect = DescribeSemanticMismatch(recognized, false, recognizedTree);
            if (recognitionSideEffect is not null)
            {
                return Degraded(recognized, false, stopwatch, statuses, recognitionSideEffect) with
                {
                    SemanticMismatch = true,
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            step = "CreateFeatures";
            created = _interop.CreateFeatures(_featureWorks!, FwAddConstraintsToSketch);
            if (!created)
            {
                return Degraded(
                    recognized,
                    false,
                    stopwatch,
                    statuses,
                    "CreateFeatures 返回 false。");
            }

            step = "VerifyGeometry";
            var mismatch = DescribeGeometryMismatch(baselineGeometry, _interop.MeasureSolidGeometry(model));
            if (mismatch is not null)
            {
                return Degraded(recognized, true, stopwatch, statuses, mismatch) with { GeometryChanged = true };
            }

            step = "VerifyFeatureTree";
            IReadOnlyList<FeatureTreeEntry> featureTree;
            try
            {
                featureTree = _interop.ReadTopLevelFeatureTree(model);
            }
            catch (Exception ex)
            {
                return Degraded(
                    recognized,
                    true,
                    stopwatch,
                    statuses,
                    "FeatureWorks 报告识别成功，但无法读取结果特征树：" + ex.Message) with
                {
                    SemanticMismatch = true,
                };
            }
            var semanticMismatch = DescribeSemanticMismatch(recognized, created, featureTree);
            if (semanticMismatch is not null)
            {
                return Degraded(recognized, true, stopwatch, statuses, semanticMismatch) with
                {
                    SemanticMismatch = true,
                };
            }

            statuses.Add($"FeatureWorks 创建 {recognized} 个特征。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // COM 服务器已死：标记出来，让调用方重建会话而不是继续对着尸体重试。
            return Degraded(recognized, created, stopwatch, statuses, $"{step} 失败：{ex.Message}") with
            {
                SessionFaulted = IsServerFault(ex),
            };
        }

        var total = 0;
        var fullyDefined = 0;
        if (request.FullyDefineSketches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress(ConversionStage.SketchFullyDefine, "正在完全定义草图。");
            (total, fullyDefined) = FullyDefineSketches(model, statuses, cancellationToken);
        }

        return new FeatureOutcome(
            recognized,
            created,
            total,
            fullyDefined,
            statuses,
            false,
            stopwatch.ElapsedMilliseconds);
    }

    private string? ActivateExpectedDocument(string documentTitle, CancellationToken cancellationToken)
    {
        string? lastNote = null;
        for (var attempt = 1; attempt <= DocumentActivationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var errors = _interop.ActivateDocument(documentTitle);
                StaMessagePump.PumpAndWait(DocumentActivationDelayMilliseconds, cancellationToken);
                var activeTitle = _interop.GetActiveDocumentTitle();
                if (DocumentTitlesMatch(documentTitle, activeTitle))
                    return null;
                lastNote = $"attempt={attempt}, errors={errors}, active={activeTitle}";
            }
            catch (Exception ex)
            {
                lastNote = $"attempt={attempt}, exception={ex.Message}";
            }
        }

        return $"无法激活导入零件；标题={documentTitle}；{lastNote}";
    }

    internal static bool DocumentTitlesMatch(string expected, string active)
        => string.Equals(expected?.Trim(), active?.Trim(), StringComparison.OrdinalIgnoreCase);

    private string? RefreshFeatureWorksForActiveDocument()
    {
        object? refreshed;
        try
        {
            refreshed = _interop.GetAddInObject(FeatureWorksProgId);
        }
        catch (Exception ex)
        {
            return "活动文档就绪后重新获取 FeatureWorks 失败：" + ex.Message;
        }

        if (refreshed is null)
            return "活动文档就绪后 FeatureWorks 自动化对象为空。";
        if (ReferenceEquals(refreshed, _featureWorks))
        {
            // GetAddInObject returned another COM reference to the same RCW.
            ComRelease.One(refreshed);
        }
        else
        {
            ComRelease.One(_featureWorks);
            _featureWorks = refreshed;
        }

        return null;
    }

    /// <summary>
    /// FeatureWorks 首次识别会异步初始化，同一文档前几次可能静默返回 0。
    /// 每次调用前必须清空“本地识别实体”选择，并在失败后泵送 STA 消息再等待。
    /// </summary>
    internal static RecognitionAttemptResult RunRecognitionAttempts(
        Action clearSelection,
        Func<int> recognize,
        Action waitBetweenAttempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RecognitionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clearSelection();
            var recognized = recognize();
            if (recognized > 0)
                return new RecognitionAttemptResult(recognized, attempt);

            if (attempt < RecognitionAttempts)
                waitBetweenAttempts();
        }

        return new RecognitionAttemptResult(0, RecognitionAttempts);
    }

    internal readonly record struct RecognitionAttemptResult(
        int RecognizedFeatureCount,
        int Attempts);

    /// <summary>
    /// 识别已经真跑过并且颗粒无收，且 <c>SetAdvancedOptions</c> 干净地返回了 false——
    /// 这是未激活会话的特征。按哑实体降级（几何原样保留），
    /// 并置 <see cref="FeatureOutcome.SessionNotActivated"/> 供父 Worker 参考。
    ///
    /// 注意：这只是**识别失败之后**的归因，绝不能用来提前跳过识别。
    /// </summary>
    internal static FeatureOutcome NotActivated(
        Stopwatch stopwatch,
        List<string> statuses,
        string? diagnostic = null)
        => new(
            0, false, 0, 0, statuses, true, stopwatch.ElapsedMilliseconds,
            (diagnostic is null ? string.Empty : diagnostic + " ")
                + "疑为本 SolidWorks 会话未激活 FeatureWorks：请在该窗口手工做一次特征识别后重跑。",
            SessionNotActivated: true);

    private static FeatureOutcome Degraded(
        int recognized,
        bool created,
        Stopwatch stopwatch,
        List<string> statuses,
        string diagnostic)
        => new(recognized, created, 0, 0, statuses, true, stopwatch.ElapsedMilliseconds, diagnostic);

    private (int Total, int FullyDefined) FullyDefineSketches(
        object model,
        List<string> statuses,
        CancellationToken cancellationToken)
    {
        var sketchFeatures = CollectSketchFeatures(model);
        var extension = _interop.GetExtension(model);
        var sketchManager = _interop.GetSketchManager(model);
        var fullyDefined = 0;

        try
        {
            foreach (var feature in sketchFeatures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = SafeFeatureName(feature);
                var inSketch = false;
                try
                {
                    _interop.ClearSelection(model);
                    _interop.SelectFeature(feature);
                    _interop.InsertSketch(sketchManager, false);
                    inSketch = true;

                    // HorizontalDatumDisp / VerticalDatumDisp 传 null 时，
                    // SolidWorks 用"选择标记 = 6"的实体作基准，这里预选原点。
                    _interop.SelectByID2(
                        extension, "Point1@Origin", "EXTSKETCHPOINT",
                        MarkHorizontalDatum | MarkVerticalDatum);

                    _interop.FullyDefineSketch(sketchManager, AllSketchRelations);
                    _interop.InsertSketch(sketchManager, true);
                    inSketch = false;

                    var status = ReadConstrainedStatus(feature);
                    statuses.Add($"{name}={DescribeStatus(status)}");
                    if (status == SwFullyConstrained)
                        fullyDefined++;
                }
                catch (OperationCanceledException)
                {
                    if (inSketch)
                        TryRun(() => _interop.InsertSketch(sketchManager, true));
                    throw;
                }
                catch (Exception ex)
                {
                    if (inSketch)
                        TryRun(() => _interop.InsertSketch(sketchManager, true));
                    statuses.Add($"{name}=失败({ex.Message})");
                }
            }
        }
        finally
        {
            foreach (var feature in sketchFeatures)
                ComRelease.Final(feature);
            ComRelease.Final(sketchManager);
            ComRelease.Final(extension);
        }

        return (sketchFeatures.Count, fullyDefined);
    }

    /// <summary>
    /// 只收集 ProfileFeature。原点是 OriginProfileFeature，不会被命中，正是期望行为。
    /// </summary>
    private List<object> CollectSketchFeatures(object model)
    {
        var result = new List<object>();
        var feature = _interop.FirstFeature(model);
        while (feature is not null)
        {
            object? next = null;
            try
            {
                next = _interop.NextFeature(feature);
                if (string.Equals(_interop.FeatureTypeName(feature), SketchFeatureTypeName, StringComparison.Ordinal))
                {
                    result.Add(feature);
                    feature = next;
                    continue;
                }
            }
            catch
            {
                next = null;
            }

            ComRelease.Final(feature);
            feature = next;
        }

        return result;
    }

    private int ReadConstrainedStatus(object feature)
    {
        object? sketch = null;
        try
        {
            sketch = _interop.SpecificFeature(feature);
            return sketch is null ? 0 : _interop.GetConstrainedStatus(sketch);
        }
        catch
        {
            return 0;
        }
        finally
        {
            ComRelease.Final(sketch);
        }
    }

    private string SafeFeatureName(object feature)
    {
        try
        {
            return _interop.FeatureName(feature);
        }
        catch
        {
            return "(未知草图)";
        }
    }

    private static string DescribeStatus(int status) => status switch
    {
        1 => "未知",
        2 => "欠定义",
        3 => "完全定义",
        4 => "过定义",
        5 => "无解",
        6 => "无效解",
        7 => "自动求解关闭",
        _ => "读取失败",
    };

    /// <summary>
    /// 比对识别前后的实体几何。返回 null 表示一致；否则返回可直接进报告的原因。
    ///
    /// 量不到就判为不一致：识别把实体重建了，此时读不出几何本身就是异常信号，
    /// 绝不能因为"读不到"而默认放行。
    /// </summary>
    internal static string? DescribeGeometryMismatch(
        (double Volume, int FaceCount)? before,
        (double Volume, int FaceCount)? after)
    {
        if (before is not { } baseline)
            return null;   // 识别前就量不到，说明这条管线本来就没有可比基准，不由本判据兜底。
        if (after is not { } result)
            return "特征识别后无法读出实体几何，已降级为哑实体。";
        if (baseline.Volume <= 0)
            return null;

        var deviation = Math.Abs(result.Volume - baseline.Volume) / baseline.Volume;
        if (deviation > MaximumFeatureWorksVolumeRelativeDeviation)
        {
            return $"特征识别改变了零件几何：体积 {baseline.Volume:G6} → {result.Volume:G6}"
                + $"（相对偏差 {deviation:G3}），面数 {baseline.FaceCount} → {result.FaceCount}；已降级为哑实体。";
        }

        return null;
    }

    internal static string? DescribeSemanticMismatch(
        int recognized,
        bool created,
        IReadOnlyList<FeatureTreeEntry> features)
    {
        var sheetMetal = features.FirstOrDefault(IsSheetMetalFeature);
        if (!string.IsNullOrWhiteSpace(sheetMetal.TypeName))
        {
            return $"普通零件被误识别为钣金特征：{sheetMetal.Name} [{sheetMetal.TypeName}]；"
                + "已丢弃错误特征树并降级为哑实体。";
        }

        if (recognized <= 0 || !created)
            return null;
        if (features.Count == 0)
            return "FeatureWorks 报告识别成功，但无法读取结果特征树；已降级为哑实体。";

        var imported = features.FirstOrDefault(feature => IsImportedBodyFeature(feature.TypeName));
        if (!string.IsNullOrWhiteSpace(imported.TypeName))
        {
            return $"FeatureWorks 报告识别成功，但结果仍包含未识别导入体：{imported.Name} [{imported.TypeName}]；"
                + "该特征树不等价于手工识别，已降级为哑实体。";
        }

        return null;
    }

    private static bool IsSheetMetalFeature(FeatureTreeEntry feature)
        => IsSheetMetalFeature(feature.TypeName)
           || feature.TypeName.Equals("CutListFolder", StringComparison.OrdinalIgnoreCase)
           && feature.Name.StartsWith("Sheet<", StringComparison.OrdinalIgnoreCase);

    private static bool IsSheetMetalFeature(string typeName)
        => typeName.Contains("SheetMetal", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("BaseFlange", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("Bend", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("EdgeFlange", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("MiterFlange", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("Hem", StringComparison.OrdinalIgnoreCase);

    private static bool IsImportedBodyFeature(string typeName)
        => typeName.Equals("BaseBody", StringComparison.OrdinalIgnoreCase)
           || typeName.Contains("ImportedBody", StringComparison.OrdinalIgnoreCase)
           || typeName.Equals("Imported", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FeatureWorks 的 COM 服务器故障后重建会话。
    ///
    /// 死对象是不会自愈的：不重建就等于对着尸体重试到批次结束。
    /// 重建不成功就返回 false，由调用方停用本批次的识别并如实报告——
    /// 让用户拿到"从第 N 件起没有特征"的明确结论，而不是每件都慢 10 次重试。
    /// </summary>
    public bool TryRecoverSession()
    {
        _featureWorks = null;
        try
        {
            var path = _interop.ResolveFeatureWorksPath();
            if (path is not null && File.Exists(path))
                _ = _interop.LoadAddIn(path);
        }
        catch
        {
            return false;
        }

        try
        {
            _featureWorks = _interop.GetAddInObject(FeatureWorksProgId);
        }
        catch
        {
            _featureWorks = null;
        }

        return _featureWorks is not null;
    }

    /// <summary>
    /// V3.5.2：子 Worker 可能附着到用户已打开的 SolidWorks 单实例，仅隔离 STA 不会重启
    /// FeatureWorks 服务。每件开始前卸载并重载加载项，清除上一个零件留下的故障状态。
    /// </summary>
    public static bool TryBeginIsolatedSession(
        SolidWorksInteropBridge interop,
        out bool wasLoaded,
        out string diagnostic)
    {
        wasLoaded = false;
        diagnostic = string.Empty;
        object? existing = null;
        try
        {
            existing = interop.GetAddInObject(FeatureWorksProgId);
            wasLoaded = existing is not null;
        }
        catch
        {
            // 故障对象本身可能无法取得；仍尝试按已加载状态重置。
            wasLoaded = true;
        }
        finally
        {
            ComRelease.Final(existing);
        }

        try
        {
            var path = interop.ResolveFeatureWorksPath();
            if (path is null || !File.Exists(path))
            {
                diagnostic = "未找到 FeatureWorks 加载项，不能建立单零件隔离会话。";
                return false;
            }

            var unloadResult = interop.UnloadAddIn(path);
            StaMessagePump.PumpAndWait(500, CancellationToken.None);
            var loadResult = interop.LoadAddIn(path);
            StaMessagePump.PumpAndWait(500, CancellationToken.None);
            if (loadResult is not (SwLoadAddinSuccess or SwLoadAddinAlreadyLoaded))
            {
                diagnostic = $"FeatureWorks 重载失败：UnloadAddIn={unloadResult}, LoadAddIn={loadResult}。";
                return false;
            }

            object? refreshed = null;
            try
            {
                refreshed = interop.GetAddInObject(FeatureWorksProgId);
                if (refreshed is null)
                {
                    diagnostic = $"FeatureWorks 重载后自动化对象为空：UnloadAddIn={unloadResult}, LoadAddIn={loadResult}。";
                    return false;
                }
            }
            finally
            {
                ComRelease.Final(refreshed);
            }

            diagnostic = $"FeatureWorks 单零件会话已重置（UnloadAddIn={unloadResult}, LoadAddIn={loadResult}）。";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = "FeatureWorks 单零件会话重置失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 已改为不做任何事。原实现会在加载项"本来没加载"时把它卸掉以还原现场，
    /// 但 FeatureWorks 的自动识别依赖一份**进程内激活状态**（须由人工识别一次建立，
    /// 见 <see cref="SolidWorksImporter.ShouldResetFeatureWorksSession"/> 的实测说明），
    /// 卸载会连带清掉它，让用户手工激活过的会话在下一批次里失效。
    /// 加载项留在会话里没有副作用，代价远小于清掉激活态。
    /// </summary>
    public static void RestoreIsolatedSessionState(SolidWorksInteropBridge interop, bool wasLoaded)
    {
    }

    public void Dispose()
    {
        if (_originalEnglishFeatureNames is bool original)
            TryRun(() => _interop.SetUserPreferenceToggle(SwUseEnglishLanguageFeatureNames, original));
        ComRelease.Final(_featureWorks);
        _featureWorks = null;
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

}
