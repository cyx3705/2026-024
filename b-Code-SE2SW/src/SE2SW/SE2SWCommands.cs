using System.Runtime.InteropServices;
using System.IO;
using SE2SW.Contracts;
using AppShell.Core.Modules;

namespace SE2SW;

public sealed record SE2SWStatus(
    string Module,
    string Version,
    string WindowId,
    bool WorkerPresent,
    string WorkerPath,
    bool SolidEdgeComRegistered,
    bool SolidWorksComRegistered,
    IReadOnlyList<string> SupportedSources,
    IReadOnlyList<string> OutputDirectories);

public sealed record SE2SWPathResponse(
    string Source,
    string Xt,
    string SolidWorks,
    string LegacyXt,
    string LegacySolidWorks,
    string XtDirectory,
    string SolidWorksDirectory);

public sealed record SE2SWScanItem(
    string Source,
    string Xt,
    string SolidWorks,
    bool XtExists,
    bool SolidWorksExists,
    bool HasExistingOutput);

public sealed record SE2SWScanResponse(
    string SourceDirectory,
    string XtDirectory,
    string SolidWorksDirectory,
    IReadOnlyList<SE2SWScanItem> Items,
    int Count,
    int ExistingOutputCount);

public sealed record SE2SWAssemblyPathResponse(
    string SourceAssembly,
    string AssemblyOutput,
    string XtDirectory,
    string SolidWorksDirectory);

public sealed record SE2SWAssemblyNodeResponse(
    string SourceAssembly,
    string Output,
    bool IsRoot,
    int Depth,
    int ChildCount);

public sealed record SE2SWAssemblyProbeResponse(
    string SourceAssembly,
    string AssemblyOutput,
    string XtDirectory,
    string SolidWorksDirectory,
    bool CanConvert,
    int OccurrenceCount,
    int PartCount,
    int ExistingOutputCount,
    int SuppressedCount,
    int UnresolvedCount,
    int SubAssemblyCount,
    int MaxDepth,
    int RelationCount,
    IReadOnlyList<SE2SWScanItem> Parts,
    IReadOnlyList<SE2SWAssemblyNodeResponse> Nodes,
    IReadOnlyList<AssemblyPlanIssue> BlockingIssues,
    IReadOnlyList<string> Warnings);

public sealed class SE2SWCommands
{
    /// <summary>说明如何显示 Mapping 内嵌工具窗口。</summary>
    [ModuleCommand(Readonly = true)]
    public string show()
    {
        return "win.show name=mapping；窗口内可选择 .par → .SLDPRT 或 .asm → .SLDASM";
    }

    /// <summary>说明如何隐藏 Mapping 内嵌工具窗口。</summary>
    [ModuleCommand(Readonly = true)]
    public string hide()
    {
        return "win.hide name=mapping";
    }

    /// <summary>返回 Mapping 当前版本、Worker 和 CAD COM 注册状态。</summary>
    [ModuleCommand(Readonly = true)]
    public SE2SWStatus status()
    {
        var workerPath = WorkerLocator.Locate();
        return new SE2SWStatus(
            Module: "mapping",
            Version: Version,
            WindowId: "mapping",
            WorkerPresent: File.Exists(workerPath),
            WorkerPath: workerPath,
            SolidEdgeComRegistered: IsComRegistered("SolidEdge.Application"),
            SolidWorksComRegistered: IsComRegistered("SldWorks.Application"),
            SupportedSources: [ConversionPathLayout.SolidEdgePartExtension, ConversionPathLayout.SolidEdgeAssemblyExtension],
            OutputDirectories: [ConversionPathLayout.XtDirectoryName, ConversionPathLayout.SolidWorksDirectoryName]);
    }

    /// <summary>解析一个 Solid Edge 零件对应的 XT 和 SolidWorks 输出路径，不创建文件或目录。</summary>
    [ModuleCommand(Readonly = true)]
    public SE2SWPathResponse paths(string source, string xt = "", string sw = "")
    {
        var sourcePath = Path.GetFullPath(source.Trim());
        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new ArgumentException("source 必须包含有效目录。", nameof(source));
        var directories = ExternalOutputLayout.Resolve(sourceDirectory, xt, sw);
        var paths = ConversionPathLayout.ResolvePartPaths(
            sourcePath, directories.XtDirectory, directories.SolidWorksDirectory, directories.RootDirectory);
        return new SE2SWPathResponse(
            sourcePath, paths.XtPath, paths.SolidWorksPath, paths.LegacyXtPath, paths.LegacySolidWorksPath,
            directories.XtDirectory, directories.SolidWorksDirectory);
    }

    /// <summary>扫描目录顶层 Solid Edge 零件并返回输出存在状态，不创建文件或目录。</summary>
    [ModuleCommand(Readonly = true)]
    public SE2SWScanResponse scan(string source, string xt = "", string sw = "")
    {
        var sourceDirectory = Path.GetFullPath(source.Trim());
        var directories = ExternalOutputLayout.Resolve(sourceDirectory, xt, sw);
        var items = FileScanner.Scan(
                ConversionMode.External,
                sourceDirectory,
                outputDirectories: directories,
                allowLegacyXt: true,
                allowLegacySolidWorks: true)
            .Select(candidate => new SE2SWScanItem(
                candidate.SourcePath,
                candidate.XtPath,
                candidate.SolidWorksPath,
                File.Exists(candidate.XtPath) || File.Exists(Path.Combine(sourceDirectory, Path.GetFileNameWithoutExtension(candidate.SourcePath) + ConversionPathLayout.XtExtension)),
                File.Exists(candidate.SolidWorksPath) || File.Exists(Path.Combine(sourceDirectory, Path.GetFileNameWithoutExtension(candidate.SourcePath) + ConversionPathLayout.SolidWorksPartExtension)),
                candidate.HasExistingOutput))
            .ToArray();
        return new SE2SWScanResponse(
            sourceDirectory,
            directories.XtDirectory,
            directories.SolidWorksDirectory,
            items,
            items.Length,
            items.Count(item => item.HasExistingOutput));
    }

    /// <summary>解析 Solid Edge 装配体对应的 SolidWorks 装配输出路径，不启动 CAD 或创建目录。</summary>
    /// <param name="source">Solid Edge .asm 文件路径。</param>
    /// <param name="xt">可选的 XT 输出目录；空值使用源目录下的 XT。</param>
    /// <param name="sw">可选的 SolidWorks 输出目录；空值使用源目录下的 SW。</param>
    [ModuleCommand(Readonly = true)]
    public SE2SWAssemblyPathResponse assemblyPaths(string source, string xt = "", string sw = "")
    {
        var sourcePath = NormalizeAssemblyPath(source, requireExisting: false);
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var directories = ExternalOutputLayout.Resolve(sourceDirectory, xt, sw);
        return new SE2SWAssemblyPathResponse(
            sourcePath,
            ConversionPathLayout.ResolveAssemblyOutputPath(sourcePath, directories.SolidWorksDirectory),
            directories.XtDirectory,
            directories.SolidWorksDirectory);
    }

    /// <summary>只读探查 Solid Edge 装配体并返回层级、零件、关系、阻塞问题和计划输出，不执行转换。</summary>
    /// <param name="source">存在的 Solid Edge .asm 文件路径。</param>
    /// <param name="xt">可选的 XT 输出目录；空值使用源目录下的 XT。</param>
    /// <param name="sw">可选的 SolidWorks 输出目录；空值使用源目录下的 SW。</param>
    [ModuleCommand(Readonly = true)]
    public async Task<SE2SWAssemblyProbeResponse> assemblyProbe(string source, string xt = "", string sw = "")
    {
        var sourcePath = NormalizeAssemblyPath(source, requireExisting: true);
        PreflightValidator.ValidateEnvironment(WorkerClient.WorkerPath);
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
            var probe = await new WorkerClient().ProbeAssemblyAsync(
                new AssemblyProbeRequest(batchId, sourcePath, resultPath),
                static _ => { },
                CancellationToken.None).ConfigureAwait(false);
            var plan = AssemblyPlanner.Create(probe, xt, sw);
            var parts = plan.Parts.Select(candidate => new SE2SWScanItem(
                candidate.SourcePath,
                candidate.XtPath,
                candidate.SolidWorksPath,
                File.Exists(candidate.XtPath),
                File.Exists(candidate.SolidWorksPath),
                candidate.HasExistingOutput)).ToArray();
            var nodes = (plan.Nodes ?? []).Select(node => new SE2SWAssemblyNodeResponse(
                node.SourceAssemblyPath,
                node.OutputPath,
                node.IsRoot,
                node.Depth,
                node.Children.Count)).ToArray();
            return new SE2SWAssemblyProbeResponse(
                plan.SourceAssemblyPath,
                plan.AssemblyOutputPath,
                plan.XtDirectory,
                plan.SolidWorksDirectory,
                plan.CanConvert,
                plan.Occurrences.Count,
                plan.Parts.Count,
                plan.Parts.Count(part => part.HasExistingOutput),
                probe.SuppressedCount,
                probe.UnresolvedCount,
                plan.SubAssemblyCount,
                plan.MaxDepth,
                plan.RelationCount,
                parts,
                nodes,
                plan.BlockingIssues,
                plan.Warnings);
        }
        finally
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
        }
    }

    private string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string NormalizeAssemblyPath(string source, bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source 不能为空。", nameof(source));
        var sourcePath = Path.GetFullPath(source.Trim());
        if (!ConversionPathLayout.HasExtension(sourcePath, ConversionPathLayout.SolidEdgeAssemblyExtension))
            throw new ArgumentException("source 必须是 Solid Edge .asm 文件。", nameof(source));
        if (requireExisting && !File.Exists(sourcePath))
            throw new FileNotFoundException("Solid Edge 装配体不存在。", sourcePath);
        return sourcePath;
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

    private static bool IsComRegistered(string progId)
    {
        try
        {
            return Type.GetTypeFromProgID(progId, throwOnError: false) != null;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
