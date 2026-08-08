using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SE2SW.Worker;

internal sealed class CadProcessOwnership
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
    private readonly string _processName;
    private readonly HashSet<int> _before;

    private CadProcessOwnership(string processName)
    {
        _processName = processName;
        _before = GetProcessIds(processName);
    }

    public int OwnedProcessId { get; private set; }
    public bool OwnsInstance => OwnedProcessId > 0;
    public bool ForcedTerminationUsed { get; private set; }

    public static CadProcessOwnership Capture(string processName) => new(processName);

    public void Resolve(long windowHandle)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var after = GetProcessIds(_processName);
            var windowProcessId = 0;
            if (windowHandle != 0)
            {
                _ = GetWindowThreadProcessId(new IntPtr(windowHandle), out var resolvedProcessId);
                windowProcessId = checked((int)resolvedProcessId);
            }

            OwnedProcessId = ResolveOwnedProcessId(_before, after, windowProcessId);
            if (OwnsInstance)
                return;

            // 只有窗口句柄明确落在启动前 PID 上，才能证明 COM 确实附着了用户会话。
            // 无界面 SolidWorks 实测会在已有隐藏实例时再创建新进程，不能把“第一拍暂无新增”
            // 当成单实例证据，否则稍后出现的新 PID 会永久失去所有权。
            if (windowProcessId > 0 && _before.Contains(windowProcessId) && after.Contains(windowProcessId))
                return;
            if (stopwatch.Elapsed >= ResolveTimeout)
                return;

            // SolidWorks 的 COM 工厂可能先返回 RCW，SLDWORKS.exe 稍后才进入进程表；
            // 单次快照会把我们刚启动的会话误判成用户会话并永久遗留。
            Thread.Sleep(100);
        }
    }

    internal static int ResolveOwnedProcessId(
        IReadOnlySet<int> before,
        IReadOnlyCollection<int> after,
        int windowProcessId)
    {
        if (windowProcessId > 0 && !before.Contains(windowProcessId) && after.Contains(windowProcessId))
            return windowProcessId;

        var newProcessIds = after.Where(processId => !before.Contains(processId)).ToArray();
        return newProcessIds.Length == 1 ? newProcessIds[0] : 0;
    }

    public bool WaitForOwnedExit(TimeSpan timeout)
    {
        if (!OwnsInstance)
            return true;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(OwnedProcessId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    public bool EnsureOwnedExit(TimeSpan gracefulTimeout, TimeSpan forcedTimeout)
    {
        if (WaitForOwnedExit(gracefulTimeout))
            return true;

        try
        {
            using var process = Process.GetProcessById(OwnedProcessId);
            // OwnedProcessId 只会来自启动前后 PID 差集或明确的窗口句柄映射。
            // 此处绝不能退化为按进程名清理，否则会伤及用户原有 CAD 会话。
            process.Kill(entireProcessTree: true);
            ForcedTerminationUsed = true;
            return process.WaitForExit(checked((int)forcedTimeout.TotalMilliseconds));
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
                result.Add(process.Id);
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
