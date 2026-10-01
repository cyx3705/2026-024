using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HistoryMinerva.Contracts;
using HistoryVulcan.Core.Modules;

namespace HistoryMinerva;

/// <summary>
/// Minerva 的运行期路径：数据目录与包目录都由宿主给（宿主 6.0.0 <c>IModuleEnvironment</c>），模块不推宿主运行区布局。
/// </summary>
public sealed class MappingRuntimePaths
{
    private const int MinimumNumberedProjects = 2;
    private static readonly Regex NumberedProjectName =
        new(@"^\d{4}-\d{3}-.+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public MappingRuntimePaths(string moduleDataDirectory, string? packageDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDataDirectory);
        ModuleDataDirectory = Path.GetFullPath(moduleDataDirectory);
        PackageDirectory = string.IsNullOrWhiteSpace(packageDirectory) ? null : Path.GetFullPath(packageDirectory);
    }

    /// <summary>可写数据目录（宿主给的 <c>ModuleData\HistoryMinerva</c>）。</summary>
    public string ModuleDataDirectory { get; }

    /// <summary>本模块的包槽位；未接入宿主时为空。Worker 随包发布在这里。</summary>
    public string? PackageDirectory { get; }

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
        // 装在宿主里：宿主从内存流装程序集，Assembly.Location 为空，Worker 只能从包目录找。
        if (PackageDirectory is not null)
            candidates.Add(Path.Combine(PackageDirectory, HistoryMinervaIdentity.WorkerFileName));
        // 开发与测试：程序集从磁盘装载，Worker 构建在旁边。
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            candidates.Add(Path.Combine(Path.GetDirectoryName(assemblyLocation)!, HistoryMinervaIdentity.WorkerFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, HistoryMinervaIdentity.WorkerFileName));
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

                if (!LooksLikeLibraryRoot(directory.FullName))
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

    /// <summary>未接入宿主时（界面设计器、无参构造）用的临时数据目录；接入后由 <see cref="FromEnvironment"/> 取代。</summary>
    public static MappingRuntimePaths Unattached()
        => new(Path.Combine(Path.GetTempPath(), HistoryMinervaIdentity.Name));

    /// <summary>接入宿主后的路径：数据目录与包目录都取宿主给的值。</summary>
    public static MappingRuntimePaths FromEnvironment(IModuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new MappingRuntimePaths(environment.DataDirectory, environment.PackageDirectory);
    }
}
