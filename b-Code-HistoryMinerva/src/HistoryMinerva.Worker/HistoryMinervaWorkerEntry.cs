namespace HistoryMinerva.Worker;

/// <summary>
/// 单一 Worker 的 STA 入口。只接受转换协议：
/// <c>&lt;verb&gt; &lt;json&gt; --cancel &lt;signal&gt;</c>。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
        => ConversionWorkerProgram.Run(args);
}
