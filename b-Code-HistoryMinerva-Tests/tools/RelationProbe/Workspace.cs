using System.Security.Cryptography;

namespace RelationProbe;

/// <summary>
/// 样件的只读保护。样件是生产资产不是可再生 fixture，默认在副本上操作，
/// 并且两种模式都对源目录做 SHA-256 前后比对。
/// </summary>
internal static class Workspace
{
    /// <summary>
    /// 复制整个目录。只复制不移动，且拒绝写入已存在的目标——.asm 里记的是绝对路径引用，
    /// 只复制装配文件会让引用指回原目录，等于没隔离，所以必须整目录复制。
    /// </summary>
    public static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException($"探针拒绝写入非空目录：{destination}");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
    }

    public static Dictionary<string, string> Hash(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
            return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            result[Path.GetRelativePath(directory, file)] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return result;
    }

    /// <summary>返回内容变化、新增或消失的相对路径；空列表即"完全没动过"。</summary>
    public static List<string> Compare(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changed = new List<string>();
        foreach (var (path, hash) in before)
        {
            if (!after.TryGetValue(path, out var current))
                changed.Add(path + "（消失）");
            else if (!string.Equals(hash, current, StringComparison.Ordinal))
                changed.Add(path + "（内容变化）");
        }

        changed.AddRange(after.Keys
            .Where(path => !before.ContainsKey(path))
            .Select(path => path + "（新增）"));
        return changed;
    }
}
