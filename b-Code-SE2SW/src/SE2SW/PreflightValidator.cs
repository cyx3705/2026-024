using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public static class PreflightValidator
{
    public static void ValidateEnvironment(string workerPath)
    {
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("未找到 SE2SW 工作进程，请重新构建或同步模块。", workerPath);
        if (Type.GetTypeFromProgID("SolidEdge.Application", throwOnError: false) is null)
            throw new InvalidOperationException("未检测到 Solid Edge COM 注册（SolidEdge.Application）。");
        if (Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false) is null)
            throw new InvalidOperationException("未检测到 SolidWorks COM 注册（SldWorks.Application）。");
    }

    public static void ValidateJobs(IEnumerable<ConversionJob> jobs, bool overwrite)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateJobs(jobs, overwrite, outputs);
    }

    public static void ValidateAssemblyRequest(AssemblyBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode != ConversionMode.External)
            throw new InvalidDataException("当前装配转换仅支持外界模式。");
        if (!Path.IsPathFullyQualified(request.SourceAssemblyPath)
            || !File.Exists(request.SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(request.SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension))
        {
            throw new FileNotFoundException("源装配体不存在或不是绝对 .asm 路径。", request.SourceAssemblyPath);
        }

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateJobs(request.PartJobs, request.Overwrite, outputs, allowExistingOutputs: true);

        // V3.3：嵌套时每个装配节点都有自己的输出，逐个校验；已存在不再是硬阻断，
        // 由 Worker 的复用判定核验它是否比全部依赖都新（与零件同口径）。
        if (request.Nodes is { Count: > 0 })
        {
            var rootCount = 0;
            foreach (var node in request.Nodes)
            {
                if (!Path.IsPathFullyQualified(node.SourceAssemblyPath)
                    || !File.Exists(node.SourceAssemblyPath)
                    || !ConversionPathLayout.HasExtension(node.SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension))
                {
                    throw new FileNotFoundException("装配节点的源文件不存在或不是绝对 .asm 路径。", node.SourceAssemblyPath);
                }
                if (node.Children.Count == 0)
                    throw new InvalidDataException($"装配节点没有任何子项：{node.SourceAssemblyPath}");
                foreach (var child in node.Children)
                {
                    if (child.LocalTransform.Length != 16 || child.LocalTransform.Any(value => !double.IsFinite(value)))
                        throw new InvalidDataException($"子项局部矩阵无效：{node.SourceAssemblyPath} → {child.Name}");
                }
                if (node.IsRoot)
                    rootCount++;
                ValidateOutput(node.OutputPath, ConversionArtifactKind.SolidWorksAssembly, request.Overwrite, outputs, allowExisting: true);
            }

            if (rootCount != 1)
                throw new InvalidDataException($"装配节点必须恰好有一个顶层，实得 {rootCount} 个。");
        }
        else
        {
            ValidateOutput(request.AssemblyOutputPath, ConversionArtifactKind.SolidWorksAssembly, request.Overwrite, outputs);
        }

        if (request.Occurrences.Count == 0)
            throw new InvalidDataException("装配实例清单为空。");

        var occurrenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in request.Occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId) || !occurrenceIds.Add(occurrence.OccurrenceId))
                throw new InvalidDataException($"装配实例编号为空或重复：{occurrence.OccurrenceId}");
            if (occurrence.WorldTransform.Length != 16
                || occurrence.WorldTransform.Any(value => !double.IsFinite(value)))
            {
                throw new InvalidDataException($"实例矩阵无效：{occurrence.OccurrenceId}");
            }
            if (!occurrence.IsSuppressed && occurrence.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true)
                throw new FileNotFoundException($"装配实例引用未解析：{occurrence.SourcePath}", occurrence.SourcePath);
        }
    }

    private static void ValidateJobs(
        IEnumerable<ConversionJob> jobs,
        bool overwrite,
        ISet<string> outputs,
        bool allowExistingOutputs = false)
    {
        var count = 0;
        foreach (var job in jobs)
        {
            count++;
            if (!Path.IsPathFullyQualified(job.SourcePath) || !File.Exists(job.SourcePath))
                throw new FileNotFoundException("源文件不存在或不是绝对路径。", job.SourcePath);
            if (!ConversionPathLayout.HasExtension(job.SourcePath, ConversionPathLayout.SolidEdgePartExtension))
                throw new InvalidDataException($"输入不是 Solid Edge .par 文件：{job.SourcePath}");
            ValidateOutput(job.XtPath, ConversionArtifactKind.Xt, overwrite, outputs, allowExistingOutputs);
            ValidateOutput(job.SolidWorksPath, ConversionArtifactKind.SolidWorksPart, overwrite, outputs, allowExistingOutputs);
        }
        if (count == 0)
            throw new InvalidOperationException("没有选中可转换文件。");
    }

    private static void ValidateOutput(
        string path,
        ConversionArtifactKind artifact,
        bool overwrite,
        ISet<string> outputs,
        bool allowExisting = false)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"输出不是绝对路径：{path}");
        if (!ConversionPathLayout.HasExtension(path, artifact))
            throw new InvalidDataException($"输出扩展名必须是 {ConversionPathLayout.GetExtension(artifact)}：{path}");
        if (!outputs.Add(Path.GetFullPath(path)))
            throw new InvalidDataException($"批次中存在重复输出：{path}");
        if (!allowExisting && !overwrite && File.Exists(path))
            throw new IOException($"输出已经存在：{path}");

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"输出目录不存在：{parent}");

        var probe = Path.Combine(parent, $".se2sw-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex)
        {
            throw new IOException($"输出目录不可写：{parent}", ex);
        }
    }
}
