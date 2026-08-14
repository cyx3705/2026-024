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
        var seeds = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
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
                var localPackage = Path.Combine(
                    directory.FullName,
                    $"z-{HistoryMinervaIdentity.Name}",
                    HistoryMinervaIdentity.WorkerFileName);
                if (File.Exists(localPackage))
                    yield return localPackage;

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
                    var candidate = Path.Combine(
                        project,
                        $"z-{HistoryMinervaIdentity.Name}",
                        HistoryMinervaIdentity.WorkerFileName);
                    if (File.Exists(candidate))
                        yield return candidate;
                }

                yield break;
            }
        }
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
}
