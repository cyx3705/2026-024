using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

public sealed class MappingRuntimePaths
{
    private const int MinimumNumberedProjects = 2;
    private static readonly Regex NumberedProjectName =
        new(@"^\d{4}-\d{3}-.+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public MappingRuntimePaths(string hostDataDirectory, string? configuredModuleDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDataDirectory);
        HostDataDirectory = Path.GetFullPath(hostDataDirectory);
        ModuleDataDirectory = Path.Combine(HostDataDirectory, HistoryMinervaIdentity.DataDirectoryName);
        ModuleDirectory = string.IsNullOrWhiteSpace(configuredModuleDirectory)
            ? Path.Combine(HostDataDirectory, "Modules", HistoryMinervaIdentity.Name)
            : Path.Combine(Path.GetFullPath(configuredModuleDirectory), HistoryMinervaIdentity.Name);
    }

    public string HostDataDirectory { get; }
    public string ModuleDataDirectory { get; }
    public string ModuleDirectory { get; }
    public string RequestsDirectory => Path.Combine(ModuleDataDirectory, HistoryMinervaIdentity.RequestsDirectoryName);
    public string ProbesDirectory => Path.Combine(ModuleDataDirectory, HistoryMinervaIdentity.ProbesDirectoryName);

    public string LocateWorker()
    {
        foreach (var candidate in WorkerCandidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return WorkerCandidates()[0];
    }

    public IReadOnlyList<string> WorkerCandidates()
    {
        var candidates = new List<string>();
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            candidates.Add(Path.Combine(Path.GetDirectoryName(assemblyLocation)!, HistoryMinervaIdentity.WorkerFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, HistoryMinervaIdentity.WorkerFileName));
        candidates.Add(Path.Combine(ModuleDirectory, HistoryMinervaIdentity.WorkerFileName));
        candidates.AddRange(FindPublishedPackageWorkers());
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> FindPublishedPackageWorkers()
    {
        // 不要用 Environment.CurrentDirectory：OpenFileDialog 默认会把进程
        // 当前目录改到所选 CAD 文件旁边。从那个（往往极大的）零件库往上
        // 枚举每一层子目录时，UI 线程会像死掉一样。
        var seeds = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty),
        };

        foreach (var seed in seeds.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(seed!);
                if (File.Exists(directory.FullName))
                    directory = directory.Parent;
            }
            catch (ArgumentException)
            {
                continue;
            }

            for (; directory is not null; directory = directory.Parent)
            {
                foreach (var candidate in EnumeratePackageWorkers(directory.FullName))
                    yield return candidate;

                if (!LooksLikeLibraryRoot(directory.FullName)
                    && !Directory.Exists(Path.Combine(directory.FullName, "HistoryVesta.git")))
                    continue;

                IEnumerable<string> projects;
                try
                {
                    projects = Directory.EnumerateDirectories(directory.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                foreach (var project in projects)
                {
                    foreach (var candidate in EnumeratePackageWorkers(project))
                        yield return candidate;
                }

                yield break;
            }
        }
    }

    private static IEnumerable<string> EnumeratePackageWorkers(string root)
    {
        var worker = HistoryMinervaIdentity.WorkerFileName;
        var legacyRoot = Path.Combine(root, $"z-{HistoryMinervaIdentity.Name}");
        if (Directory.Exists(legacyRoot))
            yield return Path.Combine(legacyRoot, worker);

        var publishRoot = Path.Combine(root, "z-Publish");
        if (!Directory.Exists(publishRoot))
            yield break;

        yield return Path.Combine(publishRoot, worker);

        IEnumerable<string> versioned;
        try
        {
            versioned = Directory.EnumerateDirectories(publishRoot, HistoryMinervaIdentity.Name + "-v*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var package in versioned)
            yield return Path.Combine(package, worker);
    }

    /// <summary>
    /// 编号项目库根：至少两个 <c>YYYY-NNN-*</c> 子目录。HistoryClio 用这个识别，不再依赖裸仓哨兵。
    /// </summary>
    private static bool LooksLikeLibraryRoot(string directory)
    {
        try
        {
            var counted = 0;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (!NumberedProjectName.IsMatch(Path.GetFileName(child)))
                    continue;
                if (++counted >= MinimumNumberedProjects)
                    return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public static MappingRuntimePaths CreateAppShellFallback()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppShell"));

    public static MappingRuntimePaths CreateHistoryVulcanDefault()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HistoryVulcan"));
}
