namespace HistoryMinerva.Contracts;

/// <summary>
/// 根据所选 SolidWorks 装配体的现有图号（或默认总装 <c>前缀-00</c>）
/// 给同级零件和下层装配体分配图号。子文件夹中的外购件不编号；
/// 小组件下的装配体视为零件，其内部文件不分配图号。
/// </summary>
public static class PropertyPrepPlanner
{
    public static AssemblyRenamePlan Create(
        AssemblyProbeResult probe,
        string drawingPrefix)
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
                drawingPrefix.Trim(),
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
        var planned = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        var unnumbered = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        VisitAssembly(
            rootPath,
            rootNumber,
            parentPath: null,
            depth: 0,
            skipChildren: !rootNumber.IsAssembly,
            rootPath,
            documents,
            probe,
            planned,
            unnumbered,
            warnings);

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

        warnings.AddRange(probe.Warnings);
        return new AssemblyRenamePlan(rootPath, prefix, entries, skipped, issues, Distinct(warnings));
    }

    /// <summary>
    /// 按文件名第一个空格洗掉图号，保留空格后的原名。只遍历所选装配体同级目录文档，
    /// 不含子文件夹外购件。没有空格的文件保持原名。
    /// </summary>
    public static AssemblyRenamePlan CreateStrip(AssemblyProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var issues = new List<string>();
        var warnings = new List<string>();
        if (!Path.IsPathFullyQualified(probe.SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(
                probe.SourceAssemblyPath, ConversionPathLayout.SolidWorksAssemblyExtension))
        {
            issues.Add("按空格洗图号只接受绝对路径的 SolidWorks .SLDASM。");
            return Empty(probe.SourceAssemblyPath, string.Empty, issues);
        }

        var rootPath = Path.GetFullPath(probe.SourceAssemblyPath);
        if (probe.Documents is not { Count: > 0 })
        {
            issues.Add("探查结果缺少文档层级，无法按空格洗图号。");
            return Empty(rootPath, string.Empty, issues);
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
            return Empty(rootPath, string.Empty, issues);
        }

        var planned = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        var kept = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        VisitStrip(
            rootPath,
            parentPath: null,
            depth: 0,
            rootPath,
            documents,
            probe,
            planned,
            kept,
            warnings);

        var entries = planned.Values
            .Select(entry => entry.ToEntry())
            .OrderBy(entry => entry.Depth)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skipped = kept.Values
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

        warnings.AddRange(probe.Warnings);
        return new AssemblyRenamePlan(rootPath, string.Empty, entries, skipped, issues, Distinct(warnings));
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
        string rootPath,
        IReadOnlyDictionary<string, AssemblyDocumentReading> documents,
        AssemblyProbeResult probe,
        Dictionary<string, MutableEntry> planned,
        Dictionary<string, MutableEntry> unnumbered,
        List<string> warnings)
    {
        Remember(planned, assemblyPath, number, parentPath, depth, assignsDrawingNumber: true);
        if (skipChildren || !documents.TryGetValue(assemblyPath, out var document))
            return;

        var sequence = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedPurchased = 0;
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

            if (planned.TryGetValue(childPath, out var already))
            {
                already.AddParent(assemblyPath);
                continue;
            }

            var treatAsPart = ShouldTreatAsPart(child.IsSubAssembly, number);
            if (treatAsPart || !child.IsSubAssembly)
            {
                var childNumber = number.Child(sequence++, asAssembly: false);
                Remember(planned, childPath, childNumber, assemblyPath, depth + 1, assignsDrawingNumber: true);
                if (child.IsSubAssembly)
                    CollectUnnumberedDescendants(
                        childPath, assemblyPath, depth + 1, rootPath, documents, planned, unnumbered);
                continue;
            }

            var assemblyNumber = number.Child(sequence++, asAssembly: true);
            VisitAssembly(
                childPath,
                assemblyNumber,
                assemblyPath,
                depth + 1,
                skipChildren: false,
                rootPath,
                documents,
                probe,
                planned,
                unnumbered,
                warnings);
        }

        if (skippedPurchased > 0)
            warnings.Add($"已跳过 {skippedPurchased} 个外购件（子文件夹，与装配体不同级）。");
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
        string rootPath,
        IReadOnlyDictionary<string, AssemblyDocumentReading> documents,
        Dictionary<string, MutableEntry> planned,
        Dictionary<string, MutableEntry> unnumbered)
    {
        if (!documents.TryGetValue(assemblyPath, out var document))
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in document.Children)
        {
            if (!Path.IsPathFullyQualified(child.SourcePath))
                continue;
            var childPath = Path.GetFullPath(child.SourcePath);
            if (!seen.Add(childPath) || planned.ContainsKey(childPath))
                continue;
            if (ConversionPathLayout.IsOutsideAssemblyDirectory(childPath, rootPath))
                continue;

            Remember(unnumbered, childPath, number: null, parentPath, depth + 1, assignsDrawingNumber: false);
            if (child.IsSubAssembly)
                CollectUnnumberedDescendants(childPath, assemblyPath, depth + 1, rootPath, documents, planned, unnumbered);
        }
    }

    private static void VisitStrip(
        string path,
        string? parentPath,
        int depth,
        string rootPath,
        IReadOnlyDictionary<string, AssemblyDocumentReading> documents,
        AssemblyProbeResult probe,
        Dictionary<string, MutableEntry> planned,
        Dictionary<string, MutableEntry> kept,
        List<string> warnings)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            warnings.Add($"跳过非绝对路径引用：{path}");
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (ConversionPathLayout.IsOutsideAssemblyDirectory(fullPath, rootPath))
            return;

        var map = DrawingNumber.TryStripBySpace(Path.GetFileName(fullPath), out var token, out var original)
            ? planned
            : kept;
        RememberStrip(map, fullPath, parentPath, depth, token, original);
        if (!documents.TryGetValue(fullPath, out var document))
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedPurchased = 0;
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
                if (planned.TryGetValue(childPath, out var plannedExisting))
                    plannedExisting.AddParent(fullPath);
                else if (kept.TryGetValue(childPath, out var keptExisting))
                    keptExisting.AddParent(fullPath);
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

            if (planned.TryGetValue(childPath, out var alreadyPlanned))
            {
                alreadyPlanned.AddParent(fullPath);
                continue;
            }

            if (kept.TryGetValue(childPath, out var alreadyKept))
            {
                alreadyKept.AddParent(fullPath);
                continue;
            }

            VisitStrip(childPath, fullPath, depth + 1, rootPath, documents, probe, planned, kept, warnings);
        }

        if (skippedPurchased > 0)
            warnings.Add($"已跳过 {skippedPurchased} 个外购件（子文件夹，与装配体不同级）。");
    }

    private static void RememberStrip(
        Dictionary<string, MutableEntry> map,
        string sourcePath,
        string? parentPath,
        int depth,
        string drawingToken,
        string originalName)
    {
        if (!map.TryGetValue(sourcePath, out var entry))
        {
            var assigns = DrawingNumber.TryStripBySpace(Path.GetFileName(sourcePath), out _, out _);
            var extension = Path.GetExtension(sourcePath);
            var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var targetPath = assigns
                ? Path.Combine(directory, originalName + extension)
                : sourcePath;
            entry = new MutableEntry(
                Guid.NewGuid().ToString("N"),
                sourcePath,
                targetPath,
                assigns ? drawingToken : string.Empty,
                assigns,
                depth);
            map[sourcePath] = entry;
        }

        entry.AddParent(parentPath);
        if (depth < entry.Depth)
            entry.Depth = depth;
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
        bool assignsDrawingNumber)
    {
        if (!map.TryGetValue(sourcePath, out var entry))
        {
            var originalName = ResolveOriginalName(sourcePath, number?.Prefix);
            var extension = Path.GetExtension(sourcePath);
            var targetPath = number is { } drawing
                ? Path.Combine(
                    Path.GetDirectoryName(sourcePath) ?? string.Empty,
                    DrawingNumber.FormatFileName(drawing, originalName, extension))
                : sourcePath;
            entry = new MutableEntry(
                Guid.NewGuid().ToString("N"),
                sourcePath,
                targetPath,
                number?.Text ?? string.Empty,
                assignsDrawingNumber,
                depth);
            map[sourcePath] = entry;
        }

        entry.AddParent(parentPath);
        if (depth < entry.Depth)
            entry.Depth = depth;
    }

    private static string ResolveOriginalName(string sourcePath, string? prefix)
    {
        var fileName = Path.GetFileName(sourcePath);
        if (!string.IsNullOrWhiteSpace(prefix)
            && DrawingNumber.TryParseFileName(fileName, prefix, out _, out var original))
        {
            return original;
        }

        return Path.GetFileNameWithoutExtension(sourcePath);
    }

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

    private sealed class MutableEntry(
        string id,
        string sourcePath,
        string targetPath,
        string drawingNumber,
        bool assignsDrawingNumber,
        int depth)
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
                Depth);
    }
}
