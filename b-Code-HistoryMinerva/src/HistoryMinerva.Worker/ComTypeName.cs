using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SE2SW.Worker;

/// <summary>
/// 读一个 COM 对象的**真实接口名**。
///
/// 19 号报告 §1 实测：关系对象的 <c>Type</c> 数值虽然稳定，但接口名才是自解释的判据，
/// 数值只作交叉校验。装配关系有十几种类型、每种成员不同，靠猜成员名来分辨
/// 猜漏了不会抛异常，只会静默少采集——所以这里走类型库。
/// </summary>
internal static class ComTypeName
{
    private const int MemberIdNil = -1;

    public static string? Of(object comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return null;

        ComTypes.ITypeInfo? typeInfo = null;
        try
        {
            if (comObject is not IDispatchLite dispatch)
                return null;
            if (dispatch.GetTypeInfo(0, 0, out typeInfo) != 0 || typeInfo is null)
                return null;
            typeInfo.GetDocumentation(MemberIdNil, out var name, out _, out _, out _);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (typeInfo is not null)
                Marshal.ReleaseComObject(typeInfo);
        }
    }

    /// <summary>IDispatch 的前四个方法必须按 vtable 顺序声明，否则调用会错位到别的槽。</summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchLite
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint lcid, out ComTypes.ITypeInfo? typeInfo);

        [PreserveSig]
        int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint lcid, IntPtr dispIds);

        [PreserveSig]
        int Invoke(
            int dispId,
            ref Guid iid,
            uint lcid,
            ushort flags,
            IntPtr dispParams,
            IntPtr result,
            IntPtr exceptionInfo,
            IntPtr argumentError);
    }
}
