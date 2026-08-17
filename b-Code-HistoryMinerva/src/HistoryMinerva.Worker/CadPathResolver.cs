using System.Reflection;

namespace HistoryMinerva.Worker;

/// <summary>
/// SolidWorks <c>IComponent2.GetPathName</c> 有时给出缺失的 <c>.SLDASM</c>/<c>.SLDPRT</c>，
/// 而现场目录里实际是同名 <c>.lnk</c>。探查必须跟着快捷方式走到真实文件，
/// 否则会把整轮转换挡在「未解析引用」上。
/// </summary>
internal static class CadPathResolver
{
    public static string ResolveExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }

        if (IsShortcut(full) && TryResolveShortcut(full) is { Length: > 0 } fromLink && File.Exists(fromLink))
            return Path.GetFullPath(fromLink);
        if (File.Exists(full))
            return full;

        var sibling = full + ".lnk";
        if (File.Exists(sibling) && TryResolveShortcut(sibling) is { Length: > 0 } fromSibling && File.Exists(fromSibling))
            return Path.GetFullPath(fromSibling);
        return full;
    }

    internal static string? TryResolveShortcut(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (type is null)
                return null;
            var shell = Activator.CreateInstance(type);
            if (shell is null)
                return null;
            var shortcut = type.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shell,
                [lnkPath]);
            if (shortcut is null)
                return null;
            var target = Convert.ToString(
                shortcut.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.GetProperty,
                    binder: null,
                    shortcut,
                    null));
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsShortcut(string path)
        => string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);
}
