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

public sealed class SE2SWCommands
{
    /// <summary>说明如何显示 SE2SW 内嵌工具窗口。</summary>
    [ModuleCommand(Readonly = true)]
    public string show()
    {
        return "win.show name=se2sw；窗口内可选择“零件转换”“装配转换”或“OHS 兼容”模式";
    }

    /// <summary>说明如何隐藏 SE2SW 内嵌工具窗口。</summary>
    [ModuleCommand(Readonly = true)]
    public string hide()
    {
        return "win.hide name=se2sw";
    }

    /// <summary>返回 SE2SW 当前版本、Worker 和 CAD COM 注册状态。</summary>
    [ModuleCommand(Readonly = true)]
    public SE2SWStatus status()
    {
        var workerPath = WorkerLocator.Locate();
        return new SE2SWStatus(
            Module: "se2sw",
            Version: Version,
            WindowId: "se2sw",
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

    private string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";

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
