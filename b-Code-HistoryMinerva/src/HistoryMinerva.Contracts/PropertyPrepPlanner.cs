using System.Security.Cryptography;
using System.Text;

namespace HistoryMinerva.Contracts;

/// <summary>
/// 根据所选 SolidWorks 装配体的现有图号（或默认总装 <c>前缀-00</c>）
/// 给同级零件和下层装配体分配图号。子文件夹中的外购件不编号；
/// 小组件下的装配体视为零件，其内部文件不分配图号。
///
/// **V4.9 只剩这一条计划**。前缀为空时算出来的目标文件名就是「只有名称、没有图号」，
/// 那正是删图号要的结果，因此原来那条独立的「按空格洗图号」计划连同它的遍历、
/// 校验和 Worker 分支一起退役了（DEC-057）。
/// </summary>
public static class PropertyPrepPlanner
{
    /// <param name="drawingPrefix">
    /// 图号前缀。空串合法：整份计划的图号都是空，文件名只剩名称，也就是删图号。
    /// </param>
    /// <param name="partNames">
    /// 用户在表里改过的零件名称，按源文件全路径记账。没记的以文件名里第一个空格之后的
    /// 那一段为准。名称同时决定目标文件名和「名称」属性槽——两者永远是同一个字符串。
    /// </param>
    /// <param name="kinds">
    /// V4.11：用户改写过的件别，按源文件全路径记账。被设成外购件或参考的同级件及其内部件
    /// 不编号、不改名、不写属性，也不占序号——与子文件夹外购件同一个待遇。
    /// </param>
    public static AssemblyRenamePlan Create(
        AssemblyProbeResult probe,
        string drawingPrefix,
        IReadOnlyDictionary<string, string>? partNames = null,
        IReadOnlyDictionary<string, PackagePartCategory>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var issues = new List<string>();
        var warnings = new List<string>();
        string prefix;
        try
        {
            prefix = DrawingNumber.NormalizePrefix(drawingPrefix);
        }
        catch (InvalidDataException ex)
        {
            return new AssemblyRenamePlan(
                probe.SourceAssemblyPath,
                (drawingPrefix ?? string.Empty).Trim(),
                [],
                [],
                [ex.Message],
                []);
        }

        if (!Path.IsPathFullyQualified(probe.SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(
                probe.SourceAssemblyPath, ConversionPathLayout.SolidWorksAssemblyExtension))
        {
            issues.Add("属性整备改名只接受绝对路径的 SolidWorks .SLDASM。");
            return Empty(probe.SourceAssemblyPath, prefix, issues);
        }

        var rootPath = Path.GetFullPath(probe.SourceAssemblyPath);
        var rootDirectory = Path.GetDirectoryName(rootPath);
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            issues.Add("无法解析所选装配体所在目录。");
            return Empty(rootPath, prefix, issues);
        }

        if (probe.Documents is not { Count: > 0 })
        {
            issues.Add("探查结果缺少文档层级，无法按装配规则编号。");
            return Empty(rootPath, prefix, issues);
        }

        var documents = new Dictionary<string, AssemblyDocumentReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in probe.Documents)
        {
            if (!Path.IsPathFullyQualified(document.SourceAssemblyPath))
                continue;
            documents[Path.GetFullPath(document.SourceAssemblyPath)] = document;
        }

        if (!documents.ContainsKey(rootPath))
        {
            issues.Add("探查结果没有所选装配体的文档读数。");
            return Empty(rootPath, prefix, issues);
        }

        var rootNumber = ResolveRootNumber(rootPath, prefix);
        var names = partNames ?? EmptyNames;
        var planned = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        var unnumbered = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        var excluded = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        var visit = new Visit(rootPath, documents, probe, planned, unnumbered, excluded, names, kinds, warnings);
        VisitAssembly(
            rootPath,
            rootNumber,
            parentPath: null,
            depth: 0,
            skipChildren: !rootNumber.IsAssembly,
            visit);
        CollectPurchasedReadings(probe, rootPath, visit);

        var entries = planned.Values
            .Select(entry => entry.ToEntry())
            .OrderBy(entry => entry.Depth)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skipped = unnumbered.Values
            .Select(entry => entry.ToEntry())
            .OrderBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CheckCollisions(entries, issues);
        foreach (var entry in entries)
        {
            if (!File.Exists(entry.SourcePath))
                issues.Add($"源文件不存在：{entry.SourcePath}");
            var targetDirectory = Path.GetDirectoryName(entry.TargetPath);
            var sourceDirectory = Path.GetDirectoryName(entry.SourcePath);
            if (!string.Equals(targetDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase))
                issues.Add($"改名不得换目录：{entry.SourcePath}");
            if (!AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath)
                && File.Exists(entry.TargetPath))
            {
                issues.Add($"目标文件已存在：{entry.TargetPath}");
            }
        }

        var excludedEntries = excluded
            .Where(pair => !planned.ContainsKey(pair.Key) && !unnumbered.ContainsKey(pair.Key))
            .Select(pair => pair.Value.ToEntry())
            .OrderBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        warnings.AddRange(probe.Warnings);
        return new AssemblyRenamePlan(
            rootPath, prefix, entries, skipped, issues, Distinct(warnings), excludedEntries);
    }

    /// <summary>
    /// 子文件夹里的外购件。探查侧不把它们放进文档层级，所以编号那一遍看不见；
    /// 这里只把它们列成不编号的行，让件别在属性整备里也看得见、改得动（改了只影响打包）。
    /// </summary>
    private static void CollectPurchasedReadings(AssemblyProbeResult probe, string rootPath, Visit visit)
    {
        if (probe.PurchasedParts is not { Count: > 0 } readings)
            return;
        foreach (var reading in readings)
        {
            if (reading.InstanceCount <= 0 || !Path.IsPathFullyQualified(reading.SourcePath))
                continue;
            var path = Path.GetFullPath(reading.SourcePath);
            if (ConversionPathLayout.IsUnderReferencePartsDirectory(path))
                continue;
            Remember(visit.Excluded, path, number: null, rootPath, depth: 1, assignsDrawingNumber: false, visit.PartNames);
        }
    }

    /// <summary>一次遍历里不变的上下文。参数太多时收成一个，免得每层递归抄一遍。</summary>
    private sealed record Visit(
        string RootPath,
        IReadOnlyDictionary<string, AssemblyDocumentReading> Documents,
        AssemblyProbeResult Probe,
        Dictionary<string, MutableEntry> Planned,
        Dictionary<string, MutableEntry> Unnumbered,
        Dictionary<string, MutableEntry> Excluded,
        IReadOnlyDictionary<string, string> PartNames,
        IReadOnlyDictionary<string, PackagePartCategory>? Kinds,
        List<string> Warnings)
    {
        /// <summary>同级件被用户设成外购件或参考。缺省判据（子文件夹、参考目录）由调用处先判。</summary>
        public bool IsExcludedByKind(string path)
            => PartKinds.Resolve(Kinds, path, RootPath) != PackagePartCategory.Machined;
    }

    private static AssemblyRenamePlan Empty(string source, string prefix, IReadOnlyList<string> issues)
        => new(source, prefix, [], [], issues, []);

    private static DrawingNumber ResolveRootNumber(string rootPath, string prefix)
    {
        var fileName = Path.GetFileName(rootPath);
        return DrawingNumber.TryParseFileName(fileName, prefix, out var parsed, out _)
            ? parsed
            : DrawingNumber.RootAssembly(prefix);
    }

    private static void VisitAssembly(
        string assemblyPath,
        DrawingNumber number,
        string? parentPath,
        int depth,
        bool skipChildren,
        Visit visit)
    {
        var (rootPath, documents, probe, planned, unnumbered, excluded, partNames, _, warnings) = visit;
        Remember(planned, assemblyPath, number, parentPath, depth, assignsDrawingNumber: true, partNames);
        if (skipChildren || !documents.TryGetValue(assemblyPath, out var document))
            return;

        var sequence = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedPurchased = 0;
        var skippedReferenceParts = 0;
        var skippedByKind = 0;
        foreach (var child in document.Children)
        {
            if (!Path.IsPathFullyQualified(child.SourcePath))
            {
                warnings.Add($"跳过非绝对路径引用：{child.SourcePath}");
                continue;
            }

            var childPath = Path.GetFullPath(child.SourcePath);
            if (!seen.Add(childPath))
            {
                if (planned.TryGetValue(childPath, out var existing))
                    existing.AddParent(assemblyPath);
                continue;
            }

            if (ConversionPathLayout.IsUnderReferencePartsDirectory(childPath))
            {
                skippedReferenceParts++;
                continue;
            }

            if (ConversionPathLayout.IsOutsideAssemblyDirectory(childPath, rootPath))
            {
                skippedPurchased++;
                continue;
            }

            if (child.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true
                || IsFullySuppressed(probe, childPath))
            {
                warnings.Add($"跳过未解析或抑制的引用：{childPath}");
                continue;
            }

            // V4.11：用户设成外购件或参考的同级件，和子文件夹外购件同一个待遇——
            // 不编号、不占序号，它底下的件也一并不碰。只留一行给用户看、给用户点回来。
            if (visit.IsExcludedByKind(childPath))
            {
                skippedByKind++;
                Remember(excluded, childPath, number: null, assemblyPath, depth + 1, assignsDrawingNumber: false, partNames);
                continue;
            }

            if (planned.TryGetValue(childPath, out var already))
            {
                already.AddParent(assemblyPath);
                continue;
            }

            var treatAsPart = ShouldTreatAsPart(child.IsSubAssembly, number);
            if (treatAsPart || !child.IsSubAssembly)
            {
                var childNumber = number.Child(sequence++, asAssembly: false);
                Remember(planned, childPath, childNumber, assemblyPath, depth + 1, assignsDrawingNumber: true, partNames);
                if (child.IsSubAssembly)
                    CollectUnnumberedDescendants(childPath, assemblyPath, depth + 1, visit);
                continue;
            }

            var assemblyNumber = number.Child(sequence++, asAssembly: true);
            VisitAssembly(
                childPath,
                assemblyNumber,
                assemblyPath,
                depth + 1,
                skipChildren: false,
                visit);
        }

        if (skippedPurchased > 0)
            warnings.Add($"已跳过 {skippedPurchased} 个外购件（子文件夹，与装配体不同级）。");
        if (skippedReferenceParts > 0)
            warnings.Add($"已跳过 {skippedReferenceParts} 个参考部件目录下的文件。");
        if (skippedByKind > 0)
            warnings.Add($"已按件别跳过 {skippedByKind} 个设为外购件或参考的文件。");
    }

    private static bool ShouldTreatAsPart(bool isSubAssembly, DrawingNumber parentNumber)
    {
        if (!isSubAssembly)
            return true;
        if (!parentNumber.IsAssembly || parentNumber.IsMinorAssembly || !parentNumber.IsRootAssembly && !parentNumber.IsMajorAssembly)
            return true;

        return false;
    }

    private static void CollectUnnumberedDescendants(
        string assemblyPath,
        string parentPath,
        int depth,
        Visit visit)
    {
        if (!visit.Documents.TryGetValue(assemblyPath, out var document))
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in document.Children)
        {
            if (!Path.IsPathFullyQualified(child.SourcePath))
                continue;
            var childPath = Path.GetFullPath(child.SourcePath);
            if (!seen.Add(childPath) || visit.Planned.ContainsKey(childPath))
                continue;
            if (ConversionPathLayout.IsUnderReferencePartsDirectory(childPath))
                continue;
            if (ConversionPathLayout.IsOutsideAssemblyDirectory(childPath, visit.RootPath))
                continue;
            // 未编号的内部件本来就不改名；件别在这里只影响打包，但行要换到「按件别排除」那一组，
            // 它底下的件也就不再列出——外购或参考的组件，里面的件不是这一台设备的事。
            if (visit.IsExcludedByKind(childPath))
            {
                Remember(visit.Excluded, childPath, number: null, parentPath, depth + 1, assignsDrawingNumber: false, visit.PartNames);
                continue;
            }

            Remember(visit.Unnumbered, childPath, number: null, parentPath, depth + 1, assignsDrawingNumber: false, visit.PartNames);
            if (child.IsSubAssembly)
                CollectUnnumberedDescendants(childPath, assemblyPath, depth + 1, visit);
        }
    }

    private static bool IsFullySuppressed(AssemblyProbeResult probe, string path)
    {
        var matches = probe.Occurrences
            .Where(item => Path.IsPathFullyQualified(item.SourcePath)
                           && string.Equals(Path.GetFullPath(item.SourcePath), path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length > 0 && matches.All(item => item.IsSuppressed);
    }

    private static void Remember(
        Dictionary<string, MutableEntry> map,
        string sourcePath,
        DrawingNumber? number,
        string? parentPath,
        int depth,
        bool assignsDrawingNumber,
        IReadOnlyDictionary<string, string> partNames)
    {
        if (!map.TryGetValue(sourcePath, out var entry))
        {
            var partName = ResolvePartName(sourcePath, partNames);
            var extension = Path.GetExtension(sourcePath);
            var targetPath = number is { } drawing
                ? Path.Combine(
                    Path.GetDirectoryName(sourcePath) ?? string.Empty,
                    DrawingNumber.FormatFileName(drawing, partName, extension))
                : sourcePath;
            entry = new MutableEntry(
                EntryId(sourcePath),
                sourcePath,
                targetPath,
                number?.Text ?? string.Empty,
                assignsDrawingNumber,
                depth,
                partName);
            map[sourcePath] = entry;
        }

        entry.AddParent(parentPath);
        if (depth < entry.Depth)
            entry.Depth = depth;
    }

    /// <summary>
    /// 这个文件的名称。用户在表里改过的以用户的为准，否则取文件名里第一个空格之后的那一段。
    ///
    /// **不再按前缀去匹配旧图号**（V4.8 及以前的做法）。那条路要求旧名字正好以本轮的前缀打头，
    /// 于是换一个前缀、或者旧号是别的项目带过来的，整个旧文件名就会被当成"名称"——
    /// 结果是 <c>ZS-01 QT-88 阀体.SLDPRT</c> 这样两个号叠在一起的名字。
    /// 按空格切只认一条规则，前缀长什么样都不影响它（DEC-058）。
    /// </summary>
    private static string ResolvePartName(string sourcePath, IReadOnlyDictionary<string, string> partNames)
    {
        if (partNames.TryGetValue(Path.GetFullPath(sourcePath), out var edited)
            && !string.IsNullOrWhiteSpace(edited))
        {
            return edited.Trim();
        }

        DrawingNumber.SplitFileName(Path.GetFileName(sourcePath), out _, out var name);
        return name;
    }

    /// <summary>没有任何名称改动时用它，省掉每次建一个空字典。</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static void CheckCollisions(IReadOnlyList<RenameEntry> entries, List<string> issues)
    {
        foreach (var group in entries.GroupBy(
                     entry => entry.TargetPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 1)
                issues.Add($"多个源文件会改成同一名称“{Path.GetFileName(group.Key)}”：{string.Join("；", paths)}");
        }
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 条目 id 按源文件全路径定死，不用 <see cref="Guid.NewGuid"/>。
    ///
    /// 改一次图号前缀，整份计划连同零件行都要重建。id 随机的话每一行都换一个身份：
    /// 界面没法原地更新，只能整表重画——几百个零件就是几百次控件重建，正是"改一格卡一下"
    /// 的来源；而且用户此刻点开的那一格，指向的已经是一个不存在的 id 了。
    /// </summary>
    private static string EntryId(string sourcePath)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..32];

    private sealed class MutableEntry(
        string id,
        string sourcePath,
        string targetPath,
        string drawingNumber,
        bool assignsDrawingNumber,
        int depth,
        string partName)
    {
        private readonly List<string> _parents = [];

        public int Depth { get; set; } = depth;

        public void AddParent(string? parentPath)
        {
            if (string.IsNullOrWhiteSpace(parentPath))
                return;
            var full = Path.GetFullPath(parentPath);
            if (!_parents.Contains(full, StringComparer.OrdinalIgnoreCase))
                _parents.Add(full);
        }

        public RenameEntry ToEntry()
            => new(
                id,
                sourcePath,
                targetPath,
                drawingNumber,
                assignsDrawingNumber,
                _parents,
                Depth,
                PartName: partName);
    }
}
