namespace HistoryMinerva.Worker;

/// <summary>
/// 合并后单个 Worker 的 STA 入口。两条既有协议按参数形态路由，协议本身不变：
/// HistoryMinerva（4 参数）：&lt;verb&gt; &lt;json&gt; --cancel &lt;signal&gt;；SWuse（2 参数）：--request &lt;json&gt;。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
        => args.Length == 2
            ? SWuse.Worker.Program.Run(args)
            : ConversionWorkerProgram.Run(args);
}
