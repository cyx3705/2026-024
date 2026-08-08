using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RelationProbe;

/// <summary>
/// 按类型库给出的成员表逐个取值，并对关系的几何引用递归展开。
/// 只读无参属性，不写入、不调用有副作用的方法。
/// </summary>
internal static class ObjectDumper
{
    /// <summary>
    /// 读到这些成员时只记录接口名，不递归——它们会把遍历带回整个 SE 对象模型。
    /// </summary>
    private static readonly HashSet<string> NoRecurse = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application", "Parent", "Document", "TopLevelDocument", "PartDocument",
        "SubOccurrenceDocument", "Occurrence", "Occurrences", "SubOccurrences",
        "Body", "Bodies", "Model", "Models", "Faces", "Edges", "Loops", "Vertices",
        "Relations3d", "Relations", "Constraints", "Sheets", "Windows", "Variables",
        // 关系两侧的组件按标量单独采集（见 RelationReader），整对象递归会把输出撑到几十 MB。
        "Occurrence1", "Occurrence2", "Part1", "Part2", "AttributeSets",
        "RelationshipsSelectSet", "SegmentRelations3d",
    };

    public static ComDump Dump(object comObject, int depth, int maxDepth)
    {
        var (interfaceName, chain, members) = TypeLibrary.Describe(comObject);
        var dump = new ComDump
        {
            InterfaceName = interfaceName,
            TypeChain = chain,
            Members = members,
        };

        foreach (var member in members)
        {
            if (member.Kind != "get" || member.ParamCount != 0)
                continue;

            object? value;
            try
            {
                value = comObject.GetType().InvokeMember(
                    member.Name,
                    BindingFlags.GetProperty,
                    binder: null,
                    comObject,
                    args: null,
                    culture: CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                dump.Unreadable[member.Name] = Describe(ex);
                continue;
            }

            dump.Values[member.Name] = Format(value);

            if (value is null || !Marshal.IsComObject(value))
                continue;
            if (depth >= maxDepth || NoRecurse.Contains(member.Name))
            {
                Release(value);
                continue;
            }

            try
            {
                dump.Children[member.Name] = Dump(value, depth + 1, maxDepth);
            }
            catch (Exception ex)
            {
                dump.Unreadable[member.Name + "(递归)"] = Describe(ex);
            }
            finally
            {
                Release(value);
            }
        }

        MethodProbe.Run(comObject, dump, depth, maxDepth);
        return dump;
    }

    /// <summary>
    /// 调用 <c>void Xxx(ref Array)</c> 形状的方法。COM 侧是 [in,out] SAFEARRAY，
    /// 必须通过 ParameterModifier 标记 byref，否则拿不回填充后的数组。
    /// </summary>
    public static double[]? TryReadRefArray(object comObject, string method, int length)
    {
        try
        {
            var args = new object[] { new double[length] };
            var modifier = new ParameterModifier(1);
            modifier[0] = true;
            comObject.GetType().InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                binder: null,
                comObject,
                args,
                [modifier],
                CultureInfo.InvariantCulture,
                namedParameters: null);
            if (args[0] is not Array array || array.Length != length)
                return null;
            var result = array.Cast<object>().Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToArray();
            return result.All(double.IsFinite) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "<null>";
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case double number:
                return number.ToString("G17", CultureInfo.InvariantCulture);
            case float number:
                return number.ToString("G9", CultureInfo.InvariantCulture);
            case IFormattable formattable when value.GetType().IsPrimitive || value is decimal:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case DateTime moment:
                return moment.ToString("O", CultureInfo.InvariantCulture);
        }

        if (Marshal.IsComObject(value))
            return "<COM:" + (TypeLibrary.GetInterfaceName(value) ?? "未知接口") + ">";

        if (value is Array array)
        {
            var items = array.Cast<object?>().Take(32).Select(Format);
            var suffix = array.Length > 32 ? $", …共 {array.Length} 项" : string.Empty;
            return "[" + string.Join(", ", items) + suffix + "]";
        }

        if (value is IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().Take(32).Select(Format);
            return "[" + string.Join(", ", items) + "]";
        }

        return value.ToString() ?? "<无法格式化>";
    }

    public static string Describe(Exception exception)
    {
        var inner = exception is TargetInvocationException { InnerException: { } target } ? target : exception;
        return $"0x{unchecked((uint)inner.HResult):X8} {inner.Message}";
    }

    public static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;
        try { Marshal.ReleaseComObject(comObject); } catch { }
    }
}
