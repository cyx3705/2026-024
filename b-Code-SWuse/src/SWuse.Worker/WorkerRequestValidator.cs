using SWuse.Contracts;

namespace SWuse.Worker;

internal static class WorkerRequestValidator
{
    public static void Validate(SWuseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
            throw new DirectoryNotFoundException("工作区必须是存在的绝对路径：" + request.WorkspacePath);
        if (!Path.IsPathFullyQualified(request.OutputPartPath)
            || !string.Equals(Path.GetExtension(request.OutputPartPath), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("目标必须是绝对 .SLDPRT 路径：" + request.OutputPartPath);
        }
        if (request.SourceFiles is not { Count: > 0 })
            throw new InvalidDataException("工作区没有可编译的 C# 源文件。");

        var workspace = Path.GetFullPath(request.WorkspacePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in request.SourceFiles)
        {
            if (!Path.IsPathFullyQualified(sourcePath)
                || !string.Equals(Path.GetExtension(sourcePath), ".cs", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("源码必须是存在的绝对 .cs 路径：" + sourcePath, sourcePath);
            }
            var fullPath = Path.GetFullPath(sourcePath);
            if (!IsWithin(fullPath, workspace))
                throw new InvalidDataException("源码不得位于工作区之外：" + fullPath);
            if (!seen.Add(fullPath))
                throw new InvalidDataException("源码清单存在重复项：" + fullPath);
        }

        if (!request.DryRun && File.Exists(request.OutputPartPath) && !request.Overwrite)
            throw new IOException("目标文件已存在，V0.1 默认拒绝覆盖：" + request.OutputPartPath);
    }

    internal static bool IsWithin(string childPath, string parentPath)
    {
        var relative = Path.GetRelativePath(parentPath, childPath);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
