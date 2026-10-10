using System.IO;
using HistoryMinerva.Bom;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// 整体打包。选一个总装配体，一次交出一个与它同级的 <c>&lt;前缀&gt; 零件采购/</c> 文件夹（V4.11）：
/// 两张 BOM 与同名截图在最外层，机加件的 DWG / PDF / STEP 三件套在 <c>图纸/</c> 里。
///
/// 流程与另外三种转换内容同构：先「解析装配体」（复用同一条只读探查），再点「打包」。
/// 不做成一个按钮，是因为解析要打开 SolidWorks 读整棵装配树，几百个零件要几分钟——
/// 那几分钟里用户看着表一行行长出来，才知道识别到的是不是他要的那一批件；
/// 件别不对的，在这张表里点一下改掉再打包（V4.11）。
///
/// V4.16：打包拆成六步（两张清单、AI 查品牌、STEP / DWG / PDF），页面底下各一个开关，见 <see cref="PackageSteps"/>。
/// </summary>
public sealed partial class AssemblyViewModel
{
    private PackagePlan? _packagePlan;

    public bool IsPackMode
        => SelectedMappingContent.Kind == MappingContent.SolidWorksAssemblyPackage;

    /// <summary>打包不需要特征识别、失败继续或重建装配关系——它一个模型都不改。</summary>
    internal bool CanPack => CanEdit && IsPackMode
        && !_conversionCompleted && _packagePlan?.CanPack == true && PackageSteps.AnyStep;

    internal string PackBlockedReason
    {
        get
        {
            if (!IsPackMode)
                return StatusText;
            if (!CanEdit)
                return "正在执行操作。";
            if (_probeResult is null)
                return "请先解析装配体。";
            if (_packagePlan is null)
                return "打包计划还没建起来，请重新解析装配体。";
            if (_packagePlan.BlockingIssues.Count > 0)
                return string.Join("；", _packagePlan.BlockingIssues);
            if (_conversionCompleted)
                return "本轮打包已完成，请重新解析后再打包。";
            if (!PackageSteps.AnyStep)
                return "打包选项全关了：至少打开一步（清单、AI 查品牌、STEP、DWG、PDF）再打包。";
            return StatusText;
        }
    }

    /// <summary>解析完之后建计划、画表。非打包模式下清掉计划，不留一份过期的。</summary>
    private void ApplyPackagePreview()
    {
        if (!IsPackMode || _probeResult is null)
        {
            _packagePlan = null;
            OnPropertyChanged(nameof(CanConvert));
            return;
        }

        var plan = PackagePlanner.Create(_probeResult, _kindEdits);
        _packagePlan = plan;
        RenderPackageRows(plan);
        OnPropertyChanged(nameof(CanConvert));
    }

    /// <summary>
    /// 把打包计划画成零件表：机加件、外购件、排除件，以及自制子装配体（V4.11，供改件别）。
    ///
    /// 行按 <see cref="PackagePartEntry.Id"/> 原地复用，理由与属性整备那张表相同：
    /// id 由源文件全路径定死，重新解析或改件别后同一个零件仍是同一行。
    /// </summary>
    private void RenderPackageRows(PackagePlan plan)
    {
        var rows = new List<ConversionFileRow>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            var candidate = new ScanCandidate(entry.SourcePath, entry.SourcePath, entry.SourcePath, false);
            var row = FindRow(entry.Id) ?? new ConversionFileRow(candidate, id: entry.Id);
            row.Rebind(candidate);
            row.DrawingText = entry.Category == PackagePartCategory.Machined
                ? entry.DrawingNumber
                : entry.Specification;
            row.PartName = DisplayName(entry);
            row.CategoryText = PartKinds.Cell(entry.Category);
            row.BrandText = CachedBrandText(entry);
            row.RenamesFile = false;
            row.WritesProperties = false;
            (row.QuantityText, row.DrawingStateText, row.Status, row.Detail) = DescribePackageRow(entry);
            rows.Add(row);
        }

        _rows.ReplaceAll(rows);

        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues);
        WarningSummary = string.Join("；", plan.Warnings.Concat(
            string.IsNullOrWhiteSpace(issueText) ? [] : new[] { issueText }));
        StatusText = plan.BlockingIssues.Count > 0
            ? $"打包规划有 {plan.BlockingIssues.Count} 个问题：{issueText}"
            : $"打包规划完成：{plan.Machined.Count} 个机加件、{plan.Purchased.Count} 个外购件、"
                + $"{plan.DrawingTargets.Count} 张工程图";
    }

    /// <summary>数量 / 工程图 / 状态 / 结果四格。排除件与自制组件不交付，四格说清楚它们为什么不在包里。</summary>
    private static (string Quantity, string Drawing, string Status, string Detail) DescribePackageRow(
        PackagePartEntry entry)
    {
        var quantity = entry.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return entry switch
        {
            { Category: PackagePartCategory.Reference } =>
                (string.Empty, string.Empty, "不打包", "排除，不进清单、不导出"),
            { Category: PackagePartCategory.Machined, IsAssembly: true } =>
                (quantity, string.Empty, "不打包", "自制组件，里面的件各自进清单"),
            { Category: PackagePartCategory.Purchased } =>
                (quantity, string.Empty, ConversionFileRow.ReadyStatus, "外购件清单"),
            { HasDrawing: true } =>
                (quantity, "有", ConversionFileRow.ReadyStatus, Path.GetFileName(entry.DrawingPath!)),
            _ => (quantity, "无", ConversionFileRow.ReadyStatus, "没有同名工程图，只导 STEP"),
        };
    }

    private async Task PackCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _packagePlan ?? throw new InvalidOperationException("请先成功解析装配体。");
        if (!plan.CanPack)
            throw new InvalidOperationException(PackBlockedReason);
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再打包。");

        var steps = PackageSteps;
        if (!steps.AnyStep)
            throw new InvalidOperationException(PackBlockedReason);
        CreatePackageDirectories(plan.Directories, steps);

        // V4.10.4：品牌在 BOM 之前查，外购件清单的「备注[参考供应商]」要带着它出厂。
        // 查不到、查询失败都只让那一格写 N/A，不拦打包（见 PurchasedBrandLookup）。
        var brands = await LookupPurchasedBrandsAsync(plan, cancellationToken).ConfigureAwait(false);

        // BOM 先于 CAD 导出写。它不需要 SolidWorks，也就没有理由陪着 CAD 导出一起失败——
        // 现场最常见的一幕正是「SolidWorks 起不来」，而采购要的清单本来就已经算好了。
        var boms = WritePackageBoms(plan, brands, steps);
        if (boms.Length > 0)
        {
            QueueUiUpdate(() => StatusText = $"BOM 已生成：{boms}");
            _operationProgress?.Report($"BOM 与截图已生成：{boms}");
        }

        var jobs = BuildPackageJobs(plan, steps);
        var packageName = Path.GetFileName(plan.Directories.PackageDirectory);
        var bomText = boms.Length > 0 ? boms : "未生成清单";
        if (jobs.Count == 0)
        {
            var reason = steps.AnyCadExport ? "没有要导出的机加件" : "STEP / DWG / PDF 均未开启";
            _lastResultText = $"打包完成：{packageName}（{bomText}），{reason}" + brands.Describe() + "。";
            _lastOperationSucceeded = true;
            QueueUiUpdate(() =>
            {
                _conversionCompleted = true;
                StatusText = _lastResultText;
                OnPropertyChanged(nameof(CanConvert));
            });
            return;
        }

        _validateEnvironment(ConversionSourceFormat.SolidWorks);
        var request = new PackageRequest(
            Guid.NewGuid().ToString("N"),
            plan.SourceAssemblyPath,
            jobs,
            Overwrite: true);

        var exported = jobs.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Parts)
        {
            if (!exported.Contains(row.Id))
                continue;
            row.Status = "排队";
            row.Detail = string.Empty;
        }

        var exportText = DescribeExports(jobs);
        QueueUiUpdate(() => StatusText = $"正在导出 {exportText}");
        var exitCode = await _packWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);

        _lastOperationSucceeded = exitCode == 0;
        _lastResultText = exitCode == 0
            ? $"打包完成：{plan.Directories.PackageDirectory} 已就绪"
                + $"（{bomText}；图纸 {exportText}）"
                + brands.Describe()
            : $"打包结束，存在导出失败项：{(boms.Length > 0 ? "BOM 已生成，" : string.Empty)}失败的零件或图纸见上方逐条报告"
                + (string.IsNullOrEmpty(_firstWorkerFailure) ? string.Empty : "；首个原因：" + _firstWorkerFailure)
                + brands.Describe();
        QueueUiUpdate(() =>
        {
            _conversionCompleted = true;
            foreach (var row in Parts)
            {
                if (exported.Contains(row.Id) && row.Status is not ("失败" or "已取消"))
                    row.Status = "完成";
            }

            StatusText = _lastResultText;
            OnPropertyChanged(nameof(CanConvert));
        });
    }

    /// <summary>
    /// 写两张 BOM 及同名截图。**空的那张不写**（用户确认）：一台没有外购件的设备，
    /// 包里就只有机加件清单——与现场手工整理的包一致。
    ///
    /// 上一轮留下的同名清单要删掉：这一轮件别改过、外购件清空了，旧的外购件清单还躺在包里，
    /// 采购照着它下单就是买错东西。
    ///
    /// V4.16：开关关着的那张清单不写也不删——用户关掉它，就是这一轮不碰它。
    /// </summary>
    /// <returns>给状态栏和结论用的一句描述；两张都没写时为空串。</returns>
    private static string WritePackageBoms(PackagePlan plan, PurchasedBrandSummary brands, PackageSteps steps)
    {
        var parts = new List<string>(2);
        var directory = plan.Directories.PackageDirectory;
        var machinedPath = Path.Combine(directory, plan.MachinedBomFileName);
        var purchasedPath = Path.Combine(directory, plan.PurchasedBomFileName);

        if (!steps.MachinedBom)
        {
        }
        else if (plan.Machined.Count > 0)
        {
            var count = BomWorkbookWriter.WriteMachined(plan, machinedPath);
            BomSheetImageRenderer.Render(machinedPath, Path.Combine(directory, plan.MachinedBomImageFileName));
            parts.Add($"{plan.MachinedBomFileName}（{count} 行）");
        }
        else
        {
            DeleteStale(machinedPath, Path.Combine(directory, plan.MachinedBomImageFileName));
        }

        if (!steps.PurchasedBom)
        {
        }
        else if (plan.Purchased.Count > 0)
        {
            var count = BomWorkbookWriter.WritePurchased(plan, purchasedPath, brands.ById, brands.NamesById);
            BomSheetImageRenderer.Render(purchasedPath, Path.Combine(directory, plan.PurchasedBomImageFileName));
            parts.Add($"{plan.PurchasedBomFileName}（{count} 行）");
        }
        else
        {
            DeleteStale(purchasedPath, Path.Combine(directory, plan.PurchasedBomImageFileName));
        }

        return string.Join("、", parts);
    }

    private static void DeleteStale(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 机加件一个 STEP，有图纸的机加件各一个 DWG 与一个 PDF，三件套都放进 <c>图纸/</c>。
    ///
    /// 三个作业共用同一个 <see cref="PackagePartEntry.Id"/>：表里它们本来就是同一行，
    /// 用三个 id 只会让进度事件找不到行。产物路径按文件主名放，
    /// 重名由 <c>WorkerRequestValidator</c> 拦下——同名零件互相覆盖会交付一个缺件的包。
    /// V4.16：三种产物各看各的开关。
    /// </summary>
    private static IReadOnlyList<PackageJob> BuildPackageJobs(PackagePlan plan, PackageSteps steps)
    {
        var drawings = plan.Directories.DrawingDirectory;
        var jobs = new List<PackageJob>(plan.StepTargets.Count + (plan.DrawingTargets.Count * 2));
        foreach (var entry in steps.Step ? plan.StepTargets : [])
        {
            jobs.Add(new PackageJob(
                entry.Id,
                entry.SourcePath,
                OutputPath(drawings, entry.SourcePath, ConversionPathLayout.StepExtension),
                PackageArtifact.Step));
        }

        foreach (var entry in plan.DrawingTargets)
        {
            var drawingPath = entry.DrawingPath!;
            if (steps.Dwg)
            {
                jobs.Add(new PackageJob(
                    entry.Id,
                    drawingPath,
                    OutputPath(drawings, drawingPath, ConversionPathLayout.DwgExtension),
                    PackageArtifact.Dwg));
            }

            if (steps.Pdf)
            {
                jobs.Add(new PackageJob(
                    entry.Id,
                    drawingPath,
                    OutputPath(drawings, drawingPath, ConversionPathLayout.PdfExtension),
                    PackageArtifact.Pdf));
            }
        }

        return jobs;
    }

    /// <summary>「3 个 STEP、2 个 DWG、2 个 PDF」：只列这一轮真的要导的。</summary>
    private static string DescribeExports(IReadOnlyList<PackageJob> jobs)
        => string.Join("、", new[] { PackageArtifact.Step, PackageArtifact.Dwg, PackageArtifact.Pdf }
            .Select(artifact => (artifact, count: jobs.Count(job => job.Artifact == artifact)))
            .Where(item => item.count > 0)
            .Select(item => $"{item.count} 个 {item.artifact.ToString().ToUpperInvariant()}"));

    private static string OutputPath(string directory, string sourcePath, string extension)
        => Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + extension);

    /// <summary>
    /// 打包目录与 <c>图纸/</c> 一次建齐，**哪怕本轮没有机加件**：
    /// 空的图纸目录说的是「这一批没有要加工的件」，缺一个目录说的是「打包是不是没跑完」。
    /// V4.16：STEP / DWG / PDF 都关着时不建 <c>图纸/</c>——这一轮本来就不出图。
    /// </summary>
    private static void CreatePackageDirectories(PackageOutputDirectories directories, PackageSteps steps)
    {
        var targets = steps.AnyCadExport
            ? new[] { directories.PackageDirectory, directories.DrawingDirectory }
            : new[] { directories.PackageDirectory };
        foreach (var directory in targets)
        {
            if (File.Exists(directory))
                throw new IOException($"打包目录被同名文件占用：{directory}");
            Directory.CreateDirectory(directory);
        }
    }
}
