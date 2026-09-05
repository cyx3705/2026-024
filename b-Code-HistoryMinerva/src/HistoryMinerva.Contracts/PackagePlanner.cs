using System.Security.Cryptography;
using System.Text;

namespace HistoryMinerva.Contracts;

/// <summary>
/// V4.10：把一次装配探查结果算成整体打包计划。
///
/// 三件事在这里定死，之后的界面、导出与 BOM 都只是照做：
///
/// <list type="number">
///   <item><b>谁进清单</b>：探查已经把与总装同级的零件收进 <see cref="AssemblyProbeResult.Occurrences"/>，
///         把子文件夹里的外购件收进 <see cref="AssemblyProbeResult.PurchasedParts"/>。
///         **只收零件**——子装配体不进表（用户要求「表格中只显示零件即可」），
///         它们既不出现在 BOM 里，也不导 STEP。</item>
///   <item><b>数量</b>：整个总装配体里的实例总数，含嵌套倍数（子装配用两次、里面三个同款件＝6），
///         抑制件不计。</item>
///   <item><b>图号与名称</b>：按文件名第一个空格切，与改名管线同一条口径
///         （<see cref="DrawingNumber.SplitFileName"/>）。BOM 描述的是此刻磁盘上的事实，
///         不重新编号——重新编号得到的是「改完名之后的图号」，而文件还没改。</item>
/// </list>
/// </summary>
public static class PackagePlanner
{
    public static PackagePlan Create(AssemblyProbeResult probe)
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

        var entries = new List<PackagePartEntry>();
        entries.AddRange(BuildMachined(probe, warnings));
        entries.AddRange(BuildPurchased(probe, warnings));

        if (entries.Count == 0)
            issues.Add("总装配体里没有识别到任何零件，没有可打包的内容。");

        var missingDrawings = entries.Count(entry => !entry.HasDrawing);
        if (missingDrawings > 0)
            warnings.Add($"{missingDrawings} 个零件没有同名工程图，DWG 与 PDF 目录里不会有它们。");

        warnings.AddRange(probe.Warnings);
        return new PackagePlan(
            rootPath,
            ConversionPathLayout.ResolvePackageDirectories(rootDirectory),
            ResolveBomNamePrefix(rootPath),
            entries,
            issues,
            Distinct(warnings));
    }

    /// <summary>
    /// 与总装同级的零件。探查侧已经把子文件夹外购件排除在
    /// <see cref="AssemblyProbeResult.Occurrences"/> 之外，这里不必再判一次目录。
    /// </summary>
    private static IEnumerable<PackagePartEntry> BuildMachined(
        AssemblyProbeResult probe,
        List<string> warnings)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in probe.Occurrences)
        {
            if (occurrence.IsSubAssembly || occurrence.IsSuppressed)
                continue;
            if (!Path.IsPathFullyQualified(occurrence.SourcePath))
                continue;
            var path = Path.GetFullPath(occurrence.SourcePath);
            if (!ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksPartExtension))
                continue;
            if (!File.Exists(path))
            {
                warnings.Add($"跳过未解析的零件引用：{path}");
                continue;
            }

            counts[path] = counts.GetValueOrDefault(path) + 1;
        }

        return counts
            .Select(pair => CreateEntry(pair.Key, pair.Value, PackagePartCategory.Machined))
            .OrderBy(entry => entry.DrawingNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.PartName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 子文件夹里的外购件。数量由探查侧数好带过来——那一遍展平已经走过全部后代实例，
    /// 而这里连文件都不该打开（外购件很可能根本不在本机上）。
    /// </summary>
    private static IEnumerable<PackagePartEntry> BuildPurchased(
        AssemblyProbeResult probe,
        List<string> warnings)
    {
        if (probe.PurchasedParts is not { Count: > 0 } readings)
            return [];

        var entries = new List<PackagePartEntry>(readings.Count);
        foreach (var reading in readings)
        {
            if (reading.InstanceCount <= 0 || string.IsNullOrWhiteSpace(reading.SourcePath))
                continue;
            string path;
            try
            {
                path = Path.GetFullPath(reading.SourcePath.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"跳过路径无法解析的外购件：{reading.SourcePath}");
                continue;
            }

            entries.Add(CreateEntry(path, reading.InstanceCount, PackagePartCategory.Purchased));
        }

        return entries
            .OrderBy(entry => entry.Specification, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.PartName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static PackagePartEntry CreateEntry(string sourcePath, int quantity, PackagePartCategory category)
    {
        var fileName = Path.GetFileName(sourcePath);
        DrawingNumber.SplitFileName(fileName, out var drawingNumber, out var partName);
        var specification = string.Empty;
        if (category == PackagePartCategory.Purchased)
        {
            // 外购件不编号，所以「图号 名称」那条规则对它们没有意义：整个主名都是货物描述。
            PurchasedPartNaming.Split(fileName, out specification, out partName);
            drawingNumber = string.Empty;
        }

        var drawingPath = ConversionPathLayout.ResolveDrawingPath(sourcePath);
        return new PackagePartEntry(
            EntryId(sourcePath),
            sourcePath,
            File.Exists(drawingPath) ? drawingPath : null,
            drawingNumber,
            partName,
            specification,
            quantity,
            category);
    }

    /// <summary>
    /// 两张 BOM 的文件名前缀：总装图号去掉尾巴上全部纯数字段。
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
            new PackageOutputDirectories(
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
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
    private static string EntryId(string sourcePath)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..32];
}
