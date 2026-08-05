using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MultiInstanceProbe;

/// <summary>
/// 实测 SolidWorks 能否在同一台机器上并存多个可自动化的实例。
///
/// 背景：生产代码全部用 <c>Activator.CreateInstance(ProgID)</c> 取实例，而它一定
/// 附着到已有会话——这是事实。但由此推出"机器上开不了第二个"是错的：
/// SolidWorks 每个实例都以 PID 注册进 ROT，可用
/// <c>!SldWorks.Application:&lt;pid&gt;</c> 这个 moniker 绑定到指定实例。
///
/// 本探针只回答四个问题，不改任何项目文件：
///   1. 能否同时起 N 个 SLDWORKS.exe 并各自按 PID 绑定成功
///   2. 授权是否允许同机多会话（起不来会在这一步失败）
///   3. 每个实例的启动墙钟与内存开销
///   4. FeatureWorks 能否在每个实例里各自加载
///
/// 只读：不打开任何文档、不保存、不碰 CAD 之外的东西。自己起的进程自己收干净。
/// </summary>
internal static class Program
{
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var wanted = 2;
        var probeFeatureWorks = true;
        var rotWatchSeconds = 0;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index] == "--instances") wanted = int.Parse(args[index + 1]);
            else if (args[index] == "--featureworks") probeFeatureWorks = bool.Parse(args[index + 1]);
            else if (args[index] == "--rot-watch-seconds") rotWatchSeconds = int.Parse(args[index + 1]);
        }

        if (rotWatchSeconds > 0)
            return RunRotWatch(rotWatchSeconds);

        var report = new Report { Requested = wanted };
        var preexisting = GetPids();
        report.PreexistingPids = preexisting;
        var owned = new List<Process>();
        try
        {
            var executable = ResolveExecutable();
            report.Executable = executable;
            if (executable is null || !File.Exists(executable))
                throw new FileNotFoundException("未从 COM 注册解析到 SLDWORKS.exe。", executable ?? "<null>");

            for (var index = 1; index <= wanted; index++)
            {
                var attempt = new InstanceFact { Ordinal = index };
                var stopwatch = Stopwatch.StartNew();
                Process? process = null;
                try
                {
                    // 直接起进程，绕开 COM 类工厂——这是拿到"第二个"的唯一办法。
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = false,
                    }) ?? throw new InvalidOperationException("Process.Start 返回 null。");
                    owned.Add(process);
                    attempt.Pid = process.Id;

                    // 关键判别：新进程是"自己退出了"（单实例交接）还是"活着但没注册 ROT"。
                    // 两者的含义完全不同，不记录就分不开。
                    var application = BindByPid(process, TimeSpan.FromSeconds(90), out var bindSeconds);
                    process.Refresh();
                    attempt.ChildExited = process.HasExited;
                    if (attempt.ChildExited)
                    {
                        attempt.ChildExitCode = SafeExitCode(process);
                        attempt.ChildExitSeconds = Math.Round(bindSeconds, 1);
                    }
                    attempt.BindSeconds = bindSeconds;
                    attempt.StartupSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (application is null)
                    {
                        attempt.Note = "按 PID 绑定超时——该实例没有把自己注册进 ROT。";
                        report.Instances.Add(attempt);
                        continue;
                    }

                    try
                    {
                        application.Visible = false;
                        application.UserControl = false;
                    }
                    catch (Exception ex)
                    {
                        attempt.Note = "设置不可见失败：" + ex.Message;
                    }

                    attempt.Revision = SafeGet(() => application.RevisionNumber());
                    attempt.Bound = attempt.Revision is not null;

                    // 各实例是不是真的互相独立：进程内 hWnd 应当各不相同。
                    attempt.WindowHandle = SafeGet(() => (application.IFrameObject() as Frame)?.GetHWndx64().ToString());

                    if (probeFeatureWorks)
                        attempt.FeatureWorks = ProbeFeatureWorks(application);

                    process.Refresh();
                    attempt.WorkingSetMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1);
                    report.Instances.Add(attempt);
                    Release(application);
                }
                catch (Exception ex)
                {
                    attempt.Note = "异常：" + ex.Message;
                    report.Instances.Add(attempt);
                }
            }

            var bound = report.Instances.Count(item => item.Bound);
            report.BoundCount = bound;
            report.DistinctWindowHandles = report.Instances
                .Select(item => item.WindowHandle)
                .Where(handle => !string.IsNullOrWhiteSpace(handle))
                .Distinct()
                .Count();
            report.HandoffExits = report.Instances.Count(item => item.ChildExited && !item.Bound);
            report.Verdict = bound >= 2
                ? $"同机可并存 {bound} 个可自动化实例（独立窗口句柄 {report.DistinctWindowHandles} 个）；"
                    + $"FeatureWorks 可用 {report.Instances.Count(item => item.FeatureWorks == "可用")} 个。"
                : report.HandoffExits > 0
                    ? $"只绑定成功 {bound} 个；{report.HandoffExits} 个新进程自行退出——"
                        + "这是 SolidWorks 的单实例交接（新进程把请求交给已有实例后退出），不是启动慢。"
                    : $"只绑定成功 {bound} 个，且新进程仍在运行却未注册 ROT——另有原因，需继续查。";
            report.Success = true;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.Error = ex.Message;
        }
        finally
        {
            // 自己起的进程自己收，绝不按进程名清理——用户可能开着自己的 SolidWorks。
            foreach (var process in owned)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(20000);
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }

            report.LeftoverPids = GetPids().Except(preexisting).ToArray();
        }

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        Console.Error.WriteLine(report.Verdict ?? report.Error);
        return report.Success ? 0 : 1;
    }

    /// <summary>
    /// 起一个 SolidWorks，边等边把运行对象表里与 SolidWorks 有关的条目全部列出来。
    /// 目的不是绑定，而是先看清楚它到底注册了什么名字——猜 moniker 格式是上一轮失败的原因。
    /// </summary>
    private static int RunRotWatch(int seconds)
    {
        var executable = ResolveExecutable();
        Console.WriteLine($"可执行文件：{executable}");
        if (executable is null || !File.Exists(executable))
            return 2;

        var before = GetPids();
        Console.WriteLine($"启动前 SW 进程：[{string.Join(",", before)}]");
        using var process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false })!;
        Console.WriteLine($"已启动 PID {process.Id}");

        var stopwatch = Stopwatch.StartNew();
        var lastDump = string.Empty;
        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(5000);
            process.Refresh();
            var alive = !process.HasExited;
            var memory = alive ? Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1) : 0;
            var entries = EnumerateRunningObjects()
                .Where(name => name.Contains("SldWorks", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SolidWorks", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var dump = string.Join(" | ", entries);
            Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds,5:F0}s] 存活={alive} 内存={memory}MB ROT命中={entries.Length}"
                + "  → " + (dump.Length == 0 ? "<无>" : dump));
            lastDump = dump;
            if (!alive)
            {
                Console.WriteLine($"进程已自行退出，退出码 {SafeExitCode(process)}");
                break;
            }
        }

        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(20000); } } catch { }
        Console.WriteLine($"收尾后残留 SW 进程：[{string.Join(",", GetPids().Except(before))}]");
        return 0;
    }

    /// <summary>枚举运行对象表的全部显示名。</summary>
    private static IEnumerable<string> EnumerateRunningObjects()
    {
        var names = new List<string>();
        IBindCtx? context = null;
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        try
        {
            if (CreateBindCtx(0, out context) != 0 || context is null)
                return names;
            context.GetRunningObjectTable(out table);
            table.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    monikers[0].GetDisplayName(context, null, out var display);
                    if (!string.IsNullOrWhiteSpace(display))
                        names.Add(display);
                }
                catch { }
            }
        }
        catch { }
        return names;
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    /// <summary>
    /// 按 PID 绑定到指定 SolidWorks 实例。SolidWorks 启动后才把自己注册进 ROT，
    /// 所以要轮询等待——冷启动可能要几十秒。
    /// </summary>
    /// <summary>
    /// 取指定 PID 的 SolidWorks 实例。
    ///
    /// **不用 BindToMoniker**：它会把字符串按文件 moniker 解析，而 SolidWorks 注册的
    /// <c>SolidWorks_PID_&lt;pid&gt;</c> 是直接放进 ROT 的 item moniker，解析不出来。
    /// 前两次失败都是在猜名字格式。这里改为枚举 ROT、按显示名匹配、直接 GetObject——
    /// 不需要知道任何格式约定。
    /// </summary>
    private static ISldWorks? BindByPid(Process process, TimeSpan timeout, out double seconds)
    {
        var wanted = $"SolidWorks_PID_{process.Id}";
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var found = GetRunningObject(wanted);
            if (found is ISldWorks application)
            {
                seconds = stopwatch.Elapsed.TotalSeconds;
                return application;
            }

            process.Refresh();
            if (process.HasExited)
                break;
            Thread.Sleep(500);
        }

        seconds = stopwatch.Elapsed.TotalSeconds;
        return null;
    }

    private static object? GetRunningObject(string displayName)
    {
        IBindCtx? context = null;
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        try
        {
            if (CreateBindCtx(0, out context) != 0 || context is null)
                return null;
            context.GetRunningObjectTable(out table);
            table.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    monikers[0].GetDisplayName(context, null, out var name);
                    if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    table.GetObject(monikers[0], out var instance);
                    return instance;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private static string ProbeFeatureWorks(ISldWorks application)
    {
        try
        {
            if (application.GetAddInObject(FeatureWorksProgId) is not null)
                return "可用";
            var path = ResolveFeatureWorksPath(application);
            if (path is null || !File.Exists(path))
                return "加载项文件未找到";
            var load = application.LoadAddIn(path);
            return application.GetAddInObject(FeatureWorksProgId) is not null
                ? "可用"
                : $"LoadAddIn={load} 但取不到自动化对象";
        }
        catch (Exception ex)
        {
            return "异常：" + ex.Message;
        }
    }

    /// <summary>
    /// FeatureWorks 加载项路径。
    ///
    /// **安装目录取自 LocalServer32 里的 sldworks.exe**，不要用
    /// <c>ISldWorks.GetExecutablePath()</c>——实测它拼出来会少一层 SOLIDWORKS 目录，
    /// 导致"加载项文件未找到"的假阴性。这个坑今天已经踩过一次了。
    /// </summary>
    private static string? ResolveFeatureWorksPath(ISldWorks application)
    {
        _ = application;
        const string classId = "{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}";
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId}\InprocServer32");
        if (key?.GetValue(null) is not string registered || string.IsNullOrWhiteSpace(registered))
            return null;
        var server = System.Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(server))
            return server;
        var executable = ResolveExecutable();
        var directory = string.IsNullOrWhiteSpace(executable) ? null : Path.GetDirectoryName(executable);
        return directory is null ? null : Path.GetFullPath(Path.Combine(directory, server));
    }

    /// <summary>从 COM 注册解析 SLDWORKS.exe，不硬编码安装路径。</summary>
    private static string? ResolveExecutable()
    {
        var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
        if (type is null)
            return null;
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{type.GUID}}}\LocalServer32");
        if (key?.GetValue(null) is not string server || string.IsNullOrWhiteSpace(server))
            return null;
        return System.Environment.ExpandEnvironmentVariables(server.Trim().Trim('"'));
    }

    private static T? SafeGet<T>(Func<T> getter) where T : class
    {
        try { return getter(); }
        catch { return null; }
    }

    private static int[] GetPids()
    {
        try { return Process.GetProcessesByName("SLDWORKS").Select(item => item.Id).ToArray(); }
        catch { return []; }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }
    }
}

internal sealed class Report
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Requested { get; set; }
    public int BoundCount { get; set; }
    public int DistinctWindowHandles { get; set; }
    public int HandoffExits { get; set; }
    public string? Executable { get; set; }
    public int[] PreexistingPids { get; set; } = [];
    public int[] LeftoverPids { get; set; } = [];
    public string? Verdict { get; set; }
    public List<InstanceFact> Instances { get; } = [];
}

internal sealed class InstanceFact
{
    public int Ordinal { get; set; }
    public int Pid { get; set; }
    public bool Bound { get; set; }
    public double StartupSeconds { get; set; }
    public double BindSeconds { get; set; }
    public double WorkingSetMb { get; set; }
    public string? Revision { get; set; }
    public string? WindowHandle { get; set; }
    public string? FeatureWorks { get; set; }
    public bool ChildExited { get; set; }
    public int? ChildExitCode { get; set; }
    public double ChildExitSeconds { get; set; }
    public string? Note { get; set; }
}
