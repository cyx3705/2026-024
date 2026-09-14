using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// 一个编出来的品牌比 N/A 更糟：采购会照着它去询价，所以「不许凭型号长相猜」一直在。
    /// 4.10.5 放宽的是**证据**而不是猜测（DEC-065）：代理商、电商页把该型号或同系列写在某品牌下就算数——
    /// 4.10.4 只认「明确对应这个型号」，模型把 F-M10X125F 标着 AirTAC 的商品页也拒收了。
    /// 三步检索策略写死在这里，是因为模型自己往往只搜一次完整型号，而目录件的长型号在网页上几乎从不逐字出现。
    /// GB / DIN / ISO 标准件点名说明，是因为模型最爱给「GB70 M8x20」这类件随手安一个大厂。
    /// 提示里必须出现 json 字样——DeepSeek 的 JSON 输出模式要求如此。
    /// </summary>
    private const string SystemPrompt =
        "你是工业采购助手，负责确认外购件的品牌（生产厂家）。"
        + "搜索策略：先用 web_search 搜完整型号；结果里没有页面对应这个型号时，换成「型号 + 品类」再搜，品类参考所在文件夹名；"
        + "仍没有时，搜型号去掉尺寸参数后的系列代号加品类。"
        + "判断规则：只要有搜索结果把这个型号、或同一系列型号，明确标在某个品牌名下（官网、代理商、电商商品页写明品牌都算），就采信那个品牌；"
        + "找不到任何把型号或系列对应到品牌的页面时 brand 写 N/A，不要凭型号长得像就猜；GB、DIN、ISO 等通用标准件没有特定品牌时写 N/A。"
        + "品牌写厂家常用英文名，例如 SMC、AirTAC、MISUMI。"
        + "只输出一个 json 对象，例如 {\"brand\": \"SMC\"} 或 {\"brand\": \"N/A\"}，不要输出其他文字。";

    /// <summary>
    /// STEP 导入留在文件名尾巴上的东西：<c>_step</c>、<c>_stp</c>、<c>.STEP-1</c>、<c>(0_0)</c>、<c>_0_0_</c>。
    /// 它们进了搜索词，搜到的就是一堆模型下载站，而不是那个型号的商品页。
    /// </summary>
    private static readonly Regex ImportResidue = new(
        @"(\.step-\d+|[_\s.\-]+(step|stp|x_t|igs|iges)|\(\d+(_\d+)*\)|(_\d+){2,}_*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseText = new(
        @"[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]", RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NotFoundWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "NA", "N.A.", "none", "null", "unknown", "-", "—",
        "无", "未知", "不详", "暂无", "无法确定", "查不到",
    };

    /// <summary>
    /// 规格为空的外购件不查：只凭「气缸」两个字搜出来的品牌没有意义，还白花一次计费调用。
    /// </summary>
    public static bool IsQueryable(PackagePartEntry entry)
        => entry.Category == PackagePartCategory.Purchased && QuerySpecification(entry).Length > 0;

    /// <summary>去重与缓存的键：同查询词同名称的外购件只查一次。</summary>
    public static string Key(PackagePartEntry entry)
        => QuerySpecification(entry).ToUpperInvariant() + "\u001f" + entry.PartName.Trim();

    /// <summary>
    /// 搜索用的型号，从**文件主名**另行清洗，不复用 BOM 上的规格（DEC-065）。
    ///
    /// BOM 的规格按 <see cref="PurchasedPartNaming"/> 切，那是交付给采购的口径，不为搜索去改它；
    /// 但那条口径把全角括号当中文丢掉——<c>BNTB-M20（1.0）</c> 在 BOM 上是 <c>BNTB-M201.0</c>，
    /// 拿它去搜就是搜一个不存在的型号。这里先全角转半角，再剥导入残留，最后才去中文。
    /// </summary>
    internal static string QuerySpecification(PackagePartEntry entry)
    {
        var text = Path.GetFileNameWithoutExtension(entry.SourcePath ?? string.Empty).Normalize(NormalizationForm.FormKC);
        string previous;
        do
        {
            previous = text;
            text = ImportResidue.Replace(text, string.Empty).Trim(' ', '_', '-', '.');
        }
        while (!string.Equals(text, previous, StringComparison.Ordinal));

        text = ChineseText.Replace(text, " ");
        text = EmptyBrackets.Replace(text, " ");
        return Whitespace.Replace(text, " ").Trim(' ', '_', '-', '.');
    }

    /// <summary>
    /// 品类提示：外购件所在文件夹名（气缸、气动浮头、螺纹杆……）。现场按品类建文件夹，
    /// 这是模型手里唯一一条「这是个什么东西」的线索，一个光秃秃的型号它常常连搜什么品类都猜错。
    /// </summary>
    internal static string FolderCategory(PackagePartEntry entry)
        => Path.GetFileName(Path.GetDirectoryName(entry.SourcePath ?? string.Empty) ?? string.Empty) ?? string.Empty;

    /// <summary>发给总线的那一行。参数值一律经 <see cref="CommandParser.QuoteArg"/> 编码，不含换行。</summary>
    public static string BuildCommand(PackagePartEntry entry)
        => "apollo.chat.ask web=true json=true maxtokens=300 timeout=120"
           + " system=" + CommandParser.QuoteArg(SystemPrompt)
           + " prompt=" + CommandParser.QuoteArg(BuildPrompt(entry));

    internal static string BuildPrompt(PackagePartEntry entry)
    {
        var parts = new List<string> { $"外购件规格型号：{QuerySpecification(entry)}" };
        if (entry.PartName.Trim() is { Length: > 0 } name)
            parts.Add($"名称：{name}");
        if (FolderCategory(entry).Trim() is { Length: > 0 } folder)
            parts.Add($"品类（所在文件夹）：{folder}");
        return string.Join("；", parts) + "。请联网确认它的品牌，按 json 输出。";
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
