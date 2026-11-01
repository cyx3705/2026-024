using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed partial class AssemblyViewModel
{
    /// <summary>
    /// 用户在「名称」列里改过的名字，按源文件全路径记账。没记的以文件名里第一个空格
    /// 之后的那一段为准（见 <c>PropertyPrepPlanner</c>）。
    ///
    /// 住在这里而不是行上：改一次前缀整份计划重建，用户改过的名字不该跟着一起被冲掉。
    /// </summary>
    private readonly Dictionary<string, string> _nameEdits = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 识别特征 / 失败继续 / 重建装配关系那一排开关。属性整备与整体打包都不显示它们：
    /// 前者只改名字，后者一个模型都不改，摆着三个不会生效的开关是在骗用户。
    /// </summary>
    public bool ShowConversionOptions => !IsRenameMode && !IsPackMode;

    /// <summary>有文件要改名或要挪进「外购件」才能点。</summary>
    internal bool CanWrite => CanEdit && IsRenameMode
        && !_conversionCompleted && _renamePlan?.CanRename == true;

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
            if (!_renamePlan.CanRename)
                return "图号与名称都已与规则一致，无需改名。";
            if (_conversionCompleted)
                return "本轮改名已完成，请重新解析后再改名。";
            return StatusText;
        }
    }

    /// <summary>
    /// 图号前缀。
    ///
    /// **空串＝本轮不动图号**（V4.15）：每个文件保留原来的图号段，「图号」槽也不写；
    /// 只有用户改过的名称会落盘。填了前缀才按层级整表重新编号。
    /// V4.9～V4.14 曾把空串当成删图号，用户没填前缀就点改名，原图号全被顶掉。
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
    /// 只有这一份计划：前缀为空时它沿用每个文件原有的图号段（V4.15）。
    /// </summary>
    private void ApplyRenamePreview()
    {
        if (!IsRenameMode || _probeResult is null)
        {
            _renamePlan = null;
            OnPropertyChanged(nameof(CanWrite));
            return;
        }

        var plan = PropertyPrepPlanner.Create(_probeResult, _drawingPrefix, _nameEdits, _kindEdits);
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
        var rows = new List<ConversionFileRow>(
            plan.Entries.Count + plan.Unnumbered.Count + plan.ExcludedEntries.Count);
        var reusedAll = true;
        var excluded = plan.ExcludedEntries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in plan.Entries.Concat(plan.Unnumbered).Concat(plan.ExcludedEntries))
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
            row.CategoryText = KindCell(entry.SourcePath);

            if (excluded.Contains(entry.Id))
            {
                // V4.11：外购件与排除件不编号。子文件夹里的件哪怕改成机加件也编不了号——
                // 探查拿不到它们的装配层级，这时件别只影响打包，要说清楚。
                var kind = PartKinds.Resolve(_kindEdits, entry.SourcePath, _sourceAssemblyPath);
                if (!AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
                {
                    // V4.15：同级件改成外购件，改名时连同只属于它的内部件一起挪进「外购件」文件夹。
                    row.Status = "待移入";
                    row.Detail = $"外购件，改名时移入「{ConversionPathLayout.PurchasedPartsDirectoryName}」文件夹，保持原名";
                }
                else
                {
                    row.Status = "不编号";
                    row.Detail = kind == PackagePartCategory.Machined
                        ? "子文件夹里的件不参与编号，件别只影响打包"
                        : $"{PartKinds.Label(kind)}，保持原名";
                }
            }
            else if (!entry.AssignsDrawingNumber)
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
        var relocating = plan.Relocations.Count;
        var keeping = plan.DrawingPrefix.Length == 0;
        var head = keeping ? "未填图号前缀，保留原图号" : "图号规划完成";
        var tail = relocating > 0
            ? $"；{relocating} 个外购件文件待移入「{ConversionPathLayout.PurchasedPartsDirectoryName}」"
            : string.Empty;
        StatusText = plan.BlockingIssues.Count > 0
            ? $"图号规划有 {plan.BlockingIssues.Count} 个问题"
            : pending > 0
                ? $"{head}：{pending} 个文件待改名{tail}"
                : keeping
                    ? $"{head}，无需改名{tail}"
                    : $"图号与名称都已与规则一致，无需改名{tail}";
    }

    private async Task RenameCoreAsync(CancellationToken cancellationToken)
        => await RunRenamePlanAsync(
            _renamePlan ?? throw new InvalidOperationException("请先成功解析装配体。"),
            cancellationToken).ConfigureAwait(false);

    private async Task RunRenamePlanAsync(AssemblyRenamePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.CanRename)
        {
            throw new InvalidOperationException(
                plan.BlockingIssues.Count > 0
                    ? string.Join("；", plan.BlockingIssues)
                    : "图号与名称都已与规则一致，无需改名。");
        }

        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再改名。");

        _validateEnvironment(ConversionSourceFormat.SolidWorks);
        var renameCount = plan.Entries.Count(
            entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath));
        var relocateCount = plan.Relocations.Count;
        // 整份清单都要过去，Worker 自己挑出要改名的。V4.15：要挪进「外购件」的文件也在这一份里。
        var entries = plan.WorkerEntries;
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

        var workload = DescribeWriteWorkload(renameCount, relocateCount);
        QueueUiUpdate(() => StatusText = $"正在改名：{workload}");
        var request = new AssemblyRenameRequest(
            Guid.NewGuid().ToString("N"),
            plan.SourceAssemblyPath,
            plan.DrawingPrefix,
            entries,
            ConversionSourceFormat.SolidWorks);
        var exitCode = await _renameWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);
        _lastOperationSucceeded = exitCode == 0;
        if (exitCode != 0)
        {
            // 结论同步定下来，命令拿到的就是这一句，而不是队列里还没轮到的那一句。
            _lastResultText = string.IsNullOrEmpty(_firstWorkerFailure)
                ? "改名失败，详见 Vulcan 控制台。"
                : $"改名失败：{_firstWorkerFailure}";
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
        _lastResultText = $"改名完成：{workload}";
        QueueUiUpdate(() =>
        {
            // 改完名之后立刻按落盘结果把索引挪到新文件名上，用户不必再手动导入一次。
            var reindexed = ReindexAfterRename(entries);
            _lastResultText = $"改名完成：{workload}{reindexed}";
            StatusText = _lastResultText;
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanWrite));
        });
    }

    private static string DescribeWriteWorkload(int renameCount, int relocateCount)
    {
        var parts = new List<string>(2);
        if (renameCount > 0)
            parts.Add($"{renameCount} 个文件改名");
        if (relocateCount > 0)
            parts.Add($"{relocateCount} 个外购件移入「{ConversionPathLayout.PurchasedPartsDirectoryName}」");
        return parts.Count == 0 ? "没有需要处理的文件" : string.Join("、", parts);
    }

    /// <summary>
    /// 改名收工之后，把探查结果、记账、改名计划和零件表整体挪到新文件名上。
    ///
    /// 没有这一步，改完名的下一秒整页就作废了：每条路径都指向不存在的文件，源装配的哈希
    /// 也因为引用更新而变了，用户只能重选一次装配体再解析一遍——而那一遍要再开一次
    /// SolidWorks、再走一遍整棵装配树，只为了换掉一批已经算得出来的名字。
    ///
    /// 挪完之后本轮并没有「结束」：文件名已经对了，用户可以接着改一格名称再改一次，
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
            // 名称记账按源路径记，不搬的话改完名再改一次名称就会漏掉用户填过的那一份。
            var remappedNames = RenameReindex.RemapKeys(
                new Dictionary<string, string>(_nameEdits, StringComparer.OrdinalIgnoreCase), moved);
            _nameEdits.Clear();
            foreach (var pair in remappedNames)
                _nameEdits[pair.Key] = pair.Value;
            RemapKindEdits(moved);
            _probeResult = reindexed;
            _sourceAssemblyPath = reindexed.SourceAssemblyPath;
            UpdateAssemblyOutputPaths();
            NotifySourceChanged();
        }

        // 哈希无论如何都要重取：改名会让 ReplaceReferencedDocument 重写父装配，
        // 不更新的话下一次写入会被「源装配体在解析后发生变化」挡住。
        try
        {
            _sourceHashAfterProbe = ComputeSha256(reindexed.SourceAssemblyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 源装配此刻读不到（被 SolidWorks 占着、或改名失败留下了半截）。
            // 这时宁可让用户重新解析，也不能拿一个过期哈希放行下一次写入。
            _conversionCompleted = true;
            return "；源装配体暂时读不到，请重新解析后再改名";
        }

        _plan = AssemblyPlanner.Create(
            reindexed, customXtDirectory: null, customSolidWorksDirectory: null, ConversionSourceFormat.SolidWorks);
        AssemblyTree.Clear();
        AssemblyTree.Add(AssemblyTreeNode.Build(reindexed, _plan.Nodes));
        // 计划与零件行按新路径整份重画；行 Id 由源路径定死，所以改过名的行确实是新行。
        ApplyRenamePreview();
        return moved.Count == 0 ? string.Empty : $"；{moved.Count} 个文件的索引已更新到新名称";
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
        ApplyPackagePreview();
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
        // 用户填过的前缀不该由"我没看懂"来清掉。
        if (inferred.Length == 0 || string.Equals(_drawingPrefix, inferred, StringComparison.Ordinal))
            return;
        // 直接落字段：这条路后面紧跟着 ApplyRenamePreview，走属性会让计划白建一遍。
        _drawingPrefix = inferred;
        OnPropertyChanged(nameof(DrawingPrefix));
    }
}
