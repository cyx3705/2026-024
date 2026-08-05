using System.Runtime.InteropServices;
using COM = System.Runtime.InteropServices.ComTypes;

namespace RelationProbe;

/// <summary>
/// 通过 IDispatch::GetTypeInfo 读出 COM 对象的真实成员表。
///
/// 这是本探针与既有探针的关键差别：不再"猜成员名 + 吞异常"，而是先问类型库
/// "你到底有什么"，再按答案取值。猜漏一个成员不会抛异常，只会静默少采集，
/// 对十几种关系类型来说这种静默丢失是不可接受的。
/// </summary>
internal static class TypeLibrary
{
    // IDispatch。只声明前两个方法（GetTypeInfoCount / GetTypeInfo），
    // 后面的 GetIDsOfNames / Invoke 不声明也不调用，vtable 槽位不受影响。
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchLite
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint lcid, [MarshalAs(UnmanagedType.Interface)] out COM.ITypeInfo typeInfo);
    }

    private const int MemberIdNil = -1;
    private const int MaxInheritanceDepth = 8;

    /// <summary>取对象默认接口的名称；拿不到时返回 null。</summary>
    public static string? GetInterfaceName(object comObject)
    {
        var typeInfo = TryGetTypeInfo(comObject);
        if (typeInfo is null)
            return null;
        try
        {
            typeInfo.GetDocumentation(MemberIdNil, out var name, out _, out _, out _);
            return name;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(typeInfo);
        }
    }

    /// <summary>
    /// 展开对象的成员表，含继承链。返回的 members 已按 (名称, 种类) 去重。
    /// </summary>
    public static (string InterfaceName, List<string> TypeChain, List<MemberRecord> Members) Describe(object comObject)
    {
        var members = new List<MemberRecord>();
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var typeInfo = TryGetTypeInfo(comObject);
        if (typeInfo is null)
            return ("<无类型信息>", chain, members);

        try
        {
            Walk(typeInfo, 0, chain, members, seen);
        }
        finally
        {
            Release(typeInfo);
        }

        return (chain.Count > 0 ? chain[0] : "<无类型信息>", chain, members);
    }

    private static void Walk(
        COM.ITypeInfo typeInfo,
        int depth,
        List<string> chain,
        List<MemberRecord> members,
        HashSet<string> seen)
    {
        if (depth >= MaxInheritanceDepth)
            return;

        string typeName;
        try
        {
            typeInfo.GetDocumentation(MemberIdNil, out typeName, out _, out _, out _);
        }
        catch
        {
            return;
        }

        // IDispatch / IUnknown 的成员对探针没有价值，且会污染成员表。
        if (typeName is "IDispatch" or "IUnknown")
            return;
        if (!chain.Contains(typeName, StringComparer.Ordinal))
            chain.Add(typeName);

        var attributePointer = IntPtr.Zero;
        try
        {
            typeInfo.GetTypeAttr(out attributePointer);
            if (attributePointer == IntPtr.Zero)
                return;
            var attribute = Marshal.PtrToStructure<COM.TYPEATTR>(attributePointer);

            for (var index = 0; index < attribute.cFuncs; index++)
                ReadFunction(typeInfo, index, members, seen);

            for (var index = 0; index < attribute.cVars; index++)
                ReadVariable(typeInfo, index, members, seen);

            for (var index = 0; index < attribute.cImplTypes; index++)
            {
                COM.ITypeInfo? parent = null;
                try
                {
                    typeInfo.GetRefTypeOfImplType(index, out var handle);
                    typeInfo.GetRefTypeInfo(handle, out parent);
                    if (parent is not null)
                        Walk(parent, depth + 1, chain, members, seen);
                }
                catch
                {
                    // 继承链读不动就停在这一层，已采集的成员仍然有效。
                }
                finally
                {
                    Release(parent);
                }
            }
        }
        catch
        {
            // 单个类型读失败不影响其他类型。
        }
        finally
        {
            if (attributePointer != IntPtr.Zero)
                typeInfo.ReleaseTypeAttr(attributePointer);
        }
    }

    private static void ReadFunction(COM.ITypeInfo typeInfo, int index, List<MemberRecord> members, HashSet<string> seen)
    {
        var pointer = IntPtr.Zero;
        try
        {
            typeInfo.GetFuncDesc(index, out pointer);
            if (pointer == IntPtr.Zero)
                return;
            var description = Marshal.PtrToStructure<COM.FUNCDESC>(pointer);
            typeInfo.GetDocumentation(description.memid, out var name, out _, out _, out _);
            if (string.IsNullOrEmpty(name))
                return;
            Add(members, seen, new MemberRecord
            {
                Name = name,
                Kind = DescribeInvokeKind(description.invkind),
                ParamCount = description.cParams,
                MemberId = description.memid,
                Parameters = ReadParameters(typeInfo, description),
                ReturnType = DescribeType(typeInfo, description.elemdescFunc.tdesc, 0),
            });
        }
        catch
        {
            // 单个成员描述读失败可以跳过。
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                typeInfo.ReleaseFuncDesc(pointer);
        }
    }

    private static void ReadVariable(COM.ITypeInfo typeInfo, int index, List<MemberRecord> members, HashSet<string> seen)
    {
        var pointer = IntPtr.Zero;
        try
        {
            typeInfo.GetVarDesc(index, out pointer);
            if (pointer == IntPtr.Zero)
                return;
            var description = Marshal.PtrToStructure<COM.VARDESC>(pointer);
            typeInfo.GetDocumentation(description.memid, out var name, out _, out _, out _);
            if (string.IsNullOrEmpty(name))
                return;
            Add(members, seen, new MemberRecord
            {
                Name = name,
                Kind = "get",
                ParamCount = 0,
                MemberId = description.memid,
            });
        }
        catch
        {
            // 同上。
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                typeInfo.ReleaseVarDesc(pointer);
        }
    }

    /// <summary>
    /// 读出方法的形参签名。GetElement1(1 参) / GetGeometry1(7 参) 这类方法无法靠猜调用，
    /// 但类型库里写着每个形参的类型与 in/out 方向——读出来即可。
    /// </summary>
    private static List<ParameterRecord> ReadParameters(COM.ITypeInfo typeInfo, COM.FUNCDESC description)
    {
        var parameters = new List<ParameterRecord>();
        if (description.cParams <= 0 || description.lprgelemdescParam == IntPtr.Zero)
            return parameters;

        var names = ReadParameterNames(typeInfo, description.memid, description.cParams);
        var size = Marshal.SizeOf<COM.ELEMDESC>();
        for (var index = 0; index < description.cParams; index++)
        {
            var label = index < names.Count ? names[index] : $"参数{index + 1}";
            try
            {
                var element = Marshal.PtrToStructure<COM.ELEMDESC>(
                    IntPtr.Add(description.lprgelemdescParam, index * size));
                var flags = element.desc.paramdesc.wParamFlags;
                var direction = new List<string>();
                if (flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FIN)) direction.Add("in");
                if (flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FOUT)) direction.Add("out");
                if (flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FOPT)) direction.Add("opt");
                if (flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FRETVAL)) direction.Add("retval");

                var (variantType, isSafeArray) = ResolveType(element.tdesc, 0);
                parameters.Add(new ParameterRecord
                {
                    Name = label,
                    Type = DescribeType(typeInfo, element.tdesc, 0)
                        + (direction.Count > 0 ? " [" + string.Join(",", direction) + "]" : string.Empty),
                    VarType = variantType,
                    IsSafeArray = isSafeArray,
                    IsIn = flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FIN),
                    IsOut = flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FOUT),
                    IsOptional = flags.HasFlag(COM.PARAMFLAG.PARAMFLAG_FOPT),
                });
            }
            catch (Exception ex)
            {
                parameters.Add(new ParameterRecord { Name = label, Type = $"<读取失败 {ex.Message}>" });
            }
        }

        return parameters;
    }

    /// <summary>穿过 VT_PTR / VT_SAFEARRAY，得到实际要构造的值的类型。</summary>
    private static (int VarType, bool IsSafeArray) ResolveType(COM.TYPEDESC descriptor, int depth)
    {
        var variantType = (int)descriptor.vt;
        if (depth >= MaxTypeDepth || descriptor.lpValue == IntPtr.Zero)
            return (variantType, false);

        try
        {
            if (variantType == VtPointer)
                return ResolveType(Marshal.PtrToStructure<COM.TYPEDESC>(descriptor.lpValue), depth + 1);
            if (variantType == VtSafeArray)
            {
                var (element, _) = ResolveType(Marshal.PtrToStructure<COM.TYPEDESC>(descriptor.lpValue), depth + 1);
                return (element, true);
            }
        }
        catch
        {
            return (variantType, false);
        }

        return (variantType, false);
    }

    private static List<string> ReadParameterNames(COM.ITypeInfo typeInfo, int memberId, int paramCount)
    {
        try
        {
            // GetNames 的第一个名字是方法名，其后才是形参名。
            var buffer = new string[paramCount + 1];
            typeInfo.GetNames(memberId, buffer, buffer.Length, out var count);
            return buffer.Take(Math.Max(0, count)).Skip(1).ToList();
        }
        catch
        {
            return [];
        }
    }

    private const int VtPointer = 26;
    private const int VtUserDefined = 29;
    private const int VtSafeArray = 27;
    private const int MaxTypeDepth = 4;

    private static readonly Dictionary<int, string> VariantTypes = new()
    {
        [0] = "VT_EMPTY", [1] = "VT_NULL", [2] = "VT_I2", [3] = "VT_I4", [4] = "VT_R4",
        [5] = "VT_R8", [6] = "VT_CY", [7] = "VT_DATE", [8] = "VT_BSTR", [9] = "VT_DISPATCH",
        [10] = "VT_ERROR", [11] = "VT_BOOL", [12] = "VT_VARIANT", [13] = "VT_UNKNOWN",
        [14] = "VT_DECIMAL", [16] = "VT_I1", [17] = "VT_UI1", [18] = "VT_UI2", [19] = "VT_UI4",
        [20] = "VT_I8", [21] = "VT_UI8", [22] = "VT_INT", [23] = "VT_UINT", [24] = "VT_VOID",
        [25] = "VT_HRESULT", [26] = "VT_PTR", [27] = "VT_SAFEARRAY", [28] = "VT_CARRAY",
        [29] = "VT_USERDEFINED", [30] = "VT_LPSTR", [31] = "VT_LPWSTR",
    };

    /// <summary>把 TYPEDESC 展开成可读类型名；指针与用户定义类型各追一层，让 out 参数的真实类型露出来。</summary>
    private static string DescribeType(COM.ITypeInfo typeInfo, COM.TYPEDESC descriptor, int depth)
    {
        var variantType = (int)descriptor.vt;
        var name = VariantTypes.GetValueOrDefault(variantType, $"VT_{variantType}");
        if (depth >= MaxTypeDepth)
            return name;

        try
        {
            if (variantType is VtPointer or VtSafeArray && descriptor.lpValue != IntPtr.Zero)
            {
                var inner = Marshal.PtrToStructure<COM.TYPEDESC>(descriptor.lpValue);
                return name + "→" + DescribeType(typeInfo, inner, depth + 1);
            }

            if (variantType == VtUserDefined)
            {
                // 用户定义类型时 lpValue 直接是 HREFTYPE，不是指针。
                typeInfo.GetRefTypeInfo((int)descriptor.lpValue, out var referenced);
                try
                {
                    referenced.GetDocumentation(MemberIdNil, out var typeName, out _, out _, out _);
                    return $"{name}({typeName})";
                }
                finally
                {
                    Release(referenced);
                }
            }
        }
        catch
        {
            return name + "(解析失败)";
        }

        return name;
    }

    private static void Add(List<MemberRecord> members, HashSet<string> seen, MemberRecord record)
    {
        if (seen.Add(record.Name + "|" + record.Kind))
            members.Add(record);
    }

    private static string DescribeInvokeKind(COM.INVOKEKIND kind) => kind switch
    {
        COM.INVOKEKIND.INVOKE_PROPERTYGET => "get",
        COM.INVOKEKIND.INVOKE_PROPERTYPUT => "put",
        COM.INVOKEKIND.INVOKE_PROPERTYPUTREF => "putref",
        _ => "method",
    };

    private static COM.ITypeInfo? TryGetTypeInfo(object comObject)
    {
        if (!Marshal.IsComObject(comObject))
            return null;
        try
        {
            if (comObject is not IDispatchLite dispatch)
                return null;
            return dispatch.GetTypeInfo(0, 0, out var typeInfo) == 0 ? typeInfo : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;
        try { Marshal.ReleaseComObject(comObject); } catch { }
    }
}
