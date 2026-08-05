using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RelationProbe;

/// <summary>
/// 带参方法的安全试探。
///
/// 关系最有价值的数据都不在无参属性上：<c>GetGeometry1(out 类型, out 点XYZ, out 向量XYZ)</c>、
/// <c>Face.GetRange(ref 最小点, ref 最大点)</c>、<c>Plane.GetPlaneData(ref 根点, ref 法向)</c>。
/// 这些方法用空 VARIANT 调会得到 DISP_E_TYPEMISMATCH——必须按类型库读出的真实 VT 给 byref 变体播种。
/// </summary>
internal static class MethodProbe
{
    private const int VtI2 = 2, VtI4 = 3, VtR4 = 4, VtR8 = 5, VtBstr = 8, VtDispatch = 9;
    private const int VtBool = 11, VtVariant = 12, VtUnknown = 13, VtUi1 = 17, VtInt = 22, VtUint = 23;

    private const int DefaultArrayLength = 3;
    private const int MatrixArrayLength = 16;
    private const int ByteArrayLength = 256;

    /// <summary>只读试探，任何可能改状态的动词一律不碰。</summary>
    private static readonly string[] MutatingPrefixes =
        ["Add", "Delete", "Remove", "Set", "Put", "Create", "Save", "Update", "Flip", "Select", "Close", "Quit"];

    private static readonly string[] SkipExact =
        ["QueryInterface", "AddRef", "Release", "GetSuppressionVariable", "HasSuppressionVariable"];

    /// <summary>返回的对象不再深挖：它们会把遍历带回装配层。</summary>
    private static readonly string[] NoCapture = ["Occurrence", "Path"];

    public static void Run(object target, ComDump dump, int depth, int maxDepth)
    {
        foreach (var member in dump.Members)
        {
            if (member.Kind != "method" || !IsSafeToProbe(member))
                continue;
            Probe(target, dump, member, depth, maxDepth);
        }
    }

    private static bool IsSafeToProbe(MemberRecord member)
    {
        if (SkipExact.Contains(member.Name, StringComparer.Ordinal))
            return false;
        if (MutatingPrefixes.Any(prefix => member.Name.StartsWith(prefix, StringComparison.Ordinal)))
            return false;
        if (member.ParamCount == 0)
            return false;

        // 只探"全部结果都从参数出来"的方法。任何必填的纯输入参数都意味着我们得先知道传什么，
        // 那就不是试探而是猜测（GetPointAtParam 的 NumParams、GetFacetData 的 Tolerance 都属此类）。
        return member.Parameters.Count == member.ParamCount
            && member.Parameters.All(parameter =>
                parameter.IsOut || parameter.IsOptional || (parameter.IsIn && parameter.IsSafeArray));
    }

    private static void Probe(object target, ComDump dump, MemberRecord member, int depth, int maxDepth)
    {
        dump.MethodProbes[member.Name + " 签名"] =
            "(" + string.Join(", ", member.Parameters.Select(p => $"{p.Name}: {p.Type}")) + ") → " + member.ReturnType;

        var args = member.Parameters.Select(parameter => Seed(member.Name, parameter)).ToArray();
        var modifier = new ParameterModifier(args.Length);
        for (var index = 0; index < args.Length; index++)
            modifier[index] = true;

        object? returned;
        try
        {
            returned = target.GetType().InvokeMember(
                member.Name,
                BindingFlags.InvokeMethod,
                binder: null,
                target,
                args,
                [modifier],
                CultureInfo.InvariantCulture,
                namedParameters: null);
        }
        catch (Exception ex)
        {
            dump.MethodProbes[member.Name + " 调用"] = ObjectDumper.Describe(ex);
            return;
        }

        if (returned is not null)
        {
            var key = member.Name + " 返回值";
            dump.MethodProbes[key] = ObjectDumper.Format(returned);
            Capture(dump, key, member.Name, returned, depth, maxDepth);
        }

        for (var index = 0; index < args.Length; index++)
        {
            var parameter = member.Parameters[index];
            var key = $"{member.Name}.{parameter.Name}";
            dump.MethodProbes[key] = ObjectDumper.Format(args[index]);
            Capture(dump, key, member.Name, args[index], depth, maxDepth);
        }
    }

    private static void Capture(ComDump dump, string key, string method, object? value, int depth, int maxDepth)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        if (depth >= maxDepth || NoCapture.Any(token => method.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            dump.Elements[key] = ObjectDumper.Dump(value, depth + 1, maxDepth);
        }
        catch (Exception ex)
        {
            dump.MethodProbes[key + "(展开)"] = ObjectDumper.Describe(ex);
        }
    }

    /// <summary>
    /// 按形参的真实 VT 构造初值。SAFEARRAY 一律给 3 元素（点、向量、范围点都是 3），
    /// GetMatrix 例外给 16；SE 的 [in,out] SAFEARRAY 惯例是被调方按需重分配。
    /// </summary>
    private static object? Seed(string method, ParameterRecord parameter)
    {
        if (parameter.IsSafeArray)
        {
            var length = method.Contains("Matrix", StringComparison.OrdinalIgnoreCase)
                ? MatrixArrayLength
                : DefaultArrayLength;
            return parameter.VarType switch
            {
                VtR8 => new double[length],
                VtR4 => new float[length],
                VtI4 or VtInt => new int[length],
                VtI2 => new short[length],
                VtBool => new bool[length],
                VtUi1 => new byte[ByteArrayLength],
                VtBstr => new string[length],
                _ => new object?[length],
            };
        }

        return parameter.VarType switch
        {
            VtI4 or VtInt or VtUint => 0,
            VtI2 => (short)0,
            VtR8 => 0d,
            VtR4 => 0f,
            VtBool => false,
            VtBstr => string.Empty,
            VtUi1 => (byte)0,
            VtDispatch or VtUnknown or VtVariant => null,
            _ => null,
        };
    }
}
