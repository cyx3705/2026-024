using System.IO;
using HistoryMinerva.Bom;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.10 整体打包。选一个总装配体，一次交出四个目录：
/// <c>STP/</c>（机加件 STEP）、<c>DWG/</c> 与 <c>PDF/</c>（同名工程图）、<c>BOM/</c>（两张清单）。
///
/// 流程与另外三种转换内容同构：先「解析装配体」（复用同一条只读探查），再点「打包」。
/// 不做成一个按钮，是因为解析要打开 SolidWorks 读整棵装配树，几百个零件要几分钟——
/// 那几分钟里用户看着表一行行长出来，才知道识别到的是不是他要的那一批件。
/// </summary>
public sealed partial class AssemblyViewModel
{
    private PackagePlan? _packagePlan;

    public bool IsPackMode
        => SelectedMappingContent.Kind == MappingContent.SolidWorksAssemblyPackage;

    /// <summary>打包不需要特征识别、失败继续或重建装配关系——它一个模型都不改。</summary>
    internal bool CanPack => CanEdit && IsPackMode
        && !_conversionCompleted && _packagePlan?.CanPack == true;

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

        var plan = PackagePlanner.Create(_probeResult);
        _packagePlan = plan;
        RenderPackageRows(plan);
        OnPropertyChanged(nameof(CanConvert));
    }

    /// <summary>
    /// 把打包计划画成零件表。**只有零件**——子装配体不进表，也不进 BOM、不导 STEP。
    ///
    /// 行按 <see cref="PackagePartEntry.Id"/> 原地复用，理由与属性整备那张表相同：
    /// id 由源文件全路径定死，重新解析后同一个零件仍是同一行。
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
            row.PartName = entry.PartName;
            row.QuantityText = entry.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.DrawingStateText = entry.HasDrawing ? "有" : "无";
            row.CategoryText = entry.Category == PackagePartCategory.Machined ? "机加件" : "外购件";
            row.RenamesFile = false;
            row.WritesProperties = false;
            row.Status = ConversionFileRow.ReadyStatus;
            row.Detail = entry.HasDrawing
                ? Path.GetFileName(entry.DrawingPath!)
                : "没有同名工程图";
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

    private async Task PackCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _packagePlan ?? throw new InvalidOperationException("请先成功解析装配体。");
        if (!plan.CanPack)
            throw new InvalidOperationException(PackBlockedReason);
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源装配体在解析后发生变化，请重新解析后再打包。");

        CreatePackageDirectories(plan.Directories);

        // BOM 先写。它不需要 SolidWorks，也就没有理由陪着 CAD 导出一起失败——
        // 现场最常见的一幕正是「SolidWorks 起不来」，而采购要的清单本来就已经算好了。
        var machinedCount = BomWorkbookWriter.WriteMachined(
            plan, Path.Combine(plan.Directories.BomDirectory, plan.MachinedBomFileName));
        var purchasedCount = BomWorkbookWriter.WritePurchased(
            plan, Path.Combine(plan.Directories.BomDirectory, plan.PurchasedBomFileName));
        QueueUiUpdate(() => StatusText =
            $"BOM 已生成：{machinedCount} 个机加件、{purchasedCount} 个外购件");
        _operationProgress?.Report(
            $"BOM 已生成：{plan.MachinedBomFileName}（{machinedCount} 行）、"
            + $"{plan.PurchasedBomFileName}（{purchasedCount} 行）");

        var jobs = BuildPackageJobs(plan);
        if (jobs.Count == 0)
        {
            _lastResultText = $"打包完成：只生成了两张 BOM，没有可导出的零件或工程图。";
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

        foreach (var row in Parts)
        {
            row.Status = "排队";
            row.Detail = string.Empty;
        }

        var stepCount = jobs.Count(job => job.Artifact == PackageArtifact.Step);
        var drawingCount = jobs.Count(job => job.Artifact == PackageArtifact.Dwg);
        QueueUiUpdate(() => StatusText =
            $"正在导出 {stepCount} 个 STEP、{drawingCount} 张工程图的 DWG 与 PDF");
        var exitCode = await _packWorker(request, ReportWorkerEvent, cancellationToken).ConfigureAwait(false);

        _lastOperationSucceeded = exitCode == 0;
        _lastResultText = exitCode == 0
            ? $"打包完成：{plan.Directories.RootDirectory} 下的 STP／DWG／PDF／BOM 四个目录已就绪"
                + $"（{stepCount} 个 STEP、{drawingCount} 张工程图、"
                + $"{machinedCount} + {purchasedCount} 行 BOM）"
            : "打包结束，存在导出失败项：BOM 已生成，失败的零件或图纸见上方逐条报告"
                + (string.IsNullOrEmpty(_firstWorkerFailure) ? string.Empty : "；首个原因：" + _firstWorkerFailure);
        QueueUiUpdate(() =>
        {
            _conversionCompleted = true;
            foreach (var row in Parts)
            {
                if (row.Status is not ("失败" or "已取消"))
                    row.Status = "完成";
            }

            StatusText = _lastResultText;
            OnPropertyChanged(nameof(CanConvert));
        });
    }

    /// <summary>
    /// 机加件一个 STEP，有图纸的零件各一个 DWG 与一个 PDF。
    ///
    /// 三个作业共用同一个 <see cref="PackagePartEntry.Id"/>：表里它们本来就是同一行，
    /// 用三个 id 只会让进度事件找不到行。产物路径按文件主名放进各自目录，
    /// 重名由 <c>WorkerRequestValidator</c> 拦下——同名零件互相覆盖会交付一个缺件的包。
    /// </summary>
    private static IReadOnlyList<PackageJob> BuildPackageJobs(PackagePlan plan)
    {
        var jobs = new List<PackageJob>(plan.StepTargets.Count + (plan.DrawingTargets.Count * 2));
        foreach (var entry in plan.StepTargets)
        {
            jobs.Add(new PackageJob(
                entry.Id,
                entry.SourcePath,
                OutputPath(plan.Directories.StepDirectory, entry.SourcePath, ConversionPathLayout.StepExtension),
                PackageArtifact.Step));
        }

        foreach (var entry in plan.DrawingTargets)
        {
            var drawingPath = entry.DrawingPath!;
            jobs.Add(new PackageJob(
                entry.Id,
                drawingPath,
                OutputPath(plan.Directories.DwgDirectory, drawingPath, ConversionPathLayout.DwgExtension),
                PackageArtifact.Dwg));
            jobs.Add(new PackageJob(
                entry.Id,
                drawingPath,
                OutputPath(plan.Directories.PdfDirectory, drawingPath, ConversionPathLayout.PdfExtension),
                PackageArtifact.Pdf));
        }

        return jobs;
    }

    private static string OutputPath(string directory, string sourcePath, string extension)
        => Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + extension);

    /// <summary>
    /// 四个目录一次建齐，**包括本轮不会往里放东西的那些**。
    ///
    /// 一台没有外购件、也没有工程图的设备照样该得到四个目录：空的 DWG 目录说的是
    /// 「这一批没有图纸」，而缺一个目录说的是「打包是不是没跑完」——后者要用户自己去猜。
    /// </summary>
    private static void CreatePackageDirectories(PackageOutputDirectories directories)
    {
        foreach (var directory in new[]
                 {
                     directories.StepDirectory,
                     directories.DwgDirectory,
                     directories.PdfDirectory,
                     directories.BomDirectory,
                 })
        {
            if (File.Exists(directory))
                throw new IOException($"打包目录被同名文件占用：{directory}");
            Directory.CreateDirectory(directory);
        }
    }
}
