using System.Text.Json;
using HistoryMinerva.Contracts;
using HistoryVulcan.Core.Commands;

namespace HistoryMinerva.Bom;

/// <summary>一种外购件的品牌查询结果。</summary>
/// <param name="Brand">品牌名；查不到、不能确定或查询失败时都是 <see cref="PurchasedBrandLookup.NotAvailable"/>。</param>
/// <param name="Failure">
/// 查询本身失败（HistoryApollo 没装、没配密钥、断网）时的原因。
/// 模型搜过之后确认查不到，是一个**答案**而不是失败，此时为 null。
/// </param>
internal sealed record BrandAnswer(string Brand, string? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// V4.10.4：整体打包时，按外购件规格联网查品牌，写进外购件清单的「备注[参考供应商]」。
///
/// **软依赖 HistoryApollo**：本模块不引用 Apollo 的任何程序集，只经命令总线发一行
/// <c>apollo.chat.ask web=true json=true</c>。Apollo 没装、没配密钥或断网时，总线回的是失败回执
/// 而不是异常——那一格写 N/A，打包照常完成。品牌只是给采购的参考，不值得为它让一整批
/// STEP 和工程图陪着失败。
/// </summary>
internal static class PurchasedBrandLookup
{
    /// <summary>查不到时写进表格与 BOM 的占位。</summary>
    public const string NotAvailable = "N/A";

    /// <summary>总线回显里的来源标签，与模块API「命令来源」一致。</summary>
    public const string CommandSource = "module:HistoryMinerva";

    private const int MaxBrandLength = 60;

    /// <summary>
    /// 「不要猜」写得这么重，是因为一个编出来的品牌比 N/A 更糟：采购会照着它去询价。
    /// GB / DIN / ISO 标准件点名说明，是因为模型最爱给「GB70 M8x20」这类件随手安一个大厂。
    /// 提示里必须出现 json 字样——DeepSeek 的 JSON 输出模式要求如此。
    /// </summary>
    private const string SystemPrompt =
        "你是工业采购助手，负责确认外购件的品牌（生产厂家）。先用 web_search 联网搜索规格型号，再根据搜索结果判断。"
        + "规则：只采信搜索结果里明确对应这个型号的品牌；GB、DIN、ISO 等国标或通用标准件没有特定品牌时视为查不到；"
        + "查不到或不能确定时 brand 写 N/A，绝对不要猜。"
        + "只输出一个 json 对象，例如 {\"brand\": \"SMC\"} 或 {\"brand\": \"N/A\"}，不要输出其他文字。";

    private static readonly HashSet<string> NotFoundWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "NA", "N.A.", "none", "null", "unknown", "-", "—",
        "无", "未知", "不详", "暂无", "无法确定", "查不到",
    };

    /// <summary>
    /// 规格为空的外购件不查：只凭「气缸」两个字搜出来的品牌没有意义，还白花一次计费调用。
    /// </summary>
    public static bool IsQueryable(PackagePartEntry entry)
        => entry.Category == PackagePartCategory.Purchased && entry.Specification.Trim().Length > 0;

    /// <summary>去重与缓存的键：同规格同名称的外购件只查一次。</summary>
    public static string Key(PackagePartEntry entry)
        => entry.Specification.Trim().ToUpperInvariant() + "\u001f" + entry.PartName.Trim();

    /// <summary>发给总线的那一行。参数值一律经 <see cref="CommandParser.QuoteArg"/> 编码，不含换行。</summary>
    public static string BuildCommand(PackagePartEntry entry)
        => "apollo.chat.ask web=true json=true maxtokens=300 timeout=120"
           + " system=" + CommandParser.QuoteArg(SystemPrompt)
           + " prompt=" + CommandParser.QuoteArg(BuildPrompt(entry));

    internal static string BuildPrompt(PackagePartEntry entry)
    {
        var specification = entry.Specification.Trim();
        var name = entry.PartName.Trim();
        return name.Length == 0
            ? $"外购件规格型号：{specification}。请联网确认它的品牌，按 json 输出。"
            : $"外购件规格型号：{specification}；名称：{name}。请联网确认它的品牌，按 json 输出。";
    }

    /// <summary>把 Apollo 的回执读成一个答案。</summary>
    public static BrandAnswer Read(CommandResult result)
    {
        if (!result.Success)
            return new BrandAnswer(NotAvailable, FirstLine(result.Message));

        // json=true 时回执正文就是模型答复本身，不带用量脚注（HistoryApollo 模块API）。
        try
        {
            using var document = JsonDocument.Parse(result.Message);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("brand", out var brand)
                   && brand.ValueKind == JsonValueKind.String
                ? new BrandAnswer(Normalize(brand.GetString()), null)
                : new BrandAnswer(NotAvailable, null);
        }
        catch (JsonException)
        {
            return new BrandAnswer(NotAvailable, "模型答复不是 JSON：" + FirstLine(result.Message));
        }
    }

    /// <summary>模型说「未知」「无」「n/a」都折成同一个 N/A，表格上不能出现五种写法的查不到。</summary>
    public static string Normalize(string? brand)
    {
        var text = (brand ?? string.Empty).Trim().Trim('"', '\'', '“', '”', '「', '」', '《', '》').Trim();
        if (text.Length == 0 || NotFoundWords.Contains(text))
            return NotAvailable;
        return text.Length <= MaxBrandLength ? text : text[..MaxBrandLength];
    }

    /// <summary>经宿主命令总线查询。</summary>
    public static Func<PackagePartEntry, CancellationToken, Task<BrandAnswer>> OverBus(CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return async (entry, cancellationToken) => Read(
            await bus.ExecuteAsync(BuildCommand(entry), CommandSource, cancellationToken).ConfigureAwait(false));
    }

    private static string FirstLine(string? message)
    {
        var line = (message ?? string.Empty).Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
