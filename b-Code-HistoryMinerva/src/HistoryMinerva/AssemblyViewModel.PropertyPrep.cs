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

    /// <summary>
    /// 用户在「名称」列里改过的名字，按源文件全路径记账。没记的以文件名里第一个空格
    /// 之后的那一段为准（见 <c>PropertyPrepPlanner</c>）。
    ///
    /// 与三个属性槽同一个理由住在这里而不是行上：改一次前缀整份计划重建，
    /// 用户改过的名字不该跟着前缀一起被冲掉。
    /// </summary>
    private readonly Dictionary<string, string> _nameEdits = new(StringComparer.OrdinalIgnoreCase);

    private string _designer = string.Empty;
    private string _dateText = string.Empty;

    /// <summary>
    /// 本轮操作的最终结论，由操作自己**同步**写下。
    ///
    /// 不能让命令去读 <see cref="StatusText"/>：那一句是排进 UI 队列的，而命令在操作的
    /// Task 完成时就返回了，两者之间没有先后保证。V4.8 现场看到的
    /// 「✓ 正在写入：16 个文件改名、15 个零件写属性。」正是这么来的——结果行里写着一句
    /// 进行时，用户没法从控制台确认这一轮到底成没成。
    /// </summary>
    private string _lastResultText = string.Empty;

    /// <summary>命令回报用的结论文本。操作没留下结论时退回 <see cref="StatusText"/>。</summary>
    internal string ResultText
        => string.IsNullOrEmpty(_lastResultText) ? StatusText : _lastResultText;

    public bool IsRenameMode
        => SelectedMappingContent.Kind == MappingContent.SolidWorksAssemblyPropertyPrep;

    public bool ShowConversionOptions => !IsRenameMode;

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
            if (_renamePlan is null)
                return "改名计划还没建起来，请重新解析装配体。";
            if (_renamePlan.BlockingIssues.Count > 0)
                return string.Join("；", _renamePlan.BlockingIssues);
            if (!_renamePlan.CanWrite)
                return "图号与名称都已与规则一致，也没有可写属性的零件。";
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
    /// 图号前缀。
    ///
    /// **空串是正常状态，不是「还没填」**：它表示这一轮把图号改成空，也就是删图号
    /// （DEC-057）。因此它与材料那三栏同构——底下一个框，改它就是整表一起改，
    /// 只不过它改的是图号而不是某个属性槽。
    /// </summary>
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
            OnPropertyChanged(nameof(CanWrite));
        }
    }

    // ---------------------------------------------------------------- 三个属性槽的统一态

    /// <summary>
    /// 这一列现在是不是一个统一的值。返回 <c>null</c> 表示**不统一**。
    ///
    /// 只看会写属性的行：装配体和未编号内部件那几行的三格永远是空的，
    /// 把它们算进来的话任何一张表都是「不统一」。
    /// </summary>
    internal string? UniformProperty(PartPropertyField field)
    {
        string? first = null;
        var seen = false;
        foreach (var row in Parts)
        {
            if (!row.WritesProperties)
                continue;
            var value = ReadPartProperty(row, field);
            if (!seen)
            {
                first = value;
                seen = true;
                continue;
            }

            if (!string.Equals(first, value, StringComparison.Ordinal))
                return null;
        }

        return seen ? first : string.Empty;
    }

    /// <summary>
    /// 底下那个选项框此刻该显示什么（V4.9 状态机）。
    ///
    /// 统一且非空 → 就显示那个值；不统一 → <see cref="PartPropertyNames.NoWriteOption"/>；
    /// 统一为空（谁都没填）→ 同样是它。也就是说「（不写）」在**框**上读作
    /// 「这一列没有一个共同的值」，而不是「这一列不写」——不写是**单元格**上的事。
    ///
    /// 材料还要多一步：行里存的是材质名，框里显示的是候选标签（重名材质带库名以示区分）。
    /// </summary>
    internal string PropertyBoxText(PartPropertyField field)
    {
        var uniform = UniformProperty(field);
        if (string.IsNullOrEmpty(uniform))
            return PartPropertyNames.NoWriteOption;
        return field == PartPropertyField.Material
            ? MaterialLabel(uniform, UniformMaterialDatabase())
            : uniform;
    }

    /// <summary>统一材质对应的材料库；不统一或没有时是空串。</summary>
    private string UniformMaterialDatabase()
    {
        string? first = null;
        foreach (var row in Parts)
        {
            if (!row.WritesProperties)
                continue;
            var database = _propertyEdits.TryGetValue(Path.GetFullPath(row.SourcePath), out var edit)
                ? edit.MaterialDatabase ?? string.Empty
                : string.Empty;
            if (first is null)
            {
                first = database;
                continue;
            }

            if (!string.Equals(first, database, StringComparison.Ordinal))
                return string.Empty;
        }

        return first ?? string.Empty;
    }

    /// <summary>
    /// 材质名 → 下拉里显示的标签。收藏里找不到就原样返回材质名：
    /// 零件身上本来就可能是一种用户没收藏过的材质，那时显示它的真名比显示「（不写）」诚实。
    /// </summary>
    private static string MaterialLabel(string materialName, string database)
    {
        var favorites = SolidWorksPropertyOptions.Materials();
        var exact = favorites.FirstOrDefault(item =>
            string.Equals(item.Name, materialName, StringComparison.Ordinal)
            && string.Equals(item.Database, database, StringComparison.Ordinal));
        if (exact.Label is { Length: > 0 })
            return exact.Label;
        var byName = favorites.FirstOrDefault(item =>
            string.Equals(item.Name, materialName, StringComparison.Ordinal));
        return byName.Label is { Length: > 0 } ? byName.Label : materialName;
    }

    /// <summary>
    /// 表里出现过、但用户没收藏的材质名。
    ///
    /// 候选表必须把它们带上，否则会出现这样一格：零件身上是「6061 合金」，表里也这么显示，
    /// 底下的框却选不中它——框里没有这一项，于是退回第一项，看起来像是材料被悄悄改成了不写。
    /// </summary>
    internal IReadOnlyList<string> ExtraMaterialNames()
    {
        var favorites = SolidWorksPropertyOptions.Materials();
        var extra = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Parts)
        {
            if (!row.WritesProperties || row.Material.Length == 0)
                continue;
            if (favorites.Any(item => string.Equals(item.Name, row.Material, StringComparison.Ordinal)))
                continue;
            if (seen.Add(row.Material))
                extra.Add(row.Material);
        }

        return extra;
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

    /// <summary>读回某一格的当前值，供弹选择框时预填。</summary>
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

    // ---------------------------------------------------------------- 名称列

    /// <summary>这一行会不会被改名。未编号的内部件保持原名，「名称」列也就不该能点。</summary>
    public bool RenamesFile(string rowId)
    {
        var row = FindRow(rowId);
        return row is { RenamesFile: true };
    }

    /// <summary>读回「名称」列当前的值，供弹输入框时预填。</summary>
    public string GetPartName(string rowId) => FindRow(rowId)?.PartName ?? string.Empty;

    /// <summary>
    /// 改一行的名称。
    ///
    /// 空名字和带文件名非法字符的名字都不收：前者会让文件名只剩一个图号，
    /// 在明细表里认不出是哪个零件；后者会让改名当场失败，而那时用户已经离开这一格了。
    /// </summary>
    public bool SetPartName(string rowId, string name, out string error)
    {
        error = string.Empty;
        var row = FindRow(rowId);
        if (row is not { RenamesFile: true })
        {
            error = "这一行不改名（标准件/外购件内部），名称未修改。";
            return false;
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "名称不能为空——文件名不能只剩一个图号。";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "名称里有文件名不允许的字符，改名会失败。";
            return false;
        }

        if (string.Equals(row.PartName, trimmed, StringComparison.Ordinal))
            return true;

        _nameEdits[Path.GetFullPath(row.SourcePath)] = trimmed;
        // 名称同时决定目标文件名，所以要整份重建计划：改完这一格，那一行的
        // 「图号 名称.ext」当场就得变，重名检查也要按新名字再跑一遍。
        ApplyRenamePreview();
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanWrite));
        return true;
    }

    /// <summary>
    /// 三个可点列现在能不能落到零件上。改名计划还没建起来（还没解析装配体）时是 false，
    /// 命令据此给出「请先解析装配体」而不是空口报「刷了 0 个零件」。
    ///
    /// V4.9 起不再要求前缀非空：前缀为空是删图号，那一轮照样写属性。
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

    /// <summary>
    /// 按当前前缀与名称记账重建改名计划，并把它画进零件表。
    ///
    /// V4.9 只有这一份计划：前缀为空时它算出来的目标文件名就是「只有名称」，
    /// 那正是删图号要的结果（DEC-057）。
    /// </summary>
    private void ApplyRenamePreview()
    {
        if (!IsRenameMode || _probeResult is null)
        {
            _renamePlan = null;
            OnPropertyChanged(nameof(CanWrite));
            return;
        }

        var plan = PropertyPrepPlanner.Create(_probeResult, _drawingPrefix, _nameEdits);
        _renamePlan = plan;
        RenderPropertyPrepRows(plan);
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
    private void RenderPropertyPrepRows(AssemblyRenamePlan plan)
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

            // 顺序也要一致才算复用：只比个数会把表留在旧顺序上。
            if (reusedAll && (rows.Count >= Parts.Count || !ReferenceEquals(Parts[rows.Count], row)))
                reusedAll = false;

            // 图号与名称是两列，各自显示各自的那一段；文件名永远是这两段拼出来的。
            row.DrawingText = entry.DrawingNumber;
            row.PartName = entry.PartName;
            row.RenamesFile = entry.AssignsDrawingNumber;
            // 判据与 Worker 侧 AssemblyRenamePlan.IsWritablePart 一致，两边各判一次。
            // 界面上让用户往装配体行里填材料，是在骗他——那一格永远不会落盘。
            row.WritesProperties = AssemblyRenamePlan.IsWritablePart(entry);

            if (!entry.AssignsDrawingNumber)
            {
                row.Status = "无图号";
                row.Detail = "标准件/外购件内部，保持原名";
            }
            else if (AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
            {
                row.Status = "已符合";
                row.Detail = Path.GetFileName(entry.TargetPath);
            }
            else
            {
                row.Status = ConversionFileRow.ReadyStatus;
                row.Detail = Path.GetFileName(entry.TargetPath);
            }

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
        var removing = plan.DrawingPrefix.Length == 0;
        StatusText = plan.BlockingIssues.Count > 0
            ? $"图号规划有 {plan.BlockingIssues.Count} 个问题"
            : pending > 0
                ? removing
                    ? $"删图号规划完成：{pending} 个文件待改名"
                    : $"图号规划完成：{pending} 个文件待改名"
                : removing
                    ? "这些文件已经没有图号，无需改名"
                    : "图号与名称都已与规则一致，无需改名";
    }

    private async Task RenameCoreAsync(CancellationToken cancellationToken)
        => await RunRenamePlanAsync(
            _renamePlan ?? throw new InvalidOperationException("请先成功解析装配体。"),
            cancellationToken).ConfigureAwait(false);

    private async Task RunRenamePlanAsync(AssemblyRenamePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 看 CanWrite 而不是 CanRename：文件名已经对了但属性还没写，也是一次要干的活。
        if (!plan.CanWrite)
        {
            throw new InvalidOperationException(
                plan.BlockingIssues.Count > 0
                    ? string.Join("；", plan.BlockingIssues)
                    : "图号与名称都已与规则一致，也没有可写属性的零件。");
        }

        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再写入。");

        _validateEnvironment(ConversionSourceFormat.SolidWorks);
        var renameCount = plan.Entries.Count(
            entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath));
        // 整份清单都要过去：Worker 自己挑出要改名的，属性则写在名字已经对的零件上。
        // 只送 pending 的话，第二次写入会因为「没有要改名的文件」而一个属性都写不进去。
        var entries = plan.Entries.Select(AttachProperties).ToArray();
        var propertyCount = entries.Count(entry => AssemblyRenamePlan.IsWritablePart(entry)
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

        var workload = DescribeWriteWorkload(renameCount, propertyCount, plan.DrawingPrefix.Length == 0);
        QueueUiUpdate(() => StatusText = $"正在写入：{workload}");
        var request = new AssemblyRenameRequest(
            Guid.NewGuid().ToString("N"),
            plan.SourceAssemblyPath,
            plan.DrawingPrefix,
            entries,
            ConversionSourceFormat.SolidWorks,
            WriteProperties: true);
        var exitCode = await _renameWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);
        _lastOperationSucceeded = exitCode == 0;
        if (exitCode != 0)
        {
            // 结论同步定下来，命令拿到的就是这一句，而不是队列里还没轮到的那一句。
            _lastResultText = string.IsNullOrEmpty(_firstWorkerFailure)
                ? "写入失败，详见 Vulcan 控制台。"
                : $"写入失败：{_firstWorkerFailure}";
            var failureText = _lastResultText;
            QueueUiUpdate(() =>
            {
                StatusText = failureText;
                OnPropertyChanged(nameof(CanConvert));
                OnPropertyChanged(nameof(CanWrite));
            });
            throw new InvalidOperationException(failureText);
        }

        // 结论先按已知的工作量同步定下来；索引重建要在 UI 线程上做，做完再把
        // 「索引已更新」那半句补进去。命令读到的至少已经是一句完成时。
        _lastResultText = $"写入完成：{workload}";
        QueueUiUpdate(() =>
        {
            // 改完名之后立刻按落盘结果把索引挪到新文件名上，用户不必再手动导入一次。
            var reindexed = ReindexAfterRename(entries);
            _lastResultText = $"写入完成：{workload}{reindexed}";
            StatusText = _lastResultText;
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanWrite));
        });
    }

    private static string DescribeWriteWorkload(int renameCount, int propertyCount, bool removingNumbers)
    {
        var parts = new List<string>(2);
        if (renameCount > 0)
            parts.Add(removingNumbers ? $"{renameCount} 个文件删图号" : $"{renameCount} 个文件改名");
        if (propertyCount > 0)
            parts.Add($"{propertyCount} 个零件写属性");
        return parts.Count == 0 ? "没有需要处理的文件" : string.Join("、", parts);
    }

    /// <summary>
    /// 给一个改名条目挂上要写的八个槽。装配体和未编号内部件原样返回，不带属性载荷——
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
                // 「名称」跟着改名一起走：值就是文件名里图号之后的那一段。
                entry.PartName),
        };
    }

    /// <summary>
    /// 解析装配体时读回来的三个槽进账，作为表格的预填值。
    ///
    /// 覆盖既有记账是对的：解析就是一次显式的「重新去零件上认一遍」，此刻零件身上的值
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
    /// 挪完之后本轮并没有「结束」：文件名已经对了，用户可以接着改一格材料再写一次，
    /// 所以不再置 <c>_conversionCompleted</c>，写入按钮继续按计划本身的判据来。
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
            var remappedProperties = RenameReindex.RemapKeys(SnapshotPropertyEdits(), moved);
            _propertyEdits.Clear();
            foreach (var pair in remappedProperties)
                _propertyEdits[pair.Key] = pair.Value;
            // 名称记账同样按源路径记，不搬的话改完名再改一次名称就会漏掉用户填过的那一份。
            var remappedNames = RenameReindex.RemapKeys(
                new Dictionary<string, string>(_nameEdits, StringComparer.OrdinalIgnoreCase), moved);
            _nameEdits.Clear();
            foreach (var pair in remappedNames)
                _nameEdits[pair.Key] = pair.Value;
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
        AdoptProbedPrefix(result);
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
        _lastOperationSucceeded = plan.CanConvert;
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanRebuildMates));
        OnPropertyChanged(nameof(RebuildMatesHint));
    }

    /// <summary>
    /// 解析装配体时按根装配的文件名把图号前缀认出来，填进「图号前缀」框（V4.9）。
    ///
    /// 为什么在这里认而不是让用户自己填：用户拿到的装配十有八九已经编过号了，
    /// 他要做的是**接着这一套号往下改**，而不是从头想一个前缀。认不出来（根装配名里
    /// 没有空格）时留空——那确实是一个还没编号的装配。
    ///
    /// 名称记账在这一步清掉：解析是一次显式的「重新去磁盘上认一遍」，此刻文件名才是事实。
    /// 留着上一轮改过的名字，用户会看到一个自己这一轮从没填过的名称。
    /// </summary>
    private void AdoptProbedPrefix(AssemblyProbeResult result)
    {
        _nameEdits.Clear();
        var inferred = DrawingNumber.InferPrefix(Path.GetFileName(result.SourceAssemblyPath));
        // 认不出来就**不动用户填的那个**。认不出只说明这个装配还没编过号，
        // 而清空是一个有后果的动作（前缀为空＝删图号）——不能由"我没看懂"来触发。
        if (inferred.Length == 0 || string.Equals(_drawingPrefix, inferred, StringComparison.Ordinal))
            return;
        // 直接落字段：这条路后面紧跟着 ApplyRenamePreview，走属性会让计划白建一遍。
        _drawingPrefix = inferred;
        OnPropertyChanged(nameof(DrawingPrefix));
    }
}
