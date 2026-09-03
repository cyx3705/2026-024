using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed partial class AssemblyViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> _probeWorker;
    private readonly Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runWorker;
    private readonly Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runPartWorker;
    private readonly Func<AssemblyRenameRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _renameWorker;
    private readonly Action<ConversionSourceFormat> _validateEnvironment;
    private readonly Dispatcher _uiDispatcher;
    private readonly MappingRuntimePaths _runtimePaths;
    private readonly object _lifecycleGate = new();
    private readonly object _dispatchGate = new();
    private readonly Queue<Action> _pendingUiUpdates = new();
    private string _sourceAssemblyPath = string.Empty;
    private string _partDirectory = string.Empty;
    private MappingContentOption _selectedMappingContent = MappingContentOption.Available[0];
    private bool _suppressMappingContentChange;
    private ConversionSourceKind _sourceKind;
    private string _xtDirectory = string.Empty;
    private string _solidWorksDirectory = string.Empty;
    private string _assemblyOutputPath = string.Empty;
    private string _statusText = "请选择转换来源";
    private string _warningSummary = string.Empty;
    private bool _isBusy;
    private bool _isProbing;
    private bool _recognizeFeatures;
    private bool _fullyDefineSketches;
    private bool _continueWhenPartFails = true;
    // 装配关系是 .asm 的语义组成部分，而不是高级附加选项。解析到可翻译关系时，
    // ApplyProbeResult 会保持此默认开启；没有关系的装配则会自动关闭且禁用开关。
    private bool _rebuildMates;
    private bool _conversionCompleted;
    private bool _cancelRequested;
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeOperation;
    private DispatcherOperation? _dispatchOperation;
    private AssemblyConversionPlan? _plan;
    private AssemblyRenamePlan? _renamePlan;
    private AssemblyProbeResult? _probeResult;
    private MateOutcome? _mateOutcome;
    private string? _sourceHashAfterProbe;
    private IProgress<string>? _operationProgress;
    private bool _lastOperationSucceeded;
    private bool _lastOperationCanceled;
    private bool _disposed;
    private string _drawingPrefix = string.Empty;
    /// <summary>本轮 Worker 报回的第一条失败原因，用于把根因带进命令的最终失败消息。</summary>
    private string _firstWorkerFailure = string.Empty;
    public ObservableCollection<AssemblyTreeNode> AssemblyTree { get; } = [];

    public IReadOnlyList<MappingContentOption> MappingContents => MappingContentOption.Available;

    public MappingContentOption SelectedMappingContent
    {
        get => _selectedMappingContent;
        set
        {
            if (value is null || _suppressMappingContentChange)
                return;
            if (IsBusy || EqualityComparer<MappingContentOption>.Default.Equals(_selectedMappingContent, value))
                return;

            _suppressMappingContentChange = true;
            try
            {
                ApplyMappingContentChange(value);
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(PartsPanelTitle));
                OnPropertyChanged(nameof(OperationText));
                OnPropertyChanged(nameof(IsAssemblyMode));
                OnPropertyChanged(nameof(IsRenameMode));
                OnPropertyChanged(nameof(ShowConversionOptions));
                OnPropertyChanged(nameof(SourcePartColumnHeader));
                OnPropertyChanged(nameof(IsPartDirectoryMode));
                OnPropertyChanged(nameof(CanProbe));
                OnPropertyChanged(nameof(CanConvert));
                OnPropertyChanged(nameof(CanWrite));
                NotifySourceChanged();
            }
            finally
            {
                _suppressMappingContentChange = false;
            }
        }
    }

    public string SourceAssemblyPath
    {
        get => _sourceAssemblyPath;
        set => SetAssemblySource(value);
    }

    public string XtDirectory
    {
        get => _xtDirectory;
        private set => SetField(ref _xtDirectory, value);
    }

    public string SolidWorksDirectory
    {
        get => _solidWorksDirectory;
        private set => SetField(ref _solidWorksDirectory, value);
    }

    public bool CanCancel => IsBusy && !_cancelRequested;

    public string AssemblyOutputPath
    {
        get => _assemblyOutputPath;
        private set => SetField(ref _assemblyOutputPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string WarningSummary
    {
        get => _warningSummary;
        private set => SetField(ref _warningSummary, value);
    }

    internal bool LastOperationSucceeded => _lastOperationSucceeded;
    internal bool LastOperationCanceled => _lastOperationCanceled;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanProbe));
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(CanRebuildMates));
            OnPropertyChanged(nameof(CanContinueWhenPartFails));
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetField(ref _isProbing, value);
    }

    /// <summary>
    /// 界面上的"识别特征与草图"——一个开关同时驱动识别与草图完全定义。
    ///
    /// 两者本就是主从关系（草图完全定义只对识别出的草图生效），拆成两个复选框只是
    /// 白占横向空间，把后面的"重建装配关系"挤到第二行、让用户拿不到那个开关。
    /// 协议层仍是两个独立字段，只有界面合并。
    /// </summary>
    public bool RecognizeFeatures
    {
        get => _recognizeFeatures;
        set
        {
            if (!SetField(ref _recognizeFeatures, value))
                return;
            FullyDefineSketches = value;
            ApplyRegenerationToRows();
        }
    }

    /// <summary>
    /// 已有产物这一轮会不会被重做。装配转换把全部唯一零件都交给 Worker。SolidWorks
    /// 特征整备对已有 SLDPRT 一律重做（先 XT 再识别，不看原零件有没有特征）；Solid Edge
    /// 只在开启识别时重做。零件文件夹模式按 <c>HasExistingOutput</c> 过滤作业，
    /// 已有产物是真的会被跳过。
    /// </summary>
    private bool RegeneratesExistingOutputs
        => IsAssemblyMode
           && (RecognizeFeatures || SourceFormat == ConversionSourceFormat.SolidWorks);

    private void ApplyRegenerationToRows()
    {
        var regenerates = RegeneratesExistingOutputs;
        foreach (var row in Parts)
            row.RegeneratesExistingOutput = regenerates;
    }

    public bool FullyDefineSketches
    {
        get => _fullyDefineSketches;
        set
        {
            if (!RecognizeFeatures && value)
                return;
            SetField(ref _fullyDefineSketches, value);
        }
    }

    public bool ContinueWhenPartFails
    {
        get => _continueWhenPartFails;
        set
        {
            if (!CanContinueWhenPartFails && value)
                return;
            SetField(ref _continueWhenPartFails, value);
        }
    }

    /// <summary>
    /// V3.5：把 SE 装配关系翻译成 SW 配合。发现关系时默认开启。
    ///
    /// 与"识别特征"**不互斥**——实测证明识别后几何匹配率仍是 100%（25 号文档 §5.1）。
    /// </summary>
    public bool RebuildMates
    {
        get => _rebuildMates;
        set
        {
            if (!IsAssemblyMode && value)
                return;
            SetField(ref _rebuildMates, value);
        }
    }

    /// <summary>只有解析出关系才允许勾选——没有关系可翻译时，这个开关是个空承诺。</summary>
    public bool CanRebuildMates => CanEdit && IsAssemblyMode && (_plan?.RelationCount ?? 0) > 0;

    public string RebuildMatesHint => _plan is null
        ? "先解析装配体，才能知道有多少装配关系可以翻译。"
        : _plan.RelationCount > 0
            ? $"把 {_plan.RelationCount} 条 Solid Edge 装配关系翻译成 SolidWorks 配合。"
                + "逐条建立并校验位置，超差的自动回滚并如实报告；建不起来的组件保持固定，位置精度不会退化。"
            : "本装配体没有显式装配关系（靠拖放定位），没有可翻译的内容，全部组件保持固定。";

    public bool IsExternalMode => true;
    public bool IsOhsModeAvailable => false;
    public ConversionSourceKind SourceKind => _sourceKind;
    public string SourcePath => SourceKind switch
    {
        ConversionSourceKind.Assembly => SourceAssemblyPath,
        ConversionSourceKind.PartDirectory => _partDirectory,
        _ => string.Empty,
    };
    public string SourceLabel => "转换来源";
    /// <summary>本次转换的源 CAD 格式。界面只从当前选中的转换内容取，不另设开关。</summary>
    public ConversionSourceFormat SourceFormat => SelectedMappingContent.SourceFormat;
    public bool IsAssemblyMode => SourceKind == ConversionSourceKind.Assembly
        || SourceKind == ConversionSourceKind.None && SelectedMappingContent.IsAssemblySource;
    public bool IsPartDirectoryMode => SourceKind == ConversionSourceKind.PartDirectory
        || SourceKind == ConversionSourceKind.None && !SelectedMappingContent.IsAssemblySource;
    public bool CanEdit => !IsBusy;
    public bool CanProbe => CanEdit && IsAssemblyMode
        && !string.IsNullOrWhiteSpace(SourceAssemblyPath)
        && ConversionPathLayout.HasExtension(
            SourceAssemblyPath, ConversionPathLayout.GetSourceAssemblyExtension(SourceFormat));
    public bool CanConvert => CanEdit && (IsRenameMode
        ? CanWrite
        : IsAssemblyMode
            ? !_conversionCompleted && _plan?.CanConvert == true
            : IsPartDirectoryMode && Parts.Any(row => !row.HasExistingOutput));
    public bool CanContinueWhenPartFails => CanEdit && IsAssemblyMode;

    /// <summary>零件列的列头。写死"Solid Edge 零件"在 SW 自整备模式下是假话。</summary>
    public string SourcePartColumnHeader => IsRenameMode
        ? "当前文件"
        : SourceFormat == ConversionSourceFormat.SolidWorks
            ? "SolidWorks 零件"
            : "Solid Edge 零件";
    public string PrimaryActionText => IsRenameMode
        ? "写入"
        : IsPartDirectoryMode
            || SourceKind == ConversionSourceKind.None && !SelectedMappingContent.IsAssemblySource
            ? "转换全部零件"
            : "转换装配体";
    public string PartsPanelTitle => IsPartDirectoryMode ? "零件" : "唯一零件";
    public string OperationText => IsProbing
        ? "正在解析装配体"
        : IsRenameMode
            ? "正在写入"
            : IsPartDirectoryMode ? "正在转换全部零件" : "正在转换装配体";

    public event PropertyChangedEventHandler? PropertyChanged;

    public AssemblyViewModel()
        : this(MappingRuntimePaths.CreateAppShellFallback())
    {
    }

    internal AssemblyViewModel(MappingRuntimePaths runtimePaths)
        : this(new WorkerClient(runtimePaths), runtimePaths, Dispatcher.CurrentDispatcher)
    {
    }

    private AssemblyViewModel(
        WorkerClient workerClient,
        MappingRuntimePaths runtimePaths,
        Dispatcher uiDispatcher)
        : this(
            workerClient.ProbeAssemblyAsync,
            workerClient.RunAssemblyAsync,
            sourceFormat => PreflightValidator.ValidateEnvironment(workerClient.WorkerPath, sourceFormat),
            uiDispatcher,
            workerClient.RunAsync,
            runtimePaths,
            workerClient.RunRenameAsync)
    {
    }

    internal AssemblyViewModel(
        Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> probeWorker,
        Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> runWorker,
        Action<ConversionSourceFormat> validateEnvironment,
        Dispatcher uiDispatcher,
        Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>>? runPartWorker = null,
        MappingRuntimePaths? runtimePaths = null,
        Func<AssemblyRenameRequest, Action<WorkerEvent>, CancellationToken, Task<int>>? renameWorker = null)
    {
        _probeWorker = probeWorker ?? throw new ArgumentNullException(nameof(probeWorker));
        _runWorker = runWorker ?? throw new ArgumentNullException(nameof(runWorker));
        _runPartWorker = runPartWorker
            ?? ((request, progress, cancellationToken) =>
                new WorkerClient(runtimePaths).RunAsync(request, progress, cancellationToken));
        _renameWorker = renameWorker
            ?? ((request, progress, cancellationToken) =>
                new WorkerClient(runtimePaths).RunRenameAsync(request, progress, cancellationToken));
        _validateEnvironment = validateEnvironment ?? throw new ArgumentNullException(nameof(validateEnvironment));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _runtimePaths = runtimePaths ?? MappingRuntimePaths.CreateAppShellFallback();
    }

    public void SetSourceFile(string path)
        => SetSourcePath(path);

    /// <summary>
    /// 来源格式只跟当前转换内容走：装配体转换收装配体文件，零件转换收文件夹。
    /// 不得按扩展名改转换内容，也不得在选完来源时解析装配体。
    /// </summary>
    public void SetSourcePath(string path)
    {
        if (IsBusy)
            return;

        var trimmed = path.Trim();
        if (!SelectedMappingContent.AcceptsSourcePath(trimmed))
            throw new InvalidOperationException(
                $"当前是「{SelectedMappingContent.DisplayName}」。{SelectedMappingContent.SourceRequirement}。");
        if (SelectedMappingContent.IsAssemblySource)
            SetAssemblySource(trimmed);
        else
            SetPartDirectory(trimmed);
    }

    public void SetAssemblySource(string path)
    {
        if (IsBusy)
            return;

        if (!SelectedMappingContent.IsAssemblySource)
            throw new InvalidOperationException(
                "当前转换内容是零件文件夹，请选择文件夹。装配体请先改转换内容。");
        var trimmed = path.Trim();
        var expected = ConversionPathLayout.GetSourceAssemblyExtension(SelectedMappingContent.SourceFormat);
        if (!ConversionPathLayout.HasExtension(trimmed, expected))
            throw new InvalidOperationException($"当前转换内容需要 {expected} 装配体文件。");
        ClearSourceResults();
        ResetOutputDirectories();
        _sourceAssemblyPath = Path.GetFullPath(trimmed);
        _partDirectory = string.Empty;
        SetSourceKind(ConversionSourceKind.Assembly);
        RebuildMates = false;
        UpdateAssemblyOutputPaths();
        StatusText = "已选择装配体，点击解析装配体";
        NotifySourceChanged();
    }

    public void SetPartDirectory(string path)
    {
        if (IsBusy)
            return;

        if (SelectedMappingContent.IsAssemblySource)
            throw new InvalidOperationException(
                "当前转换内容是装配体，请选择装配体文件，不要选择文件夹。");
        var fullPath = Path.GetFullPath(path.Trim());
        ClearSourceResults();
        ResetOutputDirectories();
        _sourceAssemblyPath = string.Empty;
        _partDirectory = fullPath;
        SetSourceKind(ConversionSourceKind.PartDirectory);
        ContinueWhenPartFails = false;
        RebuildMates = false;
        ScanPartDirectory(updateStatus: true);
        NotifySourceChanged();
    }

    public async Task ProbeAsync(IProgress<string>? progress = null)
    {
        _operationProgress = progress;
        try
        {
            await StartOperationAsync(isProbe: true, ProbeCoreAsync);
        }
        finally
        {
            if (ReferenceEquals(_operationProgress, progress))
                _operationProgress = null;
        }
    }

    public async Task ConvertAsync(IProgress<string>? progress = null)
    {
        _operationProgress = progress;
        try
        {
            await StartOperationAsync(
                isProbe: false,
                IsRenameMode
                    ? RenameCoreAsync
                    : IsPartDirectoryMode ? ConvertPartsCoreAsync : ConvertAssemblyCoreAsync);
        }
        finally
        {
            if (ReferenceEquals(_operationProgress, progress))
                _operationProgress = null;
        }
    }

    public bool Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_lifecycleGate)
        {
            if (!IsBusy || _cancelRequested || _operationCancellation is null)
                return false;
            _cancelRequested = true;
            cancellation = _operationCancellation;
        }
        StatusText = "正在取消";
        OnPropertyChanged(nameof(CanCancel));
        cancellation.Cancel();
        return true;
    }

    public void Dispose()
    {
        Task? activeOperation;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            activeOperation = _activeOperation;
            _operationCancellation?.Cancel();
        }

        lock (_dispatchGate)
        {
            _pendingUiUpdates.Clear();
            if (_dispatchOperation?.Status == DispatcherOperationStatus.Pending)
                _dispatchOperation.Abort();
            _dispatchOperation = null;
        }

        try
        {
            activeOperation?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Disposal owns cancellation; do not surface it as a shutdown failure.
        }
        catch (Exception)
        {
            // The command path already observed and reported the operation failure.
        }
        AssemblyTree.Clear();
        Parts.Clear();
    }

    private Task StartOperationAsync(bool isProbe, Func<CancellationToken, Task> operation)
    {
        _firstWorkerFailure = string.Empty;
        _lastResultText = string.Empty;
        CancellationTokenSource cancellation;
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperation is not null)
                return _activeOperation;
            cancellation = new CancellationTokenSource();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operationCancellation = cancellation;
            _activeOperation = completion.Task;
            _cancelRequested = false;
        }

        IsProbing = isProbe;
        IsBusy = true;
        _lastOperationSucceeded = false;
        _lastOperationCanceled = false;
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(OperationText));
        _ = RunOperationAndCompleteAsync(operation, cancellation, completion);
        return completion.Task;
    }

    private async Task RunOperationAndCompleteAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        var canceled = false;
        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            canceled = true;
            _lastOperationCanceled = true;
            QueueUiUpdate(() => StatusText = "操作已取消");
        }
        catch (Exception ex)
        {
            failure = ex;
            QueueUiUpdate(() => StatusText = ex.Message);
        }
        finally
        {
            QueueUiUpdate(() =>
            {
                if (!IsProbing && IsPartDirectoryMode)
                    ScanPartDirectory(updateStatus: false);
                IsBusy = false;
                IsProbing = false;
                OnPropertyChanged(nameof(OperationText));
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_activeOperation, completion.Task))
                        _activeOperation = null;
                }
            });
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                    _operationCancellation = null;
            }
            cancellation.Dispose();
            if (canceled)
                completion.TrySetCanceled();
            else if (failure is not null)
                completion.TrySetException(failure);
            else
                completion.TrySetResult();
        }
    }

    private async Task ProbeCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFormat = SourceFormat;
        var sourceAssemblyExtension = ConversionPathLayout.GetSourceAssemblyExtension(sourceFormat);
        if (!File.Exists(SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(SourceAssemblyPath, sourceAssemblyExtension))
            throw new InvalidOperationException($"请选择存在的 {sourceAssemblyExtension} 装配体文件。");
        // COM ProgID 查询和后续 Worker 启动都不要占着选文件那一拍的 UI 线程。
        await Task.Run(() => _validateEnvironment(sourceFormat), cancellationToken).ConfigureAwait(false);
        // 三个属性下拉的候选也在这一拍认一次：之后整轮整备都用这一份，不再每点开一格
        // 就重读一次注册表和整个模板目录（那是在 UI 线程上）。
        await Task.Run(SolidWorksPropertyOptions.Refresh, cancellationToken).ConfigureAwait(false);

        var batchId = Guid.NewGuid().ToString("N");
        var resultDirectory = _runtimePaths.ProbesDirectory;
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, batchId + ".result.json");
        try
        {
            var request = new AssemblyProbeRequest(
                batchId, Path.GetFullPath(SourceAssemblyPath), resultPath, sourceFormat);
            QueueUiUpdate(() => StatusText = "正在只读解析装配树");
            var result = await _probeWorker(
                request,
                ReportWorkerEvent,
                cancellationToken).ConfigureAwait(false);
            var plan = AssemblyPlanner.Create(
                result, customXtDirectory: null, customSolidWorksDirectory: null, sourceFormat);
            var sourceHash = ComputeSha256(result.SourceAssemblyPath);
            var issueText = plan.BlockingIssues.Count == 0
                ? string.Empty
                : string.Join("；", plan.BlockingIssues.Select(issue => $"[{issue.ErrorClass}] {issue.Message}"));
            _lastOperationSucceeded = plan.CanConvert;
            StatusText = FormatProbeStatus(plan, result, issueText);
            QueueUiUpdate(() => ApplyProbeResult(result, plan, sourceHash));
        }
        finally
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
        }
    }

    private async Task ConvertAssemblyCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _plan ?? throw new InvalidOperationException("请先成功解析装配体。");
        if (!plan.CanConvert)
            throw new InvalidOperationException("装配清单仍有前置错误，不能转换。");
        var sourceFormat = SourceFormat;
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再转换。");

        _validateEnvironment(sourceFormat);
        ExternalOutputLayout.EnsureDirectories(
            ConversionPathLayout.UsesParasolidHandoff(sourceFormat) ? plan.XtDirectory : null,
            plan.SolidWorksDirectory);
        var jobs = Parts.Select(row => new ConversionJob(
            row.Id,
            row.SourcePath,
            row.XtPath,
            row.SolidWorksPath)).ToArray();
        var request = new AssemblyBatchRequest(
            Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            plan.SourceAssemblyPath,
            plan.AssemblyOutputPath,
            jobs,
            plan.Occurrences,
            Overwrite: false,
            RecognizeFeatures: RecognizeFeatures,
            FullyDefineSketches: RecognizeFeatures && FullyDefineSketches,
            ContinueWhenPartFails: ContinueWhenPartFails,
            RebuildMates: RebuildMates && plan.RelationCount > 0,
            Nodes: plan.Nodes,
            Relations: plan.Relations,
            SourceFormat: sourceFormat);
        PreflightValidator.ValidateAssemblyRequest(request);

        foreach (var row in Parts)
        {
            row.Status = "排队";
            row.Detail = string.Empty;
            row.ResetFeatureResult();
            if (!RecognizeFeatures)
            {
                row.FeatureText = "—";
                row.SketchText = "—";
            }
        }
        QueueUiUpdate(() => StatusText = $"正在转换 {jobs.Length} 个唯一零件并组装");
        MateOutcome? reportedMate = null;
        var exitCode = await _runWorker(
            request,
            workerEvent =>
            {
                // 不能只依赖 UI 调度后的字段：Worker 已经退出而 Dispatcher 尚未消费事件时，
                // _mateOutcome 仍可能是旧值。这里保存本批次的原始回执，再异步更新界面。
                reportedMate ??= workerEvent.Mate ?? workerEvent.Assembly?.Mate;
                ReportWorkerEvent(workerEvent);
            },
            cancellationToken).ConfigureAwait(false);
        if (exitCode == 0 && request.RebuildMates && plan.RelationCount > 0 && reportedMate is null)
        {
            throw new InvalidDataException(
                "Worker 未返回装配关系重建结果；为避免把未重建配合的 SLDASM 误报为成功，"
                + "本次转换已判为失败。请保留转换日志并重试。");
        }
        QueueUiUpdate(() =>
        {
            _conversionCompleted = File.Exists(plan.AssemblyOutputPath);
            StatusText = exitCode == 0
                ? $"装配转换完成：{plan.AssemblyOutputPath}{FormatMateSummary()}"
                : _conversionCompleted
                    ? $"装配已生成，但有零件失败：{plan.AssemblyOutputPath}{FormatMateSummary()}"
                    : "装配转换失败，未生成 SLDASM";
            AppendMateDiagnostics();
            OnPropertyChanged(nameof(CanConvert));
        });
        _lastOperationSucceeded = exitCode == 0 && File.Exists(plan.AssemblyOutputPath);
    }

    private async Task ConvertPartsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPartDirectoryMode || !Directory.Exists(_partDirectory))
            throw new DirectoryNotFoundException("请选择存在的零件文件夹。");

        var pending = Parts.Where(row => !row.HasExistingOutput).ToArray();
        if (pending.Length == 0)
            throw new InvalidOperationException("没有尚待转换的零件。");

        // 零件文件夹模式目前只有 Solid Edge 源（SW 自整备的来源是单个 .SLDASM），
        // 但格式仍从当前转换内容取，不再另写一份假设。
        var sourceFormat = SourceFormat;
        _validateEnvironment(sourceFormat);
        ExternalOutputLayout.EnsureDirectories(XtDirectory, SolidWorksDirectory);
        var jobs = pending.Select(row => new ConversionJob(
            row.Id,
            row.SourcePath,
            row.XtPath,
            row.SolidWorksPath)).ToArray();
        PreflightValidator.ValidateJobs(jobs, overwrite: false, sourceFormat);

        foreach (var row in pending)
        {
            row.Status = "排队";
            row.Detail = string.Empty;
            row.ResetFeatureResult();
            if (!RecognizeFeatures)
            {
                row.FeatureText = "—";
                row.SketchText = "—";
            }
        }

        var request = new BatchRequest(
            Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            jobs,
            Overwrite: false,
            RecognizeFeatures: RecognizeFeatures,
            FullyDefineSketches: RecognizeFeatures && FullyDefineSketches,
            SourceFormat: sourceFormat);
        QueueUiUpdate(() => StatusText = $"正在转换 {jobs.Length} 个零件");
        var exitCode = await _runPartWorker(
            request,
            ReportWorkerEvent,
            cancellationToken).ConfigureAwait(false);
        _lastOperationSucceeded = exitCode == 0;
        QueueUiUpdate(() => StatusText = exitCode == 0 ? "零件转换完成" : "零件转换结束，存在失败项");
    }

    private void ApplyWorkerEvent(WorkerEvent workerEvent)
    {
        // 配合结果既可能直接挂在事件上，也可能随装配结果一起回来。
        _mateOutcome = workerEvent.Mate ?? workerEvent.Assembly?.Mate ?? _mateOutcome;
        if (workerEvent.JobId is null)
        {
            // Completed 会盖掉 ApplyProbeResult 写好的前置错误，控制台看起来像解析成功。
            if (workerEvent.Stage != ConversionStage.Completed)
                StatusText = ConversionProgressPresenter.FormatMessage(workerEvent);
            return;
        }
        var row = FindRow(workerEvent.JobId);
        if (row is null)
            return;
        row.Status = ConversionProgressPresenter.GetRowStatus(workerEvent, row.Status);
        row.Detail = ConversionProgressPresenter.FormatMessage(workerEvent);
        ConversionProgressPresenter.ApplyFeatureOutcome(row, workerEvent.Feature);
    }

    private void ReportWorkerEvent(WorkerEvent workerEvent)
    {
        // 留住第一条失败原因。命令最终只抛一句"失败，详见 Vulcan 控制台"，而真正的原因
        // 混在几十条进度事件里——用户看到的就是一个没有原因的红叉。把第一条原因带进
        // 最终消息，是唯一能让"为什么失败"和"失败了"出现在同一行的办法。
        if (workerEvent.IsError && string.IsNullOrEmpty(_firstWorkerFailure))
        {
            var message = workerEvent.Message?.Trim();
            if (!string.IsNullOrEmpty(message))
                _firstWorkerFailure = message;
        }

        // 逐个文件的成功事件只更新表格那一行，不再往控制台灌一条。
        // 一次十几个零件的写入会发出三十多条「已改名为 X」「已写入 8 项属性：X」，
        // 而它们说的事表里每一行都写着；控制台该留给这一轮的开头、结尾和失败
        // （DEC-060）。批次级事件 JobId 为空，失败无论如何都要出来。
        if (workerEvent.JobId is null || workerEvent.IsError)
            _operationProgress?.Report(ConversionProgressPresenter.FormatMessage(workerEvent));
        QueueUiUpdate(() => ApplyWorkerEvent(workerEvent));
    }

    private void UpdateAssemblyOutputPaths()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourceAssemblyPath))
                throw new InvalidDataException();
            var fullPath = Path.GetFullPath(SourceAssemblyPath.Trim());
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException();
            var directories = ExternalOutputLayout.Resolve(
                directory,
                xtDirectory: null,
                solidWorksDirectory: null);
            XtDirectory = directories.XtDirectory;
            SolidWorksDirectory = directories.SolidWorksDirectory;
            AssemblyOutputPath = ConversionPathLayout.ResolveAssemblyOutputPath(fullPath, directories.SolidWorksDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            XtDirectory = string.Empty;
            SolidWorksDirectory = string.Empty;
            AssemblyOutputPath = string.Empty;
        }
    }

    private void ClearProbeResult()
    {
        _plan = null;
        _renamePlan = null;
        _probeResult = null;
        _sourceHashAfterProbe = null;
        _conversionCompleted = false;
        AssemblyTree.Clear();
        Parts.Clear();
        WarningSummary = "输出按源装配的层级生成嵌套装配体，全部组件固定，不含配合。";
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanWrite));
    }

    private void ClearSourceResults()
    {
        ClearProbeResult();
        _mateOutcome = null;
        // 换了来源装配，上一台设备的材料和表面处理就不再是这批零件的事实。
        // 记账按源文件全路径，不清掉的话换回旧装配还会把旧值诈尸带出来。
        _propertyEdits.Clear();
        _nameEdits.Clear();
        XtDirectory = string.Empty;
        SolidWorksDirectory = string.Empty;
        AssemblyOutputPath = string.Empty;
    }

    private void ScanPartDirectory(bool updateStatus)
    {
        var directories = ExternalOutputLayout.Resolve(
            _partDirectory,
            xtDirectory: null,
            solidWorksDirectory: null);
        XtDirectory = directories.XtDirectory;
        SolidWorksDirectory = directories.SolidWorksDirectory;
        AssemblyOutputPath = string.Empty;
        _rows.ReplaceAll(FileScanner.Scan(
                ConversionMode.External,
                _partDirectory,
                outputDirectories: directories,
                allowLegacyXt: true,
                allowLegacySolidWorks: true)
            .Select(candidate => new ConversionFileRow(candidate)));

        WarningSummary = string.Empty;
        if (updateStatus)
        {
            var pending = Parts.Count(row => !row.HasExistingOutput);
            StatusText = Parts.Count == 0
                ? "未找到顶层 .par 文件"
                : pending == 0
                    ? $"扫描完成，共 {Parts.Count} 个零件，均已有产物"
                    : $"扫描完成，共 {Parts.Count} 个零件，{pending} 个待转换";
        }
        OnPropertyChanged(nameof(CanConvert));
    }

    private void ResetOutputDirectories()
    {
        XtDirectory = string.Empty;
        SolidWorksDirectory = string.Empty;
        AssemblyOutputPath = string.Empty;
    }

    private void SetSourceKind(ConversionSourceKind value)
    {
        if (!SetField(ref _sourceKind, value, nameof(SourceKind)))
            return;
        OnPropertyChanged(nameof(IsAssemblyMode));
        OnPropertyChanged(nameof(SourcePartColumnHeader));
        OnPropertyChanged(nameof(IsPartDirectoryMode));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PartsPanelTitle));
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(CanContinueWhenPartFails));
        OnPropertyChanged(nameof(CanRebuildMates));
        OnPropertyChanged(nameof(RebuildMatesHint));
        OnPropertyChanged(nameof(IsRenameMode));
        OnPropertyChanged(nameof(ShowConversionOptions));
        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanConvert));
    }

    private void NotifySourceChanged()
    {
        OnPropertyChanged(nameof(SourceAssemblyPath));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanConvert));
    }

    private void QueueUiUpdate(Action update)
    {
        lock (_dispatchGate)
        {
            if (_disposed)
                return;
            _pendingUiUpdates.Enqueue(update);
            if (_dispatchOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                return;
            _dispatchOperation = _uiDispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(DrainUiUpdates));
        }
    }

    private void DrainUiUpdates()
    {
        while (true)
        {
            Action update;
            lock (_dispatchGate)
            {
                if (_disposed || _pendingUiUpdates.Count == 0)
                {
                    _pendingUiUpdates.Clear();
                    _dispatchOperation = null;
                    return;
                }
                update = _pendingUiUpdates.Dequeue();
            }
            update();
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
