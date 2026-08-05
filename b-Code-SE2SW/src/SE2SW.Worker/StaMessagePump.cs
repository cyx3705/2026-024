using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SE2SW.Worker;

internal static class StaMessagePump
{
    private const uint RemoveMessage = 1;

    public static void PumpAndWait(int milliseconds, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, RemoveMessage))
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            Thread.Sleep(20);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr hwnd,
        uint filterMin,
        uint filterMax,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
