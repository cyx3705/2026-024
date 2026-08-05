using System.Globalization;
using System.Reflection;

namespace RelationProbe;

/// <summary>后期绑定的最小调用面。所有调用都可能失败，调用方负责决定是记录还是中止。</summary>
internal static class Com
{
    public static object? GetProperty(object target, string name)
        => target.GetType().InvokeMember(
            name,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            culture: CultureInfo.InvariantCulture);

    public static object? TryGetProperty(object target, string name)
    {
        try { return GetProperty(target, name); }
        catch { return null; }
    }

    public static T TryGetValue<T>(object target, string name, T fallback)
    {
        try
        {
            var value = GetProperty(target, name);
            return value is null ? fallback : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    public static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            culture: CultureInfo.InvariantCulture);

    public static void TryInvoke(object target, string name, params object?[] args)
    {
        try { Invoke(target, name, args); } catch { }
    }

    public static void SetProperty(object target, string name, object? value)
        => target.GetType().InvokeMember(
            name,
            BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            culture: CultureInfo.InvariantCulture);

    public static void TrySetProperty(object target, string name, object? value)
    {
        try { SetProperty(target, name, value); } catch { }
    }

    /// <summary>
    /// 枚举一个 COM 集合。SE 的集合惯例是 1 基 Item(i)，但不保证；
    /// 依次尝试 1 基、0 基、IEnumerable，把实际生效的方式记进 notes。
    /// </summary>
    public static List<object> Enumerate(object collection, List<string> notes)
    {
        var count = TryGetValue(collection, "Count", -1);
        if (count == 0)
        {
            notes.Add("集合 Count = 0。");
            return [];
        }

        if (count > 0)
        {
            foreach (var start in new[] { 1, 0 })
            {
                var items = new List<object>();
                try
                {
                    for (var index = start; index < start + count; index++)
                    {
                        var item = Invoke(collection, "Item", index)
                            ?? throw new InvalidOperationException($"Item({index}) 返回 null。");
                        items.Add(item);
                    }

                    notes.Add($"集合按 Item({start}..{start + count - 1}) 枚举成功，索引基 = {start}。");
                    return items;
                }
                catch (Exception ex)
                {
                    foreach (var item in items)
                        ObjectDumper.Release(item);
                    notes.Add($"集合 Item 索引基 {start} 枚举失败：{ObjectDumper.Describe(ex)}");
                }
            }
        }

        try
        {
            if (collection is System.Collections.IEnumerable sequence)
            {
                var items = sequence.Cast<object>().ToList();
                notes.Add($"集合改用 IEnumerable 枚举，取到 {items.Count} 项。");
                return items;
            }
        }
        catch (Exception ex)
        {
            notes.Add($"集合 IEnumerable 枚举失败：{ObjectDumper.Describe(ex)}");
        }

        notes.Add($"集合无法枚举，Count = {count}。");
        return [];
    }
}
