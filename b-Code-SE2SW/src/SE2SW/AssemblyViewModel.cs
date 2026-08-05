using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class AssemblyViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> _probeWorker;
    private readonly Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runWorker;
    private readonly Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runPartWorker;
    private readonly Action _validateEnvironment;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _lifecycleGate = new();
    private readonly object _dispatchGate = new();
    private readonly Queue<Action> _pendingUiUpdates = new();
    private string _sourceAssemblyPath = string.Empty;
    private string _partDirectory = string.Empty;
    private ConversionSourceKind _sourceKind;
    private string _xtDirectory = string.Empty;
    private string _solidWorksDirectory = string.Empty;
    private string _assemblyOutputPath = string.Empty;
    private string _statusText = "请选择装配体或零件文件夹";
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
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeOperation;
    private DispatcherOperation? _dispatchOperation;
    private AssemblyConversionPlan? _plan;
    private MateOutcome? _mateOutcome;
    private string? _sourceHashAfterProbe;
    private bool _disposed;

    public ObservableCollection<AssemblyTreeNode> AssemblyTree { get; } = [];
    public ObservableCollection<ConversionFileRow> Parts { get; } = [];

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
            OnPropertyChanged(nameof(CanFullyDefineSketches));
            OnPropertyChanged(nameof(CanRebuildMates));
            OnPropertyChanged(nameof(CanContinueWhenPartFails));
        }
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetField(ref _isProbing, value);
    }

    public bool RecognizeFeatures
    {
        get => _recognizeFeatures;
        set
        {
            if (!SetField(ref _recognizeFeatures, value))
                return;
            if (!value)
                FullyDefineSketches = false;
            OnPropertyChanged(nameof(CanFullyDefineSketches));
        }
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
    public string SourceLabel => SourceKind switch
    {
        ConversionSourceKind.Assembly => "装配体",
        ConversionSourceKind.PartDirectory => "零件文件夹",
        _ => "选择来源",
    };
    public bool IsAssemblyMode => SourceKind == ConversionSourceKind.Assembly;
    public bool IsPartDirectoryMode => SourceKind == ConversionSourceKind.PartDirectory;
    public bool CanEdit => !IsBusy;
    public bool CanProbe => CanEdit && IsAssemblyMode && File.Exists(SourceAssemblyPath)
        && ConversionPathLayout.HasExtension(SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension);
    public bool CanConvert => CanEdit && (IsAssemblyMode
        ? !_conversionCompleted && _plan?.CanConvert == true
        : IsPartDirectoryMode && Parts.Any(row => !row.HasExistingOutput));
    public bool CanFullyDefineSketches => CanEdit && RecognizeFeatures;
    public bool CanContinueWhenPartFails => CanEdit && IsAssemblyMode;
    public string PrimaryActionText => IsPartDirectoryMode ? "转换全部零件" : "转换装配体";
    public string PartsPanelTitle => IsPartDirectoryMode ? "零件" : "唯一零件";
    public string OperationText => IsProbing
        ? "正在解析装配体"
        : IsPartDirectoryMode ? "正在转换全部零件" : "正在转换装配体";

    public event PropertyChangedEventHandler? PropertyChanged;

    public AssemblyViewModel()
        : this(
            (request, progress, cancellationToken) =>
                new WorkerClient().ProbeAssemblyAsync(request, progress, cancellationToken),
            (request, progress, cancellationToken) =>
                new WorkerClient().RunAssemblyAsync(request, progress, cancellationToken),
            () => PreflightValidator.ValidateEnvironment(WorkerClient.WorkerPath),
            Dispatcher.CurrentDispatcher,
            (request, progress, cancellationToken) =>
                new WorkerClient().RunAsync(request, progress, cancellationToken))
    {
    }

    internal AssemblyViewModel(
        Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> probeWorker,
        Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> runWorker,
        Action validateEnvironment,
        Dispatcher uiDispatcher,
        Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>>? runPartWorker = null)
    {
        _probeWorker = probeWorker ?? throw new ArgumentNullException(nameof(probeWorker));
        _runWorker = runWorker ?? throw new ArgumentNullException(nameof(runWorker));
        _runPartWorker = runPartWorker
            ?? ((request, progress, cancellationToken) =>
                new WorkerClient().RunAsync(request, progress, cancellationToken));
        _validateEnvironment = validateEnvironment ?? throw new ArgumentNullException(nameof(validateEnvironment));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public void SetSourceFile(string path)
        => SetAssemblySource(path);

    public void SetAssemblySource(string path)
    {
        if (IsBusy)
            return;

        ClearSourceResults();
        _sourceAssemblyPath = Path.GetFullPath(path.Trim());
        _partDirectory = string.Empty;
        SetSourceKind(ConversionSourceKind.Assembly);
        RebuildMates = false;
        UpdateAssemblyOutputPaths();
        StatusText = "正在准备解析装配体";
        NotifySourceChanged();
    }

    public void SetPartDirectory(string path)
    {
        if (IsBusy)
            return;

        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"文件夹不存在：{fullPath}");

        ClearSourceResults();
        _sourceAssemblyPath = string.Empty;
        _partDirectory = fullPath;
        SetSourceKind(ConversionSourceKind.PartDirectory);
        ContinueWhenPartFails = false;
        RebuildMates = false;
        ScanPartDirectory(updateStatus: true);
        NotifySourceChanged();
    }

    public Task ProbeAsync()
        => StartOperationAsync(isProbe: true, ProbeCoreAsync);

    public Task ConvertAsync()
        => StartOperationAsync(
            isProbe: false,
            IsPartDirectoryMode ? ConvertPartsCoreAsync : ConvertAssemblyCoreAsync);

    public void Cancel()
    {
        if (!IsBusy)
            return;
        StatusText = "正在取消";
        lock (_lifecycleGate)
            _operationCancellation?.Cancel();
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

        activeOperation?.GetAwaiter().GetResult();
        AssemblyTree.Clear();
        Parts.Clear();
    }

    private Task StartOperationAsync(bool isProbe, Func<CancellationToken, Task> operation)
    {
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
        }

        IsProbing = isProbe;
        IsBusy = true;
        OnPropertyChanged(nameof(OperationText));
        _ = RunOperationAndCompleteAsync(operation, cancellation, completion);
        return completion.Task;
    }

    private async Task RunOperationAndCompleteAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            QueueUiUpdate(() => StatusText = "操作已取消");
        }
        catch (Exception ex)
        {
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
            completion.TrySetResult();
        }
    }

    private async Task ProbeCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension))
            throw new InvalidOperationException("请选择存在的 Solid Edge .asm 文件。");
        _validateEnvironment();

        var batchId = Guid.NewGuid().ToString("N");
        var resultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SE2SWIdentity.HostApplicationDataDirectoryName,
            SE2SWIdentity.ModuleApplicationDataDirectoryName,
            SE2SWIdentity.ProbesDirectoryName);
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, batchId + ".result.json");
        try
        {
            var request = new AssemblyProbeRequest(batchId, Path.GetFullPath(SourceAssemblyPath), resultPath);
            QueueUiUpdate(() => StatusText = "正在只读解析装配树");
            var result = await _probeWorker(
                request,
                workerEvent => QueueUiUpdate(() => ApplyWorkerEvent(workerEvent)),
                cancellationToken).ConfigureAwait(false);
            var plan = AssemblyPlanner.Create(result);
            var sourceHash = ComputeSha256(result.SourceAssemblyPath);
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
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源 .asm 在解析后发生变化，请重新解析后再转换。");

        _validateEnvironment();
        ExternalOutputLayout.EnsureDirectories(plan.SourceDirectory);
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
            Relations: plan.Relations);
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
                QueueUiUpdate(() => ApplyWorkerEvent(workerEvent));
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
    }

    private async Task ConvertPartsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPartDirectoryMode || !Directory.Exists(_partDirectory))
            throw new DirectoryNotFoundException("请选择存在的零件文件夹。");

        var pending = Parts.Where(row => !row.HasExistingOutput).ToArray();
        if (pending.Length == 0)
            throw new InvalidOperationException("没有尚待转换的零件。");

        _validateEnvironment();
        ExternalOutputLayout.EnsureDirectories(_partDirectory);
        var jobs = pending.Select(row => new ConversionJob(
            row.Id,
            row.SourcePath,
            row.XtPath,
            row.SolidWorksPath)).ToArray();
        PreflightValidator.ValidateJobs(jobs, overwrite: false);

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
            FullyDefineSketches: RecognizeFeatures && FullyDefineSketches);
        QueueUiUpdate(() => StatusText = $"正在转换 {jobs.Length} 个零件");
        var exitCode = await _runPartWorker(
            request,
            workerEvent => QueueUiUpdate(() => ApplyWorkerEvent(workerEvent)),
            cancellationToken).ConfigureAwait(false);
        QueueUiUpdate(() => StatusText = exitCode == 0 ? "零件转换完成" : "零件转换结束，存在失败项");
    }

    /// <summary>状态栏尾巴：一句话说清 56 条关系去了哪里。</summary>
    private string FormatMateSummary()
    {
        if (_mateOutcome is not { RelationTotal: > 0 } mate)
            return string.Empty;
        var failed = mate.FailedUnmatched + mate.FailedAmbiguous + mate.FailedRejected;
        var text = $"；配合重建 {mate.MateRebuilt}/{mate.RelationTotal}";
        if (mate.GroundApplied > 0)
            text += $"（另有 {mate.GroundApplied} 条接地关系落为固定）";
        if (failed > 0)
            text += $"，{failed} 条未建立";
        if (mate.ComponentsLeftFixed > 0)
            text += $"，{mate.ComponentsLeftFixed} 个组件保持固定";
        return text;
    }

    /// <summary>
    /// 把逐条诊断并进警告栏。V3.5 的承诺是"建不起来的如实报告"——
    /// 报告只写进日志、用户看不见的话，这个承诺就没兑现。
    /// </summary>
    private void AppendMateDiagnostics()
    {
        if (_mateOutcome is not { Diagnostics.Count: > 0 } mate)
            return;
        var summary = string.Join("；", mate.Diagnostics.Take(6));
        if (mate.Diagnostics.Count > 6)
            summary += $"；……另有 {mate.Diagnostics.Count - 6} 条，详见转换日志";
        WarningSummary = string.IsNullOrWhiteSpace(WarningSummary)
            ? summary
            : WarningSummary + "；" + summary;
    }

    private void ApplyProbeResult(
        AssemblyProbeResult result,
        AssemblyConversionPlan plan,
        string sourceHash)
    {
        ClearProbeResult();
        _mateOutcome = null;
        _plan = plan;
        // 每次解析都依据新装配的真实关系数重置默认值。这样切换到无关系装配不会留下
        // 一个看似可用、实际不会执行的勾选状态；切回有关系装配也无需用户额外发现设置。
        RebuildMates = plan.RelationCount > 0;
        _sourceHashAfterProbe = sourceHash;
        XtDirectory = plan.XtDirectory;
        SolidWorksDirectory = plan.SolidWorksDirectory;
        AssemblyOutputPath = plan.AssemblyOutputPath;
        AssemblyTree.Add(AssemblyTreeNode.Build(result, plan.Nodes));
        foreach (var candidate in plan.Parts)
            Parts.Add(new ConversionFileRow(candidate));

        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues.Select(issue => $"[{issue.ErrorClass}] {issue.Message}"));
        WarningSummary = string.Join("；", plan.Warnings.Concat(
            string.IsNullOrWhiteSpace(issueText) ? [] : new[] { issueText }));
        StatusText = plan.CanConvert
            ? plan.IsNested
                ? $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件、"
                    + $"{plan.SubAssemblyCount} 个子装配，最大 {plan.MaxDepth} 层、{plan.RelationCount} 条装配关系；按层级生成嵌套装配"
                : $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件；最终输出会展平"
            : $"解析完成，但有 {plan.BlockingIssues.Count} 个前置错误";
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanRebuildMates));
        OnPropertyChanged(nameof(RebuildMatesHint));
    }

    private void ApplyWorkerEvent(WorkerEvent workerEvent)
    {
        // 配合结果既可能直接挂在事件上，也可能随装配结果一起回来。
        _mateOutcome = workerEvent.Mate ?? workerEvent.Assembly?.Mate ?? _mateOutcome;
        if (workerEvent.JobId is null)
        {
            StatusText = ConversionProgressPresenter.FormatMessage(workerEvent);
            return;
        }
        var row = Parts.FirstOrDefault(item => item.Id == workerEvent.JobId);
        if (row is null)
            return;
        row.Status = ConversionProgressPresenter.GetRowStatus(workerEvent, row.Status);
        row.Detail = ConversionProgressPresenter.FormatMessage(workerEvent);
        ConversionProgressPresenter.ApplyFeatureOutcome(row, workerEvent.Feature);
    }

    private void UpdateAssemblyOutputPaths()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourceAssemblyPath))
                throw new InvalidDataException();
            var fullPath = Path.GetFullPath(SourceAssemblyPath.Trim());
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException();
            var directories = ConversionPathLayout.ResolveExternalDirectories(directory);
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
        _sourceHashAfterProbe = null;
        _conversionCompleted = false;
        AssemblyTree.Clear();
        Parts.Clear();
        WarningSummary = "输出按源装配的层级生成嵌套装配体，全部组件固定，不含配合。";
        OnPropertyChanged(nameof(CanConvert));
    }

    private void ClearSourceResults()
    {
        ClearProbeResult();
        _mateOutcome = null;
        XtDirectory = string.Empty;
        SolidWorksDirectory = string.Empty;
        AssemblyOutputPath = string.Empty;
    }

    private void ScanPartDirectory(bool updateStatus)
    {
        Parts.Clear();
        var directories = ConversionPathLayout.ResolveExternalDirectories(_partDirectory);
        XtDirectory = directories.XtDirectory;
        SolidWorksDirectory = directories.SolidWorksDirectory;
        AssemblyOutputPath = string.Empty;
        foreach (var candidate in FileScanner.Scan(ConversionMode.External, _partDirectory))
            Parts.Add(new ConversionFileRow(candidate));

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

    private void SetSourceKind(ConversionSourceKind value)
    {
        if (!SetField(ref _sourceKind, value, nameof(SourceKind)))
            return;
        OnPropertyChanged(nameof(IsAssemblyMode));
        OnPropertyChanged(nameof(IsPartDirectoryMode));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PartsPanelTitle));
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(CanContinueWhenPartFails));
        OnPropertyChanged(nameof(CanRebuildMates));
        OnPropertyChanged(nameof(RebuildMatesHint));
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
