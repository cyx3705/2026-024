using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>零件表里可以逐格改、也可以一键刷满的三个属性槽。</summary>
public enum PartPropertyField
{
    Material,
    SurfaceTreatment,
    HeatTreatment,
}

public sealed partial class AssemblyViewModel
{
    /// <summary>
    /// 每个零件待写的材料 / 表面处理 / 热处理，按源文件全路径记账。
    ///
    /// 不能只存在 <see cref="Parts"/> 行上：改一次图号前缀就会整份重建改名计划和零件行，
    /// 填了半张表的材料会跟着前缀一起没掉。行是派生的，这份账才是权威。
    ///
    /// V4.8 起这份账在解析装配体时就由零件上的**现值**预填（<see cref="AdoptProbedProperties"/>），
    /// 之后用户改哪一格就覆盖哪一格。
    /// </summary>
    private readonly Dictionary<string, PartPropertyEdit> _propertyEdits = new(StringComparer.OrdinalIgnoreCase);
    private string _designer = string.Empty;
    private string _dateText = string.Empty;

    public bool IsRenameMode
        => SelectedMappingContent.Kind == MappingContent.SolidWorksAssemblyPropertyPrep;

    public bool ShowConversionOptions => !IsRenameMode;

    public bool CanStrip => CanEdit && IsRenameMode
        && !_conversionCompleted && _probeResult is not null;

    internal bool CanExecuteStrip => CanStrip && _stripPlan?.CanRename == true;

    /// <summary>
    /// 「写入」= 改名 + 写属性，所以门是 <see cref="AssemblyRenamePlan.CanWrite"/> 而不是 CanRename。
    /// 名字已经对了但属性还没写的装配，必须仍然能点写入。
    /// </summary>
    internal bool CanWrite => CanEdit && IsRenameMode
        && !_conversionCompleted && _renamePlan?.CanWrite == true;

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
                return "请填写图号前缀后再写入。";
            if (_renamePlan.BlockingIssues.Count > 0)
                return string.Join("；", _renamePlan.BlockingIssues);
            if (!_renamePlan.CanWrite)
                return "图号已与规则一致，也没有可写属性的零件。";
            if (_conversionCompleted)
                return "本轮写入已完成，请重新解析后再写入。";
            return StatusText;
        }
    }

    /// <summary>
    /// 「设计」属性的值。一个文本框刷满全部零件——这一栏在同一批图纸里从来不是逐件不同的。
    /// </summary>
    public string Designer
    {
        get => _designer;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (_designer == trimmed)
                return;
            _designer = trimmed;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「日期」属性的值，格式见 <see cref="PartPropertyNames.DateFormat"/>。
    /// 由「一键设置日期」写入；空串表示本轮不写日期槽。
    /// </summary>
    public string DateText
    {
        get => _dateText;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (_dateText == trimmed)
                return;
            _dateText = trimmed;
            OnPropertyChanged();
        }
    }

    /// <summary>「一键设置日期」：把日期槽的待写值置为系统当日，返回写进去的文本。</summary>
    public string SetDateToday()
    {
        DateText = PartPropertyNames.Today();
        return DateText;
    }

    /// <summary>
    /// 一键刷满某一列。返回被改动的行数，让命令能如实回报「刷了几行」而不是空口说成功。
    /// 只刷会写属性的零件行：装配体和未编号的内部件不进属性写入清单，刷了也不会落盘。
    /// </summary>
    /// <param name="field">要刷的属性槽。</param>
    /// <param name="value">要写进去的值；空串表示这一槽本轮不写。</param>
    /// <param name="materialDatabase">
    /// 只对 <see cref="PartPropertyField.Material"/> 有意义：材质所在的材料库。
    /// 材质名脱离材料库无法应用到零件，两者必须一起传下来，见 <see cref="PartPropertyWrite.MaterialDatabase"/>。
    /// </param>
    public int SetAllPartProperties(PartPropertyField field, string value, string materialDatabase = "")
    {
        var text = value?.Trim() ?? string.Empty;
        var database = materialDatabase?.Trim() ?? string.Empty;
        var changed = 0;
        foreach (var row in Parts.Where(row => row.WritesProperties))
        {
            if (ApplyPartProperty(row, field, text, database))
                changed++;
        }

        return changed;
    }

    /// <summary>逐格改：按行 Id 定位。行不存在或不写属性时返回 false，由命令给出可读原因。</summary>
    /// <inheritdoc cref="SetAllPartProperties" path="/param[@name='materialDatabase']"/>
    public bool SetPartProperty(string rowId, PartPropertyField field, string value, string materialDatabase = "")
    {
        var row = FindRow(rowId);
        if (row is not { WritesProperties: true })
            return false;

        ApplyPartProperty(row, field, value?.Trim() ?? string.Empty, materialDatabase?.Trim() ?? string.Empty);
        return true;
    }

    /// <summary>读回某一格的当前值，供弹输入框时预填。</summary>
    public string GetPartProperty(string rowId, PartPropertyField field)
    {
        var row = FindRow(rowId);
        return row is null ? string.Empty : ReadPartProperty(row, field);
    }

    /// <summary>这一行的属性会不会真的落盘。页面据此决定三个可点列要不要给占位符。</summary>
    public bool WritesProperties(string rowId)
    {
        var row = FindRow(rowId);
        return row is { WritesProperties: true };
    }

    /// <summary>
    /// 三个可点列现在能不能落到零件上。改名计划还没建起来（没解析、或没填图号前缀）时是 false，
    /// 命令据此给出「请先解析并填前缀」而不是空口报「刷了 0 个零件」。
    /// </summary>
    public bool PropertyEditsReady => IsRenameMode && _renamePlan is not null;

    private bool ApplyPartProperty(
        ConversionFileRow row,
        PartPropertyField field,
        string value,
        string materialDatabase)
    {
        var key = Path.GetFullPath(row.SourcePath);
        var previousDatabase = _propertyEdits.TryGetValue(key, out var previous)
            ? previous.MaterialDatabase
            : string.Empty;
        // 材质名没变但材料库变了（同名材质换了个库）也是一次真实改动，不能当没发生。
        var unchanged = string.Equals(ReadPartProperty(row, field), value, StringComparison.Ordinal)
            && (field != PartPropertyField.Material
                || string.Equals(previousDatabase, materialDatabase, StringComparison.Ordinal));
        if (unchanged)
            return false;

        switch (field)
        {
            case PartPropertyField.Material:
                row.Material = value;
                previousDatabase = value.Length == 0 ? string.Empty : materialDatabase;
                break;
            case PartPropertyField.SurfaceTreatment:
                row.SurfaceTreatment = value;
                break;
            case PartPropertyField.HeatTreatment:
                row.HeatTreatment = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        _propertyEdits[key] = new PartPropertyEdit(
            row.Material, previousDatabase, row.SurfaceTreatment, row.HeatTreatment);
        return true;
    }

    private static string ReadPartProperty(ConversionFileRow row, PartPropertyField field)
        => field switch
        {
            PartPropertyField.Material => row.Material,
            PartPropertyField.SurfaceTreatment => row.SurfaceTreatment,
            PartPropertyField.HeatTreatment => row.HeatTreatment,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

    /// <summary>
    /// 一行零件上待写的三个槽。材料记两项：材质名给界面显示，材料库给
    /// <c>SetMaterialPropertyName2</c>——只有名字应用不上去。
    /// </summary>
    private readonly record struct PartPropertyEdit(
        string Material,
        string MaterialDatabase,
        string SurfaceTreatment,
        string HeatTreatment);

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
            OnPropertyChanged(nameof(CanWrite));
        }
    }

    internal bool CanKeepSolidWorksAssembly(MappingContentOption next)
        => _sourceKind == ConversionSourceKind.Assembly
           && !string.IsNullOrWhiteSpace(_sourceAssemblyPath)
           && SelectedMappingContent.IsAssemblySource
           && next.IsAssemblySource
           && SelectedMappingContent.SourceFormat == next.SourceFormat
           && next.SourceFormat == ConversionSourceFormat.SolidWorks;

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
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(SourcePartColumnHeader));
    }

    /// <summary>
    /// 把一份改名计划画成零件表。
    ///
    /// 行按 <see cref="RenameEntry.Id"/> **原地复用**：那个 id 由源文件全路径定死，
    /// 所以改一次图号前缀虽然让整份计划重建，同一个零件在前后两份计划里仍是同一行。
    /// 复用之后一次前缀改动只发出每行几条属性变更，而不是整表清空重填——后者在几百个
    /// 零件上就是几百次控件重建，也就是用户说的「改一格卡一下」。
    /// </summary>
    private void RenderPropertyPrepRows(AssemblyRenamePlan plan, bool stripPreview)
    {
        var rows = new List<ConversionFileRow>(plan.Entries.Count + plan.Unnumbered.Count);
        var reusedAll = true;
        foreach (var entry in plan.Entries.Concat(plan.Unnumbered))
        {
            var candidate = new ScanCandidate(entry.SourcePath, entry.SourcePath, entry.TargetPath, false);
            var row = FindRow(entry.Id);
            if (row is null)
            {
                row = new ConversionFileRow(candidate, id: entry.Id, showsTargetName: true);
            }
            else
            {
                row.Rebind(candidate);
            }

            // 顺序也要一致才算复用：改名计划把未编号件放 Unnumbered，洗图号计划把
            // 无空格件放 kept，两者拼出来的行序可以不同，只比个数会把表留在旧顺序上。
            if (reusedAll && (rows.Count >= Parts.Count || !ReferenceEquals(Parts[rows.Count], row)))
                reusedAll = false;

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

            // 判据与 Worker 侧 AssemblyRenamePlan.IsWritablePart 一致，两边各判一次。
            // 界面上让用户往装配体行里填材料，是在骗他——那一格永远不会落盘。
            // 洗图号预览（stripPreview）下不写属性，三列一律不可点。
            row.WritesProperties = !stripPreview && AssemblyRenamePlan.IsWritablePart(entry);

            // 三个槽的权威是 _propertyEdits：解析时按零件现值预填，用户改过的覆盖它。
            // 前缀一改计划整份重建，不搬回来的话填了半张表的材料会跟着前缀一起消失。
            var edit = _propertyEdits.GetValueOrDefault(Path.GetFullPath(entry.SourcePath));
            row.Material = edit.Material ?? string.Empty;
            row.SurfaceTreatment = edit.SurfaceTreatment ?? string.Empty;
            row.HeatTreatment = edit.HeatTreatment ?? string.Empty;
            rows.Add(row);
        }

        // 行集合本身没变（只是每行的目标路径变了）时连 Reset 都不必发，绑定跟着属性通知走。
        if (!reusedAll || rows.Count != Parts.Count)
            _rows.ReplaceAll(rows);

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
            "请填写图号前缀后再写入。",
            "正在写入",
            "属性整备写入",
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
        // 写入模式看 CanWrite：文件名已经对了但属性还没写，也是一次要干的活。
        // 洗图号仍看 CanRename——那条路只改名，没有名字可改就真的没事可做。
        if (stripBySpace ? !plan.CanRename : !plan.CanWrite)
            throw new InvalidOperationException(
                plan.BlockingIssues.Count > 0
                    ? string.Join("；", plan.BlockingIssues)
                    : emptyMessage);
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再写入。");

        _validateEnvironment(ConversionSourceFormat.SolidWorks);
        var renameCount = plan.Entries.Count(
            entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath));
        // 整份清单都要过去：Worker 自己挑出要改名的，属性则写在名字已经对的零件上。
        // 只送 pending 的话，第二次写入会因为「没有要改名的文件」而一个属性都写不进去。
        var entries = stripBySpace
            ? plan.Entries.ToArray()
            : plan.Entries.Select(AttachProperties).ToArray();
        var propertyCount = stripBySpace
            ? 0
            : entries.Count(entry => AssemblyRenamePlan.IsWritablePart(entry)
                                     && entry.Properties is { IsEmpty: false });
        // 按 Id 直接取行，不在几百行上逐条线性扫（那是 O(行 × 条目)，全在 UI 线程上）。
        foreach (var entry in entries)
        {
            if (AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
                continue;
            if (FindRow(entry.Id) is not { } row)
                continue;
            row.Status = "排队";
            row.Detail = Path.GetFileName(entry.TargetPath);
        }

        var workload = stripBySpace
            ? $"{renameCount} 个文件"
            : DescribeWriteWorkload(renameCount, propertyCount);
        QueueUiUpdate(() => StatusText = $"{progressPrefix} {workload}");
        var request = new AssemblyRenameRequest(
            Guid.NewGuid().ToString("N"),
            plan.SourceAssemblyPath,
            plan.DrawingPrefix,
            entries,
            ConversionSourceFormat.SolidWorks,
            stripBySpace,
            WriteProperties: !stripBySpace);
        var exitCode = await _renameWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);
        QueueUiUpdate(() =>
        {
            // 改完名之后立刻按落盘结果把索引挪到新文件名上，用户不必再手动导入一次。
            var reindexed = ReindexAfterRename(entries);
            StatusText = exitCode == 0
                ? $"{resultPrefix}完成：{workload}{reindexed}"
                : $"{resultPrefix}失败";
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanStrip));
            OnPropertyChanged(nameof(CanWrite));
        });
        _lastOperationSucceeded = exitCode == 0;
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(_firstWorkerFailure)
                    ? $"{resultPrefix}失败，详见 Vulcan 控制台。"
                    : $"{resultPrefix}失败：{_firstWorkerFailure}");
        }
    }

    private static string DescribeWriteWorkload(int renameCount, int propertyCount)
    {
        var parts = new List<string>(2);
        if (renameCount > 0)
            parts.Add($"{renameCount} 个文件改名");
        if (propertyCount > 0)
            parts.Add($"{propertyCount} 个零件写属性");
        return parts.Count == 0 ? "没有需要处理的文件" : string.Join("、", parts);
    }

    /// <summary>
    /// 给一个改名条目挂上要写的七个槽。装配体和未编号内部件原样返回，不带属性载荷——
    /// 判据与 Worker 侧一致，两边各判一次，界面漏判时 Worker 仍然不会去动装配体。
    /// </summary>
    private RenameEntry AttachProperties(RenameEntry entry)
    {
        if (!AssemblyRenamePlan.IsWritablePart(entry))
            return entry;

        var edit = _propertyEdits.TryGetValue(Path.GetFullPath(entry.SourcePath), out var saved)
            ? saved
            : default;
        return entry with
        {
            Properties = new PartPropertyWrite(
                entry.DrawingNumber,
                PartPropertyNames.MachinedCategory,
                _dateText,
                _designer,
                edit.Material ?? string.Empty,
                edit.SurfaceTreatment ?? string.Empty,
                edit.HeatTreatment ?? string.Empty,
                edit.MaterialDatabase ?? string.Empty,
                // 「名称」没有界面入口，跟着改名一起走：值就是文件名里图号之后的那一段。
                entry.PartName),
        };
    }

    /// <summary>
    /// 解析装配体时读回来的三个槽进账，作为表格的预填值。
    ///
    /// 覆盖既有记账是对的：解析就是一次显式的"重新去零件上认一遍"，此刻零件身上的值
    /// 才是事实。用户之后改的那几格只影响记账，不会因为改图号前缀重画表格就被顶回去——
    /// 那条路只重建计划，不重新解析。
    /// </summary>
    private void AdoptProbedProperties(AssemblyProbeResult result)
    {
        if (result.PartProperties is not { Count: > 0 } readings)
            return;
        foreach (var reading in readings)
        {
            if (!Path.IsPathFullyQualified(reading.SourcePath))
                continue;
            var key = Path.GetFullPath(reading.SourcePath);
            _propertyEdits[key] = new PartPropertyEdit(
                reading.Material,
                reading.MaterialDatabase,
                reading.SurfaceTreatment,
                reading.HeatTreatment);
        }
    }

    /// <summary>
    /// 改名收工之后，把探查结果、记账、改名计划和零件表整体挪到新文件名上。
    ///
    /// 没有这一步，改完名的下一秒整页就作废了：每条路径都指向不存在的文件，源装配的哈希
    /// 也因为引用更新而变了，用户只能重选一次装配体再解析一遍——而那一遍要再开一次
    /// SolidWorks、再走一遍整棵装配树，只为了换掉一批已经算得出来的名字。
    ///
    /// 挪完之后本轮并没有"结束"：文件名已经对了，用户可以接着改一格材料再写一次，
    /// 所以不再置 <c>_conversionCompleted</c>，写入与洗图号按钮继续按计划本身的判据来。
    ///
    /// 返回值是给状态栏的尾巴；返回空串表示这一轮没有需要挪的东西。
    /// </summary>
    private string ReindexAfterRename(IReadOnlyList<RenameEntry> entries)
    {
        _conversionCompleted = false;
        if (_probeResult is null)
            return string.Empty;

        var moved = RenameReindex.Moved(entries);
        var reindexed = RenameReindex.Remap(_probeResult, moved);
        if (moved.Count > 0)
        {
            var remapped = RenameReindex.RemapKeys(SnapshotPropertyEdits(), moved);
            _propertyEdits.Clear();
            foreach (var pair in remapped)
                _propertyEdits[pair.Key] = pair.Value;
            _probeResult = reindexed;
            _sourceAssemblyPath = reindexed.SourceAssemblyPath;
            UpdateAssemblyOutputPaths();
            NotifySourceChanged();
        }

        // 哈希无论如何都要重取：改名会让 ReplaceReferencedDocument 重写父装配，
        // 不更新的话下一次写入会被「源装配体在解析后发生变化」挡住，属性一个都写不进去。
        try
        {
            _sourceHashAfterProbe = ComputeSha256(reindexed.SourceAssemblyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 源装配此刻读不到（被 SolidWorks 占着、或改名失败留下了半截）。
            // 这时宁可让用户重新解析，也不能拿一个过期哈希放行下一次写入。
            _conversionCompleted = true;
            return "；源装配体暂时读不到，请重新解析后再写入";
        }

        _plan = AssemblyPlanner.Create(
            reindexed, customXtDirectory: null, customSolidWorksDirectory: null, ConversionSourceFormat.SolidWorks);
        AssemblyTree.Clear();
        AssemblyTree.Add(AssemblyTreeNode.Build(reindexed, _plan.Nodes));
        // 计划与零件行按新路径整份重画；行 Id 由源路径定死，所以改过名的行确实是新行。
        ApplyRenamePreview();
        return moved.Count == 0 ? string.Empty : $"；{moved.Count} 个文件的索引已更新到新名称";
    }

    /// <summary>记账的只读快照。搬键的时候不能一边读一边改同一个字典。</summary>
    private Dictionary<string, PartPropertyEdit> SnapshotPropertyEdits()
        => new(_propertyEdits, StringComparer.OrdinalIgnoreCase);

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
        // 零件上现有的材料 / 表面处理 / 热处理在这一步就进账，表格开出来显示的是零件的事实，
        // 而不是一片空白等着用户把已有的值再选一遍（选错一格就把正确的材质换掉了）。
        AdoptProbedProperties(result);
        var regeneratesExisting = RegeneratesExistingOutputs;
        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues.Select(issue => $"[{issue.ErrorClass}] {issue.Message}"));
        _rows.ReplaceAll(plan.Parts.Select(
            candidate => new ConversionFileRow(candidate, regeneratesExisting)));
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
