namespace SE2SW.Worker;

internal static class SolidWorksAssemblyTemplateResolver
{
    public static IReadOnlyList<string> FindCandidates(
        string? configuredTemplate,
        string? installDirectory,
        string? commonApplicationData = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFile(configuredTemplate, candidates, seen);

        var folders = new List<string>();
        AddFolder(Path.Combine(installDirectory ?? string.Empty, "templates"), folders);
        var programData = string.IsNullOrWhiteSpace(commonApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : commonApplicationData;
        var productRoot = Path.Combine(programData, "SOLIDWORKS");
        if (Directory.Exists(productRoot))
        {
            try
            {
                foreach (var productDirectory in Directory.EnumerateDirectories(productRoot, "SOLIDWORKS *")
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddFolder(Path.Combine(productDirectory, "templates"), folders);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> templates;
            try
            {
                templates = Directory.EnumerateFiles(folder, "*.asmdot", SearchOption.TopDirectoryOnly)
                    .OrderBy(GetPriority)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var template in templates)
                AddFile(template, candidates, seen);
        }

        return candidates;
    }

    private static int GetPriority(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "gb_assembly.asmdot", StringComparison.OrdinalIgnoreCase))
            return 0;
        return name.Contains("assembly", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static void AddFolder(string path, ICollection<string> folders)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            folders.Add(Path.GetFullPath(path));
    }

    private static void AddFile(string? path, ICollection<string> candidates, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetExtension(path), ".asmdot", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (seen.Add(fullPath))
            candidates.Add(fullPath);
    }
}
