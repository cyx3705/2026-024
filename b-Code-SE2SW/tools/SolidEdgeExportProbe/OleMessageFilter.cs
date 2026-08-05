using System.Runtime.InteropServices;

namespace SolidEdgeExportProbe;

/// <summary>
/// IOleMessageFilter 实现。等价于 Solid Edge 官方样例
/// <c>Custom\Batch\MessageFilter.vb</c>，但增加了总时限与取消支持：
/// 官方样例对 SERVERCALL_RETRYLATER 无条件返回 99ms 重试，会在无人值守批处理中无限挂起。
/// </summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

internal sealed class OleMessageFilter : IOleMessageFilter
{
    // dwRejectType
    private const int SERVERCALL_ISHANDLED = 0;
    private const int SERVERCALL_REJECTED = 1;
    private const int SERVERCALL_RETRYLATER = 2;

    // RetryRejectedCall 返回值语义
    private const int CANCEL_CALL = -1;      // 放弃调用，调用方收到 RPC_E_CALL_REJECTED
    private const int RETRY_IMMEDIATELY = 99; // 0..99 = 等待该毫秒数后重试

    // MessagePending
    private const int PENDINGMSG_WAITDEFPROCESS = 2;

    private readonly int _retryBudgetMs;
    private readonly CancellationToken _cancellation;
    private IOleMessageFilter? _previous;

    private OleMessageFilter(int retryBudgetMs, CancellationToken cancellation)
    {
        _retryBudgetMs = retryBudgetMs;
        _cancellation = cancellation;
    }

    public int RejectedCallCount { get; private set; }

    public int MaxObservedWaitMs { get; private set; }

    public bool GaveUp { get; private set; }

    /// <summary>必须在 STA 线程上调用。</summary>
    public static OleMessageFilter Register(int retryBudgetMs, CancellationToken cancellation)
    {
        var filter = new OleMessageFilter(retryBudgetMs, cancellation);
        int hr = CoRegisterMessageFilter(filter, out IOleMessageFilter? previous);
        if (hr < 0)
        {
            throw new COMException("CoRegisterMessageFilter failed.", hr);
        }

        filter._previous = previous;
        return filter;
    }

    public void Revoke()
    {
        // 恢复线程原有过滤器（通常为 null），不留下指向已释放对象的过滤器。
        CoRegisterMessageFilter(_previous, out _);
        _previous = null;
    }

    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        => SERVERCALL_ISHANDLED;

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        if (dwRejectType != SERVERCALL_RETRYLATER)
        {
            // SERVERCALL_REJECTED：服务器明确拒绝，重试无意义。
            GaveUp = true;
            return CANCEL_CALL;
        }

        RejectedCallCount++;
        if (dwTickCount > MaxObservedWaitMs)
        {
            MaxObservedWaitMs = dwTickCount;
        }

        // dwTickCount = 自调用发出以来的毫秒数，由 COM 运行时维护，用作重试预算。
        if (_cancellation.IsCancellationRequested || dwTickCount > _retryBudgetMs)
        {
            GaveUp = true;
            return CANCEL_CALL;
        }

        return RETRY_IMMEDIATELY;
    }

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        => PENDINGMSG_WAITDEFPROCESS;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(
        IOleMessageFilter? newFilter,
        out IOleMessageFilter? oldFilter);
}
