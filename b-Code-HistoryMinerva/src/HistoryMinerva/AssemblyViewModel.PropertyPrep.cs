using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed partial class AssemblyViewModel
{
    public bool IsRenameMode
        => SelectedMappingContent.Kind == MappingContent.SolidWorksAssemblyPropertyPrep;

    public bool ShowConversionOptions => !IsRenameMode;

    public bool CanStrip => CanEdit && IsRenameMode
        && !_conversionCompleted && _probeResult is not null;

    internal bool CanExecuteStrip => CanStrip && _stripPlan?.CanRename == true;

    internal string RenameBlockedReason
    {
        get
        {
            if (!IsRenameMode)
                return StatusText;
            if (!CanEdit)
                return "正在执行操作。";
            if (_probeResult is null)
                return "请先解析装配体。";
            if (string.IsNullOrWhiteSpace(_drawingPrefix) || _renamePlan is null)
                return "请填写图号前缀后再按图号改名。";
            if (_renamePlan.BlockingIssues.Count > 0)
                return string.Join("；", _renamePlan.BlockingIssues);
            if (!_renamePlan.CanRename)
                return "图号已与规则一致，没有需要改名的文件。";
            if (_conversionCompleted)
                return "本轮改名已完成，请重新解析后再改名。";
            return StatusText;
        }
    }

    internal string StripBlockedReason
    {
        get
        {
            if (!IsRenameMode)
                return "洗图号只在「属性整备（改名）」下可用，不能在特征整备或转换模式里用。";
            if (!CanEdit)
                return "正在执行操作。";
            if (_probeResult is null)
                return "请先解析装配体。";
            if (_stripPlan is null)
                return "请先解析装配体。";
            if (_stripPlan.BlockingIssues.Count > 0)
                return string.Join("；", _stripPlan.BlockingIssues);
            if (!_stripPlan.CanRename)
                return "没有可按空格洗掉的图号。";
            if (_conversionCompleted)
                return "本轮改名已完成，请重新解析后再洗图号。";
            return StatusText;
        }
    }

    public async Task StripDrawingNumbersAsync(IProgress<string>? progress = null)
    {
        _operationProgress = progress;
        _isStripping = true;
        OnPropertyChanged(nameof(OperationText));
        try
        {
            await StartOperationAsync(isProbe: false, StripCoreAsync);
        }
        finally
        {
            _isStripping = false;
            OnPropertyChanged(nameof(OperationText));
            if (ReferenceEquals(_operationProgress, progress))
                _operationProgress = null;
        }
    }

    public string DrawingPrefix
    {
        get => _drawingPrefix;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (_drawingPrefix == trimmed)
                return;
            _drawingPrefix = trimmed;
            OnPropertyChanged();
            ApplyRenamePreview();
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanStrip));
        }
    }

    internal bool CanKeepSolidWorksAssembly(MappingContentOption next)
        => _sourceKind == ConversionSourceKind.Assembly
           && !string.IsNullOrWhiteSpace(_sourceAssemblyPath)
           && SelectedMappingContent.IsAssemblySource
           && next.IsAssemblySource
           && SelectedMappingContent.SourceFormat == next.SourceFormat
           && next.SourceFormat == ConversionSourceFormat.SolidWorks;

    private void SelectMappingContentForSource(MappingContent kind)
    {
        var selected = MappingContentOption.Available.Single(option => option.Kind == kind);
        if (EqualityComparer<MappingContentOption>.Default.Equals(_selectedMappingContent, selected))
        {
            SyncFeatureRecognitionDefault(kind);
            return;
        }

        _suppressMappingContentChange = true;
        try
        {
            _selectedMappingContent = selected;
            SyncFeatureRecognitionDefault(kind);
            OnPropertyChanged(nameof(SelectedMappingContent));
            OnPropertyChanged(nameof(IsRenameMode));
            OnPropertyChanged(nameof(ShowConversionOptions));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(SourcePartColumnHeader));
            OnPropertyChanged(nameof(IsAssemblyMode));
            OnPropertyChanged(nameof(IsPartDirectoryMode));
        }
        finally
        {
            _suppressMappingContentChange = false;
        }
    }

    private void SyncFeatureRecognitionDefault(MappingContent kind)
    {
        var next = kind == MappingContent.SolidWorksAssemblyToSolidWorksAssembly;
        if (_recognizeFeatures == next)
            return;
        _recognizeFeatures = next;
        _fullyDefineSketches = next;
        ApplyRegenerationToRows();
        OnPropertyChanged(nameof(RecognizeFeatures));
        OnPropertyChanged(nameof(FullyDefineSketches));
    }

    private void ApplyMappingContentChange(MappingContentOption value)
    {
        var keepAssembly = CanKeepSolidWorksAssembly(value);
        var keptAssembly = _sourceAssemblyPath;
        var keptProbe = _probeResult;
        var keptPlan = _plan;
        var keptHash = _sourceHashAfterProbe;
        _selectedMappingContent = value;
        if (keepAssembly)
        {
            ResetOutputDirectories();
            _partDirectory = string.Empty;
            RebuildMates = false;
            _sourceAssemblyPath = keptAssembly;
            SetSourceKind(ConversionSourceKind.Assembly);
            UpdateAssemblyOutputPaths();
            if (keptProbe is not null && keptPlan is not null && keptHash is not null)
                ApplyProbeResult(keptProbe, keptPlan, keptHash);
            else
                StatusText = "已选择装配体，点击解析装配体";
            SyncFeatureRecognitionDefault(value.Kind);
            return;
        }

        ClearSourceResults();
        ResetOutputDirectories();
        _partDirectory = string.Empty;
        RebuildMates = false;
        _sourceAssemblyPath = string.Empty;
        SetSourceKind(ConversionSourceKind.None);
        StatusText = "请选择转换来源";
        SyncFeatureRecognitionDefault(value.Kind);
    }

    private void ApplyRenamePreview()
    {
        if (!IsRenameMode || _probeResult is null)
        {
            _renamePlan = null;
            _stripPlan = null;
            OnPropertyChanged(nameof(CanStrip));
            return;
        }

        _stripPlan = PropertyPrepPlanner.CreateStrip(_probeResult);
        var plan = string.IsNullOrWhiteSpace(_drawingPrefix)
            ? _stripPlan
            : PropertyPrepPlanner.Create(_probeResult, _drawingPrefix);
        _renamePlan = string.IsNullOrWhiteSpace(_drawingPrefix) ? null : plan;
        RenderPropertyPrepRows(
            plan,
            stripPreview: string.IsNullOrWhiteSpace(_drawingPrefix));
        OnPropertyChanged(nameof(CanStrip));
        OnPropertyChanged(nameof(SourcePartColumnHeader));
    }

    private void RenderPropertyPrepRows(AssemblyRenamePlan plan, bool stripPreview)
    {
        Parts.Clear();
        foreach (var entry in plan.Entries.Concat(plan.Unnumbered))
        {
            var row = new ConversionFileRow(
                new ScanCandidate(entry.SourcePath, entry.SourcePath, entry.TargetPath, false),
                id: entry.Id);
            if (!entry.AssignsDrawingNumber)
            {
                row.Status = stripPreview ? "无空格" : "无图号";
                row.Detail = stripPreview ? "文件名没有空格，保持原名" : "标准件/外购件内部，保持原名";
                row.FeatureText = "—";
                row.SketchText = "—";
            }
            else if (AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
            {
                row.Status = "已符合";
                row.Detail = Path.GetFileName(entry.TargetPath);
                row.FeatureText = entry.DrawingNumber;
                row.SketchText = "—";
            }
            else
            {
                row.Status = ConversionFileRow.ReadyStatus;
                row.Detail = Path.GetFileName(entry.TargetPath);
                row.FeatureText = entry.DrawingNumber;
                row.SketchText = "—";
            }

            Parts.Add(row);
        }

        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues);
        WarningSummary = string.Join("；", plan.Warnings.Concat(
            string.IsNullOrWhiteSpace(issueText) ? [] : new[] { issueText }));
        var pending = plan.Entries.Count(entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath));
        StatusText = plan.BlockingIssues.Count > 0
            ? (stripPreview ? $"洗图号规划有 {plan.BlockingIssues.Count} 个问题" : $"图号规划有 {plan.BlockingIssues.Count} 个问题")
            : stripPreview
                ? plan.CanRename
                    ? $"可按空格洗掉 {pending} 个文件的图号"
                    : "没有可按空格洗掉的图号"
                : plan.CanRename
                    ? $"图号规划完成：{pending} 个文件待改名"
                    : "图号已与规则一致，无需改名";
    }

    private async Task RenameCoreAsync(CancellationToken cancellationToken)
        => await RunRenamePlanAsync(
            _renamePlan ?? throw new InvalidOperationException("请先成功解析装配体并填写图号前缀。"),
            stripBySpace: false,
            "请填写图号前缀后再按图号改名。",
            "正在按图号改名",
            "属性整备改名",
            cancellationToken).ConfigureAwait(false);

    private async Task StripCoreAsync(CancellationToken cancellationToken)
        => await RunRenamePlanAsync(
            _stripPlan ?? throw new InvalidOperationException("请先成功解析装配体。"),
            stripBySpace: true,
            "没有可按空格洗掉的图号。",
            "正在按空格洗图号",
            "属性整备洗图号",
            cancellationToken).ConfigureAwait(false);

    private async Task RunRenamePlanAsync(
        AssemblyRenamePlan plan,
        bool stripBySpace,
        string emptyMessage,
        string progressPrefix,
        string resultPrefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.CanRename)
            throw new InvalidOperationException(
                plan.BlockingIssues.Count > 0
                    ? string.Join("；", plan.BlockingIssues)
                    : emptyMessage);
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再改名。");

        _validateEnvironment(ConversionSourceFormat.SolidWorks);
        var pending = plan.Entries
            .Where(entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
            .ToArray();
        foreach (var row in Parts)
        {
            var entry = pending.FirstOrDefault(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
            if (entry is null)
                continue;
            row.Status = "排队";
            row.Detail = Path.GetFileName(entry.TargetPath);
        }

        QueueUiUpdate(() => StatusText = $"{progressPrefix} {pending.Length} 个文件");
        var request = new AssemblyRenameRequest(
            Guid.NewGuid().ToString("N"),
            plan.SourceAssemblyPath,
            plan.DrawingPrefix,
            pending,
            ConversionSourceFormat.SolidWorks,
            stripBySpace);
        var exitCode = await _renameWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);
        QueueUiUpdate(() =>
        {
            _conversionCompleted = exitCode == 0;
            StatusText = exitCode == 0
                ? $"{resultPrefix}完成，共 {pending.Length} 个文件"
                : $"{resultPrefix}失败";
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanStrip));
        });
        _lastOperationSucceeded = exitCode == 0;
        if (exitCode != 0)
            throw new InvalidOperationException($"{resultPrefix}失败，详见 Vulcan 控制台。");
    }

    /// <summary>探查结果进命令总线。前置错误必须出现在文本里，页面按 REQ-006 不绑 StatusText。</summary>
    internal static string FormatProbeStatus(
        AssemblyConversionPlan plan,
        AssemblyProbeResult result,
        string issueText)
    {
        if (plan.CanConvert)
        {
            return plan.IsNested
                ? $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件、"
                    + $"{plan.SubAssemblyCount} 个子装配，最大 {plan.MaxDepth} 层、{plan.RelationCount} 条装配关系；按层级生成嵌套装配"
                : $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件；最终输出会展平";
        }

        return string.IsNullOrWhiteSpace(issueText)
            ? $"解析完成，但有 {plan.BlockingIssues.Count} 个前置错误"
            : $"解析完成，但有 {plan.BlockingIssues.Count} 个前置错误：{issueText}";
    }

    private void ApplyProbeResult(
        AssemblyProbeResult result,
        AssemblyConversionPlan plan,
        string sourceHash)
    {
        ClearProbeResult();
        _mateOutcome = null;
        _plan = plan;
        _probeResult = result;
        // 每次解析都依据新装配的真实关系数重置默认值。这样切换到无关系装配不会留下
        // 一个看似可用、实际不会执行的勾选状态；切回有关系装配也无需用户额外发现设置。
        RebuildMates = plan.RelationCount > 0;
        _sourceHashAfterProbe = sourceHash;
        XtDirectory = plan.XtDirectory;
        SolidWorksDirectory = plan.SolidWorksDirectory;
        AssemblyOutputPath = plan.AssemblyOutputPath;
        AssemblyTree.Add(AssemblyTreeNode.Build(result, plan.Nodes));
        var regeneratesExisting = RegeneratesExistingOutputs;
        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues.Select(issue => $"[{issue.ErrorClass}] {issue.Message}"));
        foreach (var candidate in plan.Parts)
            Parts.Add(new ConversionFileRow(candidate, regeneratesExisting));
        if (plan.BlockingIssues.Count > 0)
        {
            foreach (var row in Parts)
            {
                row.Status = "受阻";
                row.Detail = issueText;
            }
        }
        WarningSummary = string.Join("；", plan.Warnings.Concat(
            string.IsNullOrWhiteSpace(issueText) ? [] : new[] { issueText }));
        StatusText = FormatProbeStatus(plan, result, issueText);
        ApplyRenamePreview();
        _lastOperationSucceeded = IsRenameMode
            ? _stripPlan is not null && _stripPlan.BlockingIssues.Count == 0
            : plan.CanConvert;
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanRebuildMates));
        OnPropertyChanged(nameof(RebuildMatesHint));
    }
}
