using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoryMinerva;

/// <summary>
/// 整体打包的本机开关（V4.10.5，DEC-066）。目前只有「AI 查品牌」一项。
///
/// 落在模块数据目录而不是页面状态里：页面实例随热重载重建，开关要记住的正是用户上一次的选择。
/// 读不出来一律回到默认开启——一份坏掉的配置文件不能让打包页打不开。
/// </summary>
internal static class PackageOptions
{
    public const string FileName = "package-options.json";

    private const string BrandLookupKey = "brandLookup";

    public static string PathIn(string moduleDataDirectory) => Path.Combine(moduleDataDirectory, FileName);

    /// <summary>读「AI 查品牌」开关；文件不在、不可读或内容不对时为开启。</summary>
    public static bool LoadBrandLookup(string path)
    {
        try
        {
            if (!File.Exists(path))
                return true;
            return JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                   || root[BrandLookupKey] is not JsonValue value
                   || !value.TryGetValue<bool>(out var enabled)
                   || enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    /// <summary>写「AI 查品牌」开关。先写临时文件再替换，写到一半断电不会留下半个 JSON。</summary>
    public static void SaveBrandLookup(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { [BrandLookupKey] = enabled }.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }
}
