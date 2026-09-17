using System.Security.Cryptography;
using System.Text;

namespace HistoryMinerva.Contracts;

/// <summary>
/// V4.10：把一次装配探查结果算成整体打包计划。
///
/// 四件事在这里定死，之后的界面、导出与 BOM 都只是照做：
///
/// <list type="number">
///   <item><b>谁进清单</b>：探查已经把与总装同级的零件与子装配体收进 <see cref="AssemblyProbeResult.Occurrences"/>，
///         把子文件夹里的外购件收进 <see cref="AssemblyProbeResult.PurchasedParts"/>。</item>
///   <item><b>件别</b>（V4.11）：缺省按路径推出，用户在表里改写过的以用户的为准（<see cref="PartKinds.Resolve"/>）。
///         自制子装配体（机加件）照常展开，自己只占一行不进 BOM；外购子装配体整体算一种货，
///         里面的件全部不进清单；参考件连同其内部件整个排除。</item>
///   <item><b>数量</b>：整个总装配体里的实例总数，含嵌套倍数（子装配用两次、里面三个同款件＝6），
///         抑制件不计。</item>
///   <item><b>图号与名称</b>：按文件名第一个空格切，与改名管线同一条口径
///         （<see cref="DrawingNumber.SplitFileName"/>）。BOM 描述的是此刻磁盘上的事实，
///         不重新编号——重新编号得到的是「改完名之后的图号」，而文件还没改。</item>
/// </list>
/// </summary>
public static class PackagePlanner
{
    /// <param name="kinds">
    /// V4.11：用户改写过的件别，按源文件全路径记账。没记的按路径取缺省。
    /// </param>
    public static PackagePlan Create(
        AssemblyProbeResult probe,
        IReadOnlyDictionary<string, PackagePartCategory>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var issues = new List<string>();
        var warnings = new List<string>();

        if (!Path.IsPathFullyQualified(probe.SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(
                probe.SourceAssemblyPath, ConversionPathLayout.SolidWorksAssemblyExtension))
        {
            issues.Add("整体打包只接受绝对路径的 SolidWorks .SLDASM 总装配体。");
            return Empty(probe.SourceAssemblyPath, issues);
        }

        var rootPath = Path.GetFullPath(probe.SourceAssemblyPath);
        var rootDirectory = Path.GetDirectoryName(rootPath);
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            issues.Add("无法解析所选总装配体所在目录。");
            return Empty(rootPath, issues);
        }

        var tally = new Tally();
        CountOccurrences(probe, rootPath, kinds, tally, warnings);
        CountPurchasedReadings(probe, rootPath, kinds, tally, warnings);

        var entries = tally.ToEntries();
        var plan = new PackagePlan(
            rootPath,
            ConversionPathLayout.ResolvePackageDirectories(rootDirectory, ResolveBomNamePrefix(rootPath)),
            ResolveBomNamePrefix(rootPath),
            entries,
            issues,
            []);

        if (plan.Machined.Count == 0 && plan.Purchased.Count == 0)
            issues.Add("总装配体里没有识别到任何机加件或外购件，没有可打包的内容。");

        var missingDrawings = plan.Machined.Count(entry => !entry.HasDrawing);
        if (missingDrawings > 0)
            warnings.Add($"{missingDrawings} 个机加件没有同名工程图，图纸目录里只有它们的 STEP。");

        warnings.AddRange(probe.Warnings);
        return plan with { BlockingIssues = issues, Warnings = Distinct(warnings) };
    }

    /// <summary>
    /// 与总装同级的零件与子装配体。探查侧已经把子文件夹外购件及其内部件排除在
    /// <see cref="AssemblyProbeResult.Occurrences"/> 之外，这里只按件别再筛一遍。
    ///
    /// 祖先按 <see cref="AssemblyOccurrence.OccurrenceId"/> 的 <c>/</c> 前缀逐级查：
    /// 任何一级祖先不是机加件（被设成外购件或参考），这个实例就不再单独出现。
    /// </summary>
    private static void CountOccurrences(
        AssemblyProbeResult probe,
        string rootPath,
        IReadOnlyDictionary<string, PackagePartCategory>? kinds,
        Tally tally,
        List<string> warnings)
    {
        var pathById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in probe.Occurrences)
        {
            if (!string.IsNullOrEmpty(occurrence.OccurrenceId)
                && Path.IsPathFullyQualified(occurrence.SourcePath))
            {
                pathById[occurrence.OccurrenceId] = Path.GetFullPath(occurrence.SourcePath);
            }
        }

        foreach (var occurrence in probe.Occurrences)
        {
            if (occurrence.IsSuppressed || !Path.IsPathFullyQualified(occurrence.SourcePath))
                continue;
            var path = Path.GetFullPath(occurrence.SourcePath);
            var isAssembly = occurrence.IsSubAssembly
                || ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksAssemblyExtension);
            if (!isAssembly && !ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksPartExtension))
                continue;
            if (HasExcludedAncestor(occurrence.OccurrenceId, pathById, kinds, rootPath))
                continue;

            var kind = PartKinds.Resolve(kinds, path, rootPath);
            if (kind == PackagePartCategory.Reference)
            {
                tally.Add(path, kind, isAssembly, counted: false);
                continue;
            }

            if (!File.Exists(path))
            {
                warnings.Add($"跳过未解析的引用：{path}");
                continue;
            }

            tally.Add(path, kind, isAssembly, counted: true);
        }
    }

    private static bool HasExcludedAncestor(
        string occurrenceId,
        IReadOnlyDictionary<string, string> pathById,
        IReadOnlyDictionary<string, PackagePartCategory>? kinds,
        string rootPath)
    {
        if (string.IsNullOrEmpty(occurrenceId))
            return false;
        for (var separator = occurrenceId.IndexOf('/', StringComparison.Ordinal);
             separator >= 0;
             separator = occurrenceId.IndexOf('/', separator + 1))
        {
            if (pathById.TryGetValue(occurrenceId[..separator], out var ancestor)
                && PartKinds.Resolve(kinds, ancestor, rootPath) != PackagePartCategory.Machined)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 子文件夹里的外购件。数量由探查侧数好带过来——那一遍展平已经走过全部后代实例，
    /// 而这里连文件都不该打开（外购件很可能根本不在本机上）。
    /// 用户把它改成机加件时，它照样按文件名拆图号、导 STEP、找同名工程图。
    /// </summary>
    private static void CountPurchasedReadings(
        AssemblyProbeResult probe,
        string rootPath,
        IReadOnlyDictionary<string, PackagePartCategory>? kinds,
        Tally tally,
        List<string> warnings)
    {
        if (probe.PurchasedParts is not { Count: > 0 } readings)
            return;

        foreach (var reading in readings)
        {
            if (reading.InstanceCount <= 0 || string.IsNullOrWhiteSpace(reading.SourcePath))
                continue;
            if (!PartKinds.TryFullPath(reading.SourcePath, out var path))
            {
                warnings.Add($"跳过路径无法解析的外购件：{reading.SourcePath}");
                continue;
            }

            // 探查结果带进来的参考部件目录件：缺省就是参考，照样只作为可改件别的一行。
            var isAssembly = ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksAssemblyExtension);
            var kind = PartKinds.Resolve(kinds, path, rootPath);
            if (kind == PackagePartCategory.Machined && !PartKinds.CanBeMachined(path, rootPath))
            {
                warnings.Add($"子文件夹里的装配体不能当自制组件，仍按外购件处理：{path}");
                kind = PackagePartCategory.Purchased;
            }

            tally.Add(path, kind, isAssembly, counted: kind != PackagePartCategory.Reference, reading.InstanceCount);
        }
    }

    private static PackagePartEntry CreateEntry(
        string sourcePath,
        int quantity,
        PackagePartCategory category,
        bool isAssembly)
    {
        var fileName = Path.GetFileName(sourcePath);
        DrawingNumber.SplitFileName(fileName, out var drawingNumber, out var partName);
        var specification = string.Empty;
        if (category != PackagePartCategory.Machined)
        {
            // 外购件不编号，所以「图号 名称」那条规则对它们没有意义：整个主名都是货物描述。
            PurchasedPartNaming.Split(fileName, out specification, out partName);
            drawingNumber = string.Empty;
        }

        var drawingPath = isAssembly ? null : ConversionPathLayout.ResolveDrawingPath(sourcePath);
        return new PackagePartEntry(
            EntryId(sourcePath),
            sourcePath,
            drawingPath is not null && File.Exists(drawingPath) ? drawingPath : null,
            drawingNumber,
            partName,
            specification,
            quantity,
            category,
            isAssembly);
    }

    /// <summary>
    /// 两张 BOM 与打包目录的文件名前缀：总装图号去掉尾巴上全部纯数字段。
    ///
    /// <c>GHLSS-06-00 总装.SLDASM</c> → 图号段 <c>GHLSS-06-00</c> → 前缀 <c>GHLSS</c>。
    /// 用户要的是「只带总装体前缀、不带数字」——同一台设备的多个总装分件出的 BOM
    /// 才不会因为层级号不同而变成一堆看不出关系的文件名。
    ///
    /// 认不出图号（装配体名里没有空格）时退回装配体主名：宁可文件名长一点，
    /// 也不能让两张 BOM 叫「 机加件清单.xlsx」。
    /// </summary>
    public static string ResolveBomNamePrefix(string assemblyPath)
    {
        var fileName = Path.GetFileName(assemblyPath);
        DrawingNumber.SplitFileName(fileName, out var token, out _);
        var segments = token.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (segments.Count > 0 && segments[^1].All(char.IsAsciiDigit))
            segments.RemoveAt(segments.Count - 1);
        var prefix = string.Join('-', segments).Trim();
        return prefix.Length > 0 ? prefix : Path.GetFileNameWithoutExtension(fileName).Trim();
    }

    private static PackagePlan Empty(string sourceAssemblyPath, IReadOnlyList<string> issues)
        => new(
            sourceAssemblyPath,
            new PackageOutputDirectories(string.Empty, string.Empty, string.Empty),
            string.Empty,
            [],
            issues,
            []);

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 行 id 按源文件全路径定死，与 <c>PropertyPrepPlanner.EntryId</c> 同一约定：
    /// 重新解析之后同一个零件仍是同一行，界面可以原地更新而不是整表重画。
    /// </summary>
    internal static string EntryId(string sourcePath)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..32];

    /// <summary>按路径累计数量；一个路径只有一种件别。</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, (PackagePartCategory Kind, bool IsAssembly, int Quantity)> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, PackagePartCategory kind, bool isAssembly, bool counted, int count = 1)
        {
            var quantity = _items.TryGetValue(path, out var current) ? current.Quantity : 0;
            _items[path] = (kind, isAssembly, quantity + (counted ? count : 0));
        }

        /// <summary>机加件（零件与自制组件按图号混排）→ 外购件（按规格）→ 参考。</summary>
        public IReadOnlyList<PackagePartEntry> ToEntries()
        {
            var entries = _items
                .Select(pair => CreateEntry(pair.Key, pair.Value.Quantity, pair.Value.Kind, pair.Value.IsAssembly))
                .ToArray();
            return entries.Where(entry => entry.Category == PackagePartCategory.Machined)
                .OrderBy(entry => entry.DrawingNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.PartName, StringComparer.CurrentCultureIgnoreCase)
                .Concat(entries.Where(entry => entry.Category == PackagePartCategory.Purchased)
                    .OrderBy(entry => entry.Specification, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(entry => entry.PartName, StringComparer.CurrentCultureIgnoreCase))
                .Concat(entries.Where(entry => entry.Category == PackagePartCategory.Reference)
                    .OrderBy(entry => entry.FileName, StringComparer.CurrentCultureIgnoreCase))
                .ToArray();
        }
    }
}
