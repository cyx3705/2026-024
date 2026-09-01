using System.Globalization;
using System.IO;
using System.Xml.Linq;
using HistoryMinerva.Contracts;
using Microsoft.Win32;

namespace HistoryMinerva;

/// <summary>用户在 SolidWorks 材料对话框里收藏的一种材质。</summary>
/// <param name="Label">下拉里显示的文字。重名时会带上材料库以示区分，所以它未必等于 <paramref name="Name"/>。</param>
/// <param name="Database">材料库，原样来自注册表：库名或 <c>.sldmat</c> 全路径。</param>
/// <param name="Name">材质名，写进模型时用的就是它。</param>
public readonly record struct FavoriteMaterial(string Label, string Database, string Name);

/// <summary>
/// 三个属性下拉的候选从哪来。
///
/// **为什么不写死在代码里**：这三栏在 SolidWorks 里都不是自由文本——
/// 「表面处理」「热处理」是属性标签模板里的 <c>ComboBox</c>，候选写在模板的
/// <c>&lt;Data SourceType="List"&gt;</c> 里；「材料」接的是材质库，用户收藏了哪几种就该出哪几种。
/// 把候选抄进代码，用户改一次模板、收藏一次新材料，这里就开始说假话，而且是**静默**说假话：
/// 选出来的值照样写得进去，只是和图框、明细表对不上。
///
/// 两处来源都记在当前用户的 SolidWorks 系统选项里，运行时读注册表拿到，
/// 因此仓库里没有任何一条本机路径。读不到就退回 <see cref="PartPropertyNames"/> 里的兜底副本，
/// 页面照常能开——没装 SolidWorks 的机器也得能把这一页画出来。
///
/// 候选按轮缓存：**解析装配体时认一次**，之后整轮整备都用这一份。
/// V4.8 之前每点开一格单元格、每展开一次下拉都要重读注册表并解析模板目录里全部
/// `.prtprp`，一张几百行的表就是几百次同样的磁盘往返，而且全落在 UI 线程上——
/// 那正是「改一格卡一下」的一半来源。用户改了模板或收藏，重新解析一次装配体即生效，
/// 仍然不需要重启宿主。
/// </summary>
internal static class SolidWorksPropertyOptions
{
    private const string SolidWorksRoot = @"Software\SolidWorks";
    private const string VersionKeyPrefix = "SOLIDWORKS ";

    private static IReadOnlyList<FavoriteMaterial>? _materials;
    private static IReadOnlyList<string>? _surfaceTreatments;
    private static IReadOnlyList<string>? _heatTreatments;

    /// <summary>
    /// 丢掉上一轮的候选，就地重新认一遍机器上的真实来源。
    ///
    /// 由解析装配体触发：那是唯一一个"用户刚从 SolidWorks 那边回来"的时刻，
    /// 也是重读几个文件的代价还看不出来的时刻——它跑在探查那条后台线程上，
    /// 而每点开一格就重读一次是跑在 UI 线程上的。
    /// </summary>
    public static void Refresh()
    {
        _materials = null;
        _surfaceTreatments = null;
        _heatTreatments = null;
        _ = Materials();
        _ = SurfaceTreatments();
        _ = HeatTreatments();
    }

    /// <summary>收藏材料。读不到时返回空表——材料没有兜底候选，凭空编一个材质名比不给更糟。</summary>
    public static IReadOnlyList<FavoriteMaterial> Materials()
    {
        if (_materials is not null)
            return _materials;
        try
        {
            return _materials = ReadFavoriteMaterials();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return _materials = [];
        }
    }

    /// <summary>「表面处理」候选。</summary>
    public static IReadOnlyList<string> SurfaceTreatments()
        => _surfaceTreatments ??= ComboOptions(
            PartPropertyNames.SurfaceTreatment, PartPropertyNames.FallbackSurfaceTreatments);

    /// <summary>「热处理」候选。</summary>
    public static IReadOnlyList<string> HeatTreatments()
        => _heatTreatments ??= ComboOptions(
            PartPropertyNames.HeatTreatment, PartPropertyNames.FallbackHeatTreatments);

    /// <summary>
    /// 当前用户装的最新一版 SolidWorks 的注册表键名。
    ///
    /// 不能直接按名字倒序取第一个：这一层下面还有 <c>SOLIDWORKS CAM</c> 这种同前缀的兄弟键，
    /// 字符串序里 <c>C</c> 排在 <c>2</c> 后面，取到的会是 CAM 而不是 2025。只认后缀是四位年份的。
    /// </summary>
    private static string? ResolveVersionKey()
    {
        using var root = Registry.CurrentUser.OpenSubKey(SolidWorksRoot);
        if (root is null)
            return null;

        string? best = null;
        var bestYear = 0;
        foreach (var name in root.GetSubKeyNames())
        {
            if (!name.StartsWith(VersionKeyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = name[VersionKeyPrefix.Length..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 2000)
                continue;
            if (year <= bestYear)
                continue;
            bestYear = year;
            best = name;
        }

        return best;
    }

    private static IReadOnlyList<FavoriteMaterial> ReadFavoriteMaterials()
    {
        var version = ResolveVersionKey();
        if (version is null)
            return [];

        using var key = Registry.CurrentUser.OpenSubKey($@"{SolidWorksRoot}\{version}\Material");
        if (key is null)
            return [];

        // __NumOfFavs 是当前收藏的条数。_FavMaterialN 会残留超出这个数的旧条目，
        // 全读进来就会把用户早就取消收藏的材质又摆回下拉里。
        var count = key.GetValue("__NumOfFavs") switch
        {
            int value => value,
            string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        var entries = new List<(string Database, string Name)>();
        for (var index = 1; index <= count; index++)
        {
            if (key.GetValue($"_FavMaterial{index}") is not string raw)
                continue;
            // 形如 "库|材质名" 或 "库|材质名|序号"，材质名本身不含竖线。
            var parts = raw.Split('|');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
                continue;
            entries.Add((parts[0], parts[1]));
        }

        // 同名材质可以分属不同材料库（自建库常照抄标准库的名字）。重名的才带上库名，
        // 不重名的保持干净——大多数情况下用户只想看见「304不锈钢」。
        var duplicated = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return entries
            .Select(entry => new FavoriteMaterial(
                duplicated.Contains(entry.Name)
                    ? $"{entry.Name}（{DatabaseLabel(entry.Database)}）"
                    : entry.Name,
                entry.Database,
                entry.Name))
            .ToArray();
    }

    /// <summary>材料库拿来当区分用的短名：全路径只留文件名，库名原样。</summary>
    private static string DatabaseLabel(string database)
        => database.Contains('/') || database.Contains('\\')
            ? Path.GetFileNameWithoutExtension(database)
            : database;

    private static IReadOnlyList<string> ComboOptions(string propertyName, IReadOnlyList<string> fallback)
    {
        try
        {
            var parsed = ReadTemplateOptions(propertyName);
            return parsed.Count == 0 ? fallback : parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or System.Xml.XmlException)
        {
            return fallback;
        }
    }

    private static IReadOnlyList<string> ReadTemplateOptions(string propertyName)
    {
        var version = ResolveVersionKey();
        if (version is null)
            return [];

        using var key = Registry.CurrentUser.OpenSubKey($@"{SolidWorksRoot}\{version}\ExtReferences");
        if (key?.GetValue("Custom Property Folders") is not string folders)
            return [];

        var options = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // SolidWorks 允许配多个模板目录，分号隔开。
        foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.prtprp"))
                CollectOptions(file, propertyName, options, seen);
        }

        return options;
    }

    private static void CollectOptions(string file, string propertyName, List<string> options, HashSet<string> seen)
    {
        var document = XDocument.Load(file);
        foreach (var control in document.Descendants("Control"))
        {
            // 按 PropName 而不是 Label 找：这两个在模板里是分开的字段，可以不同。
            if (!string.Equals((string?)control.Attribute("PropName"), propertyName, StringComparison.Ordinal))
                continue;
            foreach (var item in control.Elements("Data").Elements("Item"))
            {
                var value = item.Value.Trim();
                if (value.Length != 0 && seen.Add(value))
                    options.Add(value);
            }
        }
    }
}
