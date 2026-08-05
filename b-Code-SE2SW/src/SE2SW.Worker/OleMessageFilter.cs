using System.Runtime.InteropServices;

namespace SE2SW.Worker;

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);

    [PreserveSig]
    int MessagePending(IntPtr callee, int tickCount, int pendingType);
}

internal sealed class OleMessageFilter : IOleMessageFilter, IDisposable
{
    private const int RetryLater = 2;
    private readonly int _retryBudgetMilliseconds;
    private readonly CancellationToken _cancellationToken;
    private IOleMessageFilter? _previous;

    private OleMessageFilter(TimeSpan retryBudget, CancellationToken cancellationToken)
    {
        _retryBudgetMilliseconds = checked((int)retryBudget.TotalMilliseconds);
        _cancellationToken = cancellationToken;
    }

    public static OleMessageFilter Register(TimeSpan retryBudget, CancellationToken cancellationToken)
    {
        var filter = new OleMessageFilter(retryBudget, cancellationToken);
        var result = CoRegisterMessageFilter(filter, out var previous);
        if (result < 0)
            throw new COMException("注册 OLE 消息过滤器失败。", result);
        filter._previous = previous;
        return filter;
    }

    public void Dispose()
    {
        CoRegisterMessageFilter(_previous, out _);
        _previous = null;
        GC.SuppressFinalize(this);
    }

    int IOleMessageFilter.HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) => 0;

    int IOleMessageFilter.RetryRejectedCall(IntPtr callee, int tickCount, int rejectType)
        => rejectType == RetryLater &&
           !_cancellationToken.IsCancellationRequested &&
           tickCount <= _retryBudgetMilliseconds
            ? 99
            : -1;

    int IOleMessageFilter.MessagePending(IntPtr callee, int tickCount, int pendingType) => 2;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(
        IOleMessageFilter? newFilter,
        out IOleMessageFilter? oldFilter);
}
