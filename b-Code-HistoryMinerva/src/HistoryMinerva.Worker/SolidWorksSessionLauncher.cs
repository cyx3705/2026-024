using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// 启动一个**专属**的 SolidWorks 进程并按 PID 绑定它。
///
/// 为什么需要它：<c>Activator.CreateInstance</c> 只会附着到已在运行的 SolidWorks。
/// 逐零件子 Worker 全都这么做，于是共用同一个进程——那个进程里的 FeatureWorks 一旦
/// 崩掉（<c>RPC_E_SERVERFAULT</c>），后面每个子 Worker 拿到的都是同一具尸体，
/// 重试多少次都没用。现场实测：跑到第 14 个零件 FeatureWorks 崩溃，
/// 剩下 39 个零件全部退化成哑实体。
///
/// 会话崩溃后改用专属进程重试，就把"一个复杂零件搞崩了整批"变成"只坏这一个"。
///
/// 绝不按进程名清理：只 <c>ExitApp</c> 自己启动的那个 PID，收不掉就报告，留给人处置。
/// </summary>
internal static class SolidWorksSessionLauncher
{
    private const string ProgId = "SldWorks.Application";
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(180);

    /// <summary>启动并绑定一个专属实例。返回 COM 对象与其 PID。</summary>
    public static (object Application, int ProcessId) StartDedicated(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable()
            ?? throw new ClassifiedConversionException(
                ConversionErrorClass.ComNotRegistered,
                "未能从 COM 注册解析出 sldworks.exe，无法启动专属 SolidWorks 会话。");

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false })
                ?? throw new InvalidOperationException("Process.Start 返回 null。");
            var application = BindByProcessId(process, BindTimeout, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"启动了 SolidWorks（PID {process.Id}）但在 {BindTimeout.TotalSeconds:F0} 秒内没能绑定它的 COM 对象。");
            return (application, process.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ClassifiedConversionException)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.AppLaunchFailed,
                "启动专属 SolidWorks 会话失败：" + ex.Message,
                ex);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 从运行对象表里按 <c>SolidWorks_PID_&lt;pid&gt;</c> 绑定。
    /// 不能用 <c>BindToMoniker</c>——它解析不了 ROT 里的 item moniker，必须枚举后 GetObject。
    /// </summary>
    private static object? BindByProcessId(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var wanted = $"SolidWorks_PID_{process.Id}";
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (CreateBindCtx(0, out var context) == 0 && context is not null)
                {
                    context.GetRunningObjectTable(out var table);
                    IEnumMoniker? enumerator = null;
                    table?.EnumRunning(out enumerator);
                    var monikers = new IMoniker[1];
                    while (table is not null
                           && enumerator is not null
                           && enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                    {
                        monikers[0].GetDisplayName(context, null, out var name);
                        if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                            continue;
                        table.GetObject(monikers[0], out var instance);
                        if (instance is not null)
                            return instance;
                    }
                }
            }
            catch
            {
                // ROT 在 SolidWorks 启动过程中可能瞬时不可读，继续等。
            }

            process.Refresh();
            if (process.HasExited)
                return null;
            Thread.Sleep(500);
        }

        return null;
    }

    /// <summary>安装路径取自 COM 注册的 LocalServer32，不要猜目录。</summary>
    private static string? ResolveExecutable()
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
            return null;
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
            "CLSID\\{" + type.GUID + "}\\LocalServer32");
        if (key?.GetValue(null) is not string server || string.IsNullOrWhiteSpace(server))
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(server.Trim().Trim('"'));
        return File.Exists(expanded) ? expanded : null;
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);
}
