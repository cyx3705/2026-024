using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class SE2SWViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runWorker;
    private readonly Action _validateEnvironment;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _lifecycleGate = new();
    private readonly object _dispatchGate = new();
    private readonly Queue<Action> _pendingUiUpdates = new();
    private ConversionMode _mode = ConversionMode.Ohs;
    private string _selectedDirectory = "";
    private string _sourceDirectory = "";
    private string _xtDirectory = "";
    private string _solidWorksDirectory = "";
    private string _statusText = "请选择 OHS 项目";
    private bool _isRunning;
    private bool _recognizeFeatures = true;
    private bool _fullyDefineSketches = true;
    private CancellationTokenSource? _batchCancellation;
    private Task? _activeRun;
    private DispatcherOperation? _dispatchOperation;
    private bool _disposed;
    private ProjectLayout? _layout;

    public ObservableCollection<ConversionFileRow> Files { get; } = [];

    public ConversionMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetField(ref _mode, value))
                return;
            OnPropertyChanged(nameof(IsOhsMode));
            OnPropertyChanged(nameof(IsExternalMode));
            OnPropertyChanged(nameof(PrimaryActionText));
        }
    }

    public bool IsOhsMode => Mode == ConversionMode.Ohs;
    public bool IsExternalMode => Mode == ConversionMode.External;

    public string SelectedDirectory
    {
        get => _selectedDirectory;
        set => SetField(ref _selectedDirectory, value);
    }

    public string SourceDirectory
    {
        get => _sourceDirectory;
        private set => SetField(ref _sourceDirectory, value);
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

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value))
                return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanFullyDefineSketches));
        }
    }

    /// <summary>V2.0：导入后用 FeatureWorks 自动识别特征。</summary>
    public bool RecognizeFeatures
    {
        get => _recognizeFeatures;
        set
        {
            if (!SetField(ref _recognizeFeatures, value))
                return;
            OnPropertyChanged(nameof(CanFullyDefineSketches));
        }
    }

    /// <summary>V2.0：对识别出的每个草图执行"完全定义草图"。</summary>
    public bool FullyDefineSketches
    {
        get => _fullyDefineSketches;
        set => SetField(ref _fullyDefineSketches, value);
    }

    public bool CanFullyDefineSketches => CanEdit && RecognizeFeatures;

    public bool CanEdit => !IsRunning;
    public string PrimaryActionText => IsOhsMode ? "按 OHS 目录转换" : "在当前文件夹转换";
    public string SelectionText => $"已选择 {Files.Count(file => file.IsSelected)} / {Files.Count}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public SE2SWViewModel()
        : this(
            (request, progress, cancellationToken) =>
                new WorkerClient().RunAsync(request, progress, cancellationToken),
            () => PreflightValidator.ValidateEnvironment(WorkerClient.WorkerPath),
            Dispatcher.CurrentDispatcher)
    {
    }

    internal SE2SWViewModel(
        Func<BatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> runWorker,
        Action validateEnvironment,
        Dispatcher uiDispatcher)
    {
        _runWorker = runWorker ?? throw new ArgumentNullException(nameof(runWorker));
        _validateEnvironment = validateEnvironment ?? throw new ArgumentNullException(nameof(validateEnvironment));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public void SetMode(ConversionMode mode)
    {
        if (IsRunning || Mode == mode)
            return;
        Mode = mode;
        ClearScan();
        StatusText = mode == ConversionMode.Ohs ? "请选择 OHS 项目" : "请选择文件夹";
        if (!string.IsNullOrWhiteSpace(SelectedDirectory))
            Scan(allowExternalFallback: false);
    }

    public void SetDirectory(string directory)
    {
        if (IsRunning)
            return;

        SelectedDirectory = directory;
        Scan();
    }

    public void Scan() => Scan(allowExternalFallback: true);

    private void Scan(bool allowExternalFallback)
    {
        if (IsRunning)
            return;
        ClearScan();
        var switchedToExternal = false;
        try
        {
            if (IsOhsMode)
            {
                try
                {
                    _layout = OhsProjectResolver.Resolve(SelectedDirectory);
                    SelectedDirectory = _layout.ProjectRoot;
                    SourceDirectory = _layout.SourceDirectory;
                    XtDirectory = _layout.XtDirectory;
                    SolidWorksDirectory = _layout.SolidWorksDirectory;
                }
                catch (DirectoryNotFoundException) when (
                    allowExternalFallback && FileScanner.HasPartFiles(SelectedDirectory))
                {
                    Mode = ConversionMode.External;
                    switchedToExternal = true;
                }
            }

            if (IsExternalMode)
            {
                _layout = null;
                SourceDirectory = Path.GetFullPath(SelectedDirectory.Trim());
                var directories = ConversionPathLayout.ResolveExternalDirectories(SourceDirectory);
                XtDirectory = directories.XtDirectory;
                SolidWorksDirectory = directories.SolidWorksDirectory;
            }

            foreach (var candidate in FileScanner.Scan(Mode, SelectedDirectory, _layout))
            {
                var row = new ConversionFileRow(candidate);
                row.PropertyChanged += OnRowPropertyChanged;
                Files.Add(row);
            }
            StatusText = switchedToExternal
                ? $"已切换到零件转换模式，扫描完成，共 {Files.Count} 个文件"
                : Files.Count == 0
                    ? "未找到 .par 文件"
                    : $"扫描完成，共 {Files.Count} 个文件";
            OnPropertyChanged(nameof(SelectionText));
        }
        catch (Exception ex)
        {
            StatusText = IsOhsMode
                ? $"{ex.Message}。OHS 兼容请选择项目根或 b-Module-SE；普通零件目录请切换到零件转换。"
                : ex.Message;
        }
    }

    public void SelectAll(bool selected)
    {
        if (IsRunning)
            return;
        foreach (var row in Files)
            row.IsSelected = selected;
        OnPropertyChanged(nameof(SelectionText));
    }

    public Task StartAsync()
    {
        CancellationTokenSource cancellation;
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
                return _activeRun;

            cancellation = new CancellationTokenSource();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _batchCancellation = cancellation;
            _activeRun = completion.Task;
        }

        _ = RunAndCompleteAsync(cancellation, completion);
        return completion.Task;
    }

    private async Task RunAndCompleteAsync(
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        var selected = Array.Empty<ConversionFileRow>();

        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            selected = Files.Where(row => row.IsSelected).ToArray();
            if (selected.Length == 0)
            {
                StatusText = "没有选中可转换文件";
                return;
            }

            _validateEnvironment();
            if (IsOhsMode)
            {
                _layout = OhsProjectResolver.Resolve(SelectedDirectory);
                _ = OhsProjectResolver.GetRequiredMoves(_layout);
                OhsProjectResolver.PrepareOutputDirectories(_layout);
            }
            else
            {
                ExternalOutputLayout.EnsureDirectories(SourceDirectory);
            }

            var jobs = selected.Select(row => new ConversionJob(
                row.Id,
                row.SourcePath,
                row.XtPath,
                row.SolidWorksPath)).ToArray();
            PreflightValidator.ValidateJobs(jobs, overwrite: false);

            foreach (var row in selected)
            {
                row.Status = "排队";
                row.Detail = "";
                row.ResetFeatureResult();
            }

            var request = new BatchRequest(
                Guid.NewGuid().ToString("N"),
                Mode,
                jobs,
                Overwrite: false,
                RecognizeFeatures: RecognizeFeatures,
                FullyDefineSketches: RecognizeFeatures && FullyDefineSketches);
            IsRunning = true;
            StatusText = $"正在转换 {jobs.Length} 个文件";
            var exitCode = await _runWorker(
                request,
                workerEvent => QueueUiUpdate(() => ApplyWorkerEvent(workerEvent)),
                cancellation.Token).ConfigureAwait(false);
            QueueUiUpdate(() =>
                StatusText = exitCode == 0 ? "转换完成" : "转换结束，存在失败项");
        }
        catch (OperationCanceledException)
        {
            QueueUiUpdate(() =>
            {
                foreach (var row in selected.Where(row =>
                             row.Status is "排队" or "导出 XT" or "生成 SW" or "识别特征" or "定义草图"))
                    row.Status = "已取消";
                StatusText = "转换已取消";
            });
        }
        catch (Exception ex)
        {
            QueueUiUpdate(() => StatusText = ex.Message);
        }
        finally
        {
            QueueUiUpdate(() =>
            {
                IsRunning = false;
                ScanAfterRun();
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_activeRun, completion.Task))
                        _activeRun = null;
                }
            });

            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_batchCancellation, cancellation))
                    _batchCancellation = null;
            }
            cancellation.Dispose();
            completion.TrySetResult();
        }
    }

    public void Cancel()
    {
        if (!IsRunning)
            return;
        StatusText = "正在取消";
        lock (_lifecycleGate)
            _batchCancellation?.Cancel();
    }

    public void Dispose()
    {
        Task? activeRun;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            activeRun = _activeRun;
            _batchCancellation?.Cancel();
        }

        lock (_dispatchGate)
        {
            _pendingUiUpdates.Clear();
            if (_dispatchOperation?.Status == DispatcherOperationStatus.Pending)
                _dispatchOperation.Abort();
            _dispatchOperation = null;
        }

        activeRun?.GetAwaiter().GetResult();

        foreach (var row in Files)
            row.PropertyChanged -= OnRowPropertyChanged;
        Files.Clear();
    }

    private void QueueUiUpdate(Action update)
    {
        lock (_dispatchGate)
        {
            if (_disposed)
                return;
            _pendingUiUpdates.Enqueue(update);
            if (_dispatchOperation?.Status is DispatcherOperationStatus.Pending
                or DispatcherOperationStatus.Executing)
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

    private void ApplyWorkerEvent(WorkerEvent workerEvent)
    {
        if (workerEvent.JobId is null)
        {
            StatusText = ConversionProgressPresenter.FormatMessage(workerEvent);
            return;
        }
        var row = Files.FirstOrDefault(item => item.Id == workerEvent.JobId);
        if (row is null)
            return;
        row.Status = ConversionProgressPresenter.GetRowStatus(workerEvent, row.Status);
        row.Detail = ConversionProgressPresenter.FormatMessage(workerEvent);
        ConversionProgressPresenter.ApplyFeatureOutcome(row, workerEvent.Feature);
    }

    private void ScanAfterRun()
    {
        OnPropertyChanged(nameof(SelectionText));
    }

    private void ClearScan()
    {
        foreach (var row in Files)
            row.PropertyChanged -= OnRowPropertyChanged;
        Files.Clear();
        _layout = null;
        SourceDirectory = "";
        XtDirectory = "";
        SolidWorksDirectory = "";
        OnPropertyChanged(nameof(SelectionText));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ConversionFileRow.IsSelected))
            OnPropertyChanged(nameof(SelectionText));
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
