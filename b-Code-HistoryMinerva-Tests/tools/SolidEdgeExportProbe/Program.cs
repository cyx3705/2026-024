using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolidEdgeExportProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        ProbeOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C：只请求取消，让清理路径跑完。
            e.Cancel = true;
            cts.Cancel();
        };

        if (options.SelfCancelAfterMs > 0)
        {
            // SE-10 用：无需人工按 Ctrl+C 即可复现取消路径。
            cts.CancelAfter(options.SelfCancelAfterMs);
        }

        var exporter = new SolidEdgeExporter(options, cts.Token);
        ProbeResult result = exporter.Run();

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return result.Success ? 0 : 1;
    }

    private const string Usage = """
        用法:
          SolidEdgeExportProbe --input <绝对 .par 路径> --output <绝对 .x_t 路径> [选项]

        选项:
          --method saveas|savebody   导出方式，默认 savebody
          --parasolid-version <n>    0=seParasolidCurrentVersion，或 70/80/.../130，默认 0
          --binary                   导出二进制 Parasolid（默认文本）
          --retry-budget-ms <n>      COM 调用被拒绝时的重试总预算，默认 60000
          --stage-timeout-ms <n>     单阶段超时，默认 180000
          --keep-app-alive           不退出本探针创建的 Solid Edge 实例（调试用）
          --self-cancel-after-ms <n> 指定毫秒后自动触发取消，用于复现 SE-10

        退出码: 0 成功 / 1 探针失败 / 2 参数错误
        """;

    private static ProbeOptions ParseArgs(string[] args)
    {
        var options = new ProbeOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input":
                    options.InputPath = NextValue(args, ref i);
                    break;
                case "--output":
                    options.OutputPath = NextValue(args, ref i);
                    break;
                case "--method":
                    string method = NextValue(args, ref i);
                    options.Method = method.ToLowerInvariant() switch
                    {
                        "saveas" => ExportMethod.SaveAs,
                        "savebody" => ExportMethod.SaveBody,
                        _ => throw new ArgumentException($"未知的 --method: {method}"),
                    };
                    break;
                case "--parasolid-version":
                    options.ParasolidVersion = int.Parse(NextValue(args, ref i));
                    break;
                case "--binary":
                    options.Binary = true;
                    break;
                case "--retry-budget-ms":
                    options.RetryBudgetMs = int.Parse(NextValue(args, ref i));
                    break;
                case "--stage-timeout-ms":
                    options.StageTimeoutMs = int.Parse(NextValue(args, ref i));
                    break;
                case "--keep-app-alive":
                    options.KeepAppAlive = true;
                    break;
                case "--self-cancel-after-ms":
                    options.SelfCancelAfterMs = int.Parse(NextValue(args, ref i));
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.InputPath) || string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new ArgumentException("--input 与 --output 均为必填。");
        }

        return options;
    }

    private static string NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[index]} 缺少取值。");
        }

        return args[++index];
    }
}
