using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoryMinerva;

/// <summary>
/// 整体打包的分步开关（V4.16）：打包拆成六步，每一步都能单独关掉。全开就是 V4.15 的整包。
///
/// 关掉一步就是「这一轮不做」：不写、不删、不碰上一轮留下的同名产物——用户只想补导几张 PDF 时，
/// 包里已经发给采购的清单不该被顺手改掉。
/// </summary>
/// <param name="MachinedBom">机加件清单（含同名截图）。</param>
/// <param name="PurchasedBom">外购件清单（含同名截图）。</param>
/// <param name="BrandLookup">AI 查品牌：联网查外购件品牌，名称为空的顺带查名称（V4.16）。</param>
/// <param name="Step">机加件 STEP。</param>
/// <param name="Dwg">工程图 DWG。</param>
/// <param name="Pdf">工程图 PDF。</param>
internal sealed record PackageSteps(
    bool MachinedBom = true,
    bool PurchasedBom = true,
    bool BrandLookup = true,
    bool Step = true,
    bool Dwg = true,
    bool Pdf = true)
{
    public static PackageSteps All { get; } = new();

    /// <summary>要不要起 Worker 做 CAD 导出。</summary>
    public bool AnyCadExport => Step || Dwg || Pdf;

    /// <summary>六步全关时没有可做的事，打包按钮不可用。</summary>
    public bool AnyStep => MachinedBom || PurchasedBom || BrandLookup || AnyCadExport;
}

/// <summary>
/// 打包开关的本机存档（V4.10.5 起，DEC-066；V4.16 扩成六步）。
///
/// 落在模块数据目录而不是页面状态里：页面实例随热重载重建，开关要记住的正是用户上一次的选择。
/// 读不出来的项一律回到默认开启——一份坏掉的配置文件不能让打包页打不开；
/// 旧文件只有 <c>brandLookup</c> 一项，其余按开启读。
/// </summary>
internal static class PackageOptions
{
    public const string FileName = "package-options.json";

    /// <summary>开关名 → 存档键。开关名也是 <c>minerva.ui.options option=</c> 认的名字。</summary>
    public static IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["machinedbom"] = "machinedBom",
        ["purchasedbom"] = "purchasedBom",
        ["brandai"] = "brandLookup",
        ["step"] = "step",
        ["dwg"] = "dwg",
        ["pdf"] = "pdf",
    };

    public static string PathIn(string moduleDataDirectory) => Path.Combine(moduleDataDirectory, FileName);

    /// <summary>读六个开关；文件不在、不可读或某项不对时那一项为开启。</summary>
    public static PackageSteps Load(string path)
    {
        JsonObject? root = null;
        try
        {
            if (File.Exists(path))
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        bool Read(string option)
            => root?[Keys[option]] is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || enabled;

        return new PackageSteps(
            Read("machinedbom"), Read("purchasedbom"), Read("brandai"), Read("step"), Read("dwg"), Read("pdf"));
    }

    /// <summary>写六个开关。先写临时文件再替换，写到一半断电不会留下半个 JSON。</summary>
    public static void Save(string path, PackageSteps steps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var root = new JsonObject();
        foreach (var (option, key) in Keys)
            root[key] = Get(steps, option);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>按开关名取值。</summary>
    public static bool Get(PackageSteps steps, string option) => option.ToLowerInvariant() switch
    {
        "machinedbom" => steps.MachinedBom,
        "purchasedbom" => steps.PurchasedBom,
        "brandai" => steps.BrandLookup,
        "step" => steps.Step,
        "dwg" => steps.Dwg,
        "pdf" => steps.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "未知打包开关"),
    };

    /// <summary>按开关名改一项。</summary>
    public static PackageSteps With(PackageSteps steps, string option, bool enabled) => option.ToLowerInvariant() switch
    {
        "machinedbom" => steps with { MachinedBom = enabled },
        "purchasedbom" => steps with { PurchasedBom = enabled },
        "brandai" => steps with { BrandLookup = enabled },
        "step" => steps with { Step = enabled },
        "dwg" => steps with { Dwg = enabled },
        "pdf" => steps with { Pdf = enabled },
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "未知打包开关"),
    };
}
