using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

internal static class WorkerRequestValidator
{
    public static void Validate(BatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId) || request.Jobs.Count == 0)
            throw new InvalidDataException("批处理编号或任务列表无效。");

        var sourceExtension = ConversionPathLayout.GetSourcePartExtension(request.SourceFormat);
        var usesParasolid = ConversionPathLayout.UsesParasolidHandoff(request.SourceFormat);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in request.Jobs)
        {
            ValidatePath(job.SourcePath, sourceExtension, mustExist: true);
            if (usesParasolid)
                ValidatePath(job.XtPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.Xt), mustExist: false);
            ValidatePath(job.SolidWorksPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.SolidWorksPart), mustExist: false);
            if (usesParasolid && !outputs.Add(Path.GetFullPath(job.XtPath)))
                throw new InvalidDataException($"批次中存在重复输出：{job.Id}");
            if (!outputs.Add(Path.GetFullPath(job.SolidWorksPath)))
                throw new InvalidDataException($"批次中存在重复输出：{job.Id}");
            // 源零件与产物同名不同目录是 SW 自整备的常态，只有真的指到同一个文件才拦。
            if (SameFile(job.SourcePath, job.SolidWorksPath))
                throw new InvalidDataException($"产物不能就是源零件本身：{job.Id}");
            if (!request.Overwrite
                && (File.Exists(job.SolidWorksPath) || (usesParasolid && File.Exists(job.XtPath))))
            {
                throw new IOException($"输出已经存在：{job.Id}");
            }
        }
    }

    public static void Validate(PartImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId) || string.IsNullOrWhiteSpace(request.Job.Id))
            throw new InvalidDataException("单零件导入批次编号或任务编号无效。");

        ValidatePath(
            request.Job.SourcePath,
            ConversionPathLayout.GetSourcePartExtension(request.SourceFormat),
            mustExist: true);
        if (ConversionPathLayout.UsesParasolidHandoff(request.SourceFormat))
            ValidatePath(request.Job.XtPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.Xt), mustExist: true);
        ValidatePath(request.Job.SolidWorksPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.SolidWorksPart), mustExist: false);
        if (SameFile(request.Job.SourcePath, request.Job.SolidWorksPath))
            throw new InvalidDataException($"产物不能就是源零件本身：{request.Job.Id}");
        if (!request.Overwrite && File.Exists(request.Job.SolidWorksPath))
            throw new IOException($"输出已经存在：{request.Job.Id}");
    }

    public static void Validate(AssemblyProbeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId))
            throw new InvalidDataException("装配探查批次编号无效。");
        ValidatePath(
            request.SourceAssemblyPath,
            ConversionPathLayout.GetSourceAssemblyExtension(request.SourceFormat),
            mustExist: true);
        if (!Path.IsPathFullyQualified(request.ResultPath))
            throw new InvalidDataException("探查结果路径必须是绝对路径。");
        var parent = Path.GetDirectoryName(request.ResultPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"探查结果目录不存在：{parent}");
    }

    public static void Validate(AssemblyBatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId) || request.Mode != ConversionMode.External)
            throw new InvalidDataException("当前装配转换仅支持外界模式。");
        var sourceAssemblyExtension = ConversionPathLayout.GetSourceAssemblyExtension(request.SourceFormat);
        var sourcePartExtension = ConversionPathLayout.GetSourcePartExtension(request.SourceFormat);
        var usesParasolid = ConversionPathLayout.UsesParasolidHandoff(request.SourceFormat);
        ValidatePath(request.SourceAssemblyPath, sourceAssemblyExtension, mustExist: true);
        ValidatePath(request.AssemblyOutputPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.SolidWorksAssembly), mustExist: false);
        if (SameFile(request.SourceAssemblyPath, request.AssemblyOutputPath))
            throw new InvalidDataException($"装配产物不能就是源装配本身：{request.AssemblyOutputPath}");
        if (!request.Overwrite && File.Exists(request.AssemblyOutputPath))
            throw new IOException($"装配输出已经存在：{request.AssemblyOutputPath}");
        if (request.PartJobs.Count == 0)
            throw new InvalidDataException("装配唯一零件任务为空。");
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(request.AssemblyOutputPath),
        };
        foreach (var job in request.PartJobs)
        {
            ValidatePath(job.SourcePath, sourcePartExtension, mustExist: true);
            if (usesParasolid)
            {
                ValidatePath(job.XtPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.Xt), mustExist: false);
                if (!outputs.Add(Path.GetFullPath(job.XtPath)))
                    throw new InvalidDataException($"装配批次中存在重复输出：{job.Id}");
            }
            ValidatePath(job.SolidWorksPath, ConversionPathLayout.GetExtension(ConversionArtifactKind.SolidWorksPart), mustExist: false);
            if (!outputs.Add(Path.GetFullPath(job.SolidWorksPath)))
                throw new InvalidDataException($"装配批次中存在重复输出：{job.Id}");
            if (SameFile(job.SourcePath, job.SolidWorksPath))
                throw new InvalidDataException($"产物不能就是源零件本身：{job.Id}");
        }
        if (request.Occurrences.Count == 0)
            throw new InvalidDataException("装配实例清单为空。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supportedPartPaths = new HashSet<string>(
            request.PartJobs.Select(job => Path.GetFullPath(job.SourcePath)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in request.Occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId) || !ids.Add(occurrence.OccurrenceId))
                throw new InvalidDataException($"实例编号为空或重复：{occurrence.OccurrenceId}");
            if (occurrence.WorldTransform.Length != 16 || occurrence.WorldTransform.Any(value => !double.IsFinite(value)))
                throw new InvalidDataException($"实例矩阵无效：{occurrence.OccurrenceId}");
            if (!Path.IsPathFullyQualified(occurrence.SourcePath))
                throw new InvalidDataException($"实例引用不是绝对路径：{occurrence.OccurrenceId}");
            if (!occurrence.IsSuppressed
                && occurrence.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true)
            {
                throw new FileNotFoundException("装配实例引用未解析。", occurrence.SourcePath);
            }
            if (!occurrence.IsSubAssembly
                && !occurrence.IsSuppressed
                && ConversionPathLayout.HasExtension(occurrence.SourcePath, sourcePartExtension)
                && !supportedPartPaths.Contains(Path.GetFullPath(occurrence.SourcePath)))
            {
                throw new InvalidDataException($"实例没有对应的唯一零件任务：{occurrence.OccurrenceId}");
            }
        }
    }

    /// <summary>
    /// 两个路径是不是同一个文件。SW 自整备管线里源与产物同名不同目录是常态，
    /// 真正必须拦下的只有"产物就是源文件本身"——那会当场毁掉用户的原始零件。
    /// </summary>
    private static bool SameFile(string left, string right)
        => Path.IsPathFullyQualified(left)
           && Path.IsPathFullyQualified(right)
           && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void ValidatePath(string path, string extension, bool mustExist)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"路径必须是绝对路径：{path}");
        if (!ConversionPathLayout.HasExtension(path, extension))
            throw new InvalidDataException($"路径扩展名必须是 {extension}：{path}");
        if (mustExist && !File.Exists(path))
            throw new FileNotFoundException("源文件不存在。", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException($"输出目录不存在：{Path.GetDirectoryName(path)}");
    }

    public static void Validate(AssemblyRenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId))
            throw new InvalidDataException("改名批次编号无效。");
        if (request.SourceFormat != ConversionSourceFormat.SolidWorks)
            throw new InvalidDataException("属性整备改名目前只支持 SolidWorks 装配体。");
        // 前缀为空是合法的：那一轮把图号改成空，也就是删图号。这里只拦空格与非法字符。
        _ = DrawingNumber.NormalizePrefix(request.DrawingPrefix);
        ValidateSolidWorksDocument(request.SourceAssemblyPath, mustExist: true);
        if (request.Entries.Count == 0)
            throw new InvalidDataException("改名清单为空。");

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidDataException("改名条目缺少编号。");
            ValidateSolidWorksDocument(entry.SourcePath, mustExist: true);
            ValidateSolidWorksDocument(entry.TargetPath, mustExist: false);
            var source = Path.GetFullPath(entry.SourcePath);
            var target = Path.GetFullPath(entry.TargetPath);
            if (!sources.Add(source))
                throw new InvalidDataException($"改名清单存在重复源文件：{source}");
            if (!targets.Add(target))
                throw new InvalidDataException($"改名清单存在重复目标：{target}");
            var sourceDir = Path.GetDirectoryName(source);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"改名不得换目录：{source}");
            if (!string.Equals(Path.GetExtension(source), Path.GetExtension(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"改名不得改扩展名：{source}");
            if (!AssemblyRenamePlan.SamePath(source, target) && File.Exists(target))
                throw new IOException($"目标文件已存在：{target}");
            foreach (var parent in entry.ParentSourcePaths)
                ValidateSolidWorksDocument(parent, mustExist: true);
        }
    }

    /// <summary>
    /// V4.10 整体打包导出请求。四条判据缺一不可：批次有名字、总装配体是存在的
    /// <c>.SLDASM</c>、每个作业的源与产物类型对得上、同一个产物路径只出现一次。
    ///
    /// 最后一条是这条链路特有的：STP / DWG / PDF 三个目录按**文件主名**放产物，
    /// 两个不同目录下的同名零件会算出同一个 <c>STP\阀体.STEP</c>，后一个悄悄盖掉前一个。
    /// 与其交付一个少了零件的包，不如在这里挡住。
    /// </summary>
    public static void Validate(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BatchId))
            throw new InvalidDataException("打包批次编号无效。");
        ValidateSolidWorksDocument(request.SourceAssemblyPath, mustExist: true);
        if (!ConversionPathLayout.HasExtension(
                request.SourceAssemblyPath, ConversionPathLayout.SolidWorksAssemblyExtension))
        {
            throw new InvalidDataException("整体打包只接受 SolidWorks .SLDASM 总装配体。");
        }
        if (request.Jobs.Count == 0)
            throw new InvalidDataException("打包作业清单为空。");

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in request.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
                throw new InvalidDataException("打包作业缺少编号。");
            if (!Path.IsPathFullyQualified(job.SourcePath) || !Path.IsPathFullyQualified(job.OutputPath))
                throw new InvalidDataException($"打包作业路径必须是绝对路径：{job.SourcePath}");
            if (!File.Exists(job.SourcePath))
                throw new FileNotFoundException("打包源文件不存在。", job.SourcePath);

            var expectedSource = job.Artifact == PackageArtifact.Step
                ? ConversionPathLayout.SolidWorksPartExtension
                : ConversionPathLayout.SolidWorksDrawingExtension;
            if (!ConversionPathLayout.HasExtension(job.SourcePath, expectedSource))
                throw new InvalidDataException($"{job.Artifact} 作业的源必须是 {expectedSource}：{job.SourcePath}");

            var expectedOutput = job.Artifact switch
            {
                PackageArtifact.Step => ConversionPathLayout.StepExtension,
                PackageArtifact.Dwg => ConversionPathLayout.DwgExtension,
                PackageArtifact.Pdf => ConversionPathLayout.PdfExtension,
                _ => throw new InvalidDataException($"未知打包产物类型：{job.Artifact}"),
            };
            if (!ConversionPathLayout.HasExtension(job.OutputPath, expectedOutput))
                throw new InvalidDataException($"{job.Artifact} 作业的产物必须是 {expectedOutput}：{job.OutputPath}");
            if (!outputs.Add(Path.GetFullPath(job.OutputPath)))
                throw new InvalidDataException($"打包清单存在重复产物路径：{job.OutputPath}");
        }
    }

    private static void ValidateSolidWorksDocument(string path, bool mustExist)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"路径必须是绝对路径：{path}");
        var isPart = ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksPartExtension);
        var isAssembly = ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksAssemblyExtension);
        if (!isPart && !isAssembly)
            throw new InvalidDataException($"路径必须是 .SLDPRT 或 .SLDASM：{path}");
        if (mustExist && !File.Exists(path))
            throw new FileNotFoundException("源文件不存在。", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException($"输出目录不存在：{Path.GetDirectoryName(path)}");
    }
}
