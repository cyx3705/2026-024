using System.Reflection;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V3.5 §2：从一个已打开的 Solid Edge 装配文档读出它**自己那一层**的装配关系。
///
/// 全部取法照抄 19 号报告的实测结论，不猜：
///   • 入口 <c>Relations3d</c>，<c>Item(1..Count)</c>，索引基 1；
///   • 接口名走类型库（<see cref="ComTypeName"/>），不靠成员名试探；
///   • 几何走 <c>GetGeometryN</c>——它是唯一对所有关系类型都成立的来源，
///     轴关系拿不到 <c>Face</c>（实测 0/20 恒 E_FAIL）；
///   • byref 参数必须按真实 VT 播种，传空 VARIANT 会恒返回 DISP_E_TYPEMISMATCH；
///   • 条件可读的成员先读开关再读值，读不出来是正常状态而不是故障。
///
/// 参考系：调用方必须把该 <c>.asm</c> 作为**独立顶层文档**打开，
/// 这样 <c>GetGeometryN</c> 的"世界系"就是该文档自己的坐标系（25 号文档 §3.2）。
/// </summary>
internal static class SolidEdgeRelationReader
{
    public static List<AssemblyRelation> Read(
        dynamic document,
        string assemblyPath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var relations = new List<AssemblyRelation>();
        object? collectionObject = null;
        try
        {
            collectionObject = document.Relations3d;
        }
        catch (Exception ex)
        {
            warnings.Add($"无法打开装配关系集合：{Path.GetFileName(assemblyPath)}：{ex.Message}");
            return relations;
        }

        if (collectionObject is null)
            return relations;

        try
        {
            dynamic collection = collectionObject;
            int count;
            try
            {
                count = Convert.ToInt32(collection.Count);
            }
            catch (Exception ex)
            {
                warnings.Add($"无法读取装配关系数量：{Path.GetFileName(assemblyPath)}：{ex.Message}");
                return relations;
            }

            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? relationObject = null;
                try
                {
                    relationObject = collection.Item(index);
                    if (relationObject is not null)
                        relations.Add(ReadOne(relationObject, assemblyPath, index));
                }
                catch (Exception ex)
                {
                    warnings.Add($"跳过读不出的装配关系 #{index}（{Path.GetFileName(assemblyPath)}）：{ex.Message}");
                }
                finally
                {
                    ComRelease.Final(relationObject);
                }
            }
        }
        finally
        {
            ComRelease.Final(collectionObject);
        }

        return relations;
    }

    private static AssemblyRelation ReadOne(object relationObject, string assemblyPath, int index)
    {
        dynamic relation = relationObject;
        var interfaceName = ComTypeName.Of(relationObject) ?? "UnknownRelation3d";
        var diagnostics = new List<string>();
        var isSuppressed = TryGet(() => Convert.ToBoolean(relation.Suppress), false);

        string? occurrence1 = null;
        string? occurrence2 = null;
        if (string.Equals(interfaceName, MateTypeMapperNames.Ground, StringComparison.Ordinal))
        {
            occurrence1 = ReadOccurrenceName(relation, "Occurrence");
        }
        else
        {
            occurrence1 = ReadOccurrenceName(relation, "Occurrence1");
            occurrence2 = ReadOccurrenceName(relation, "Occurrence2");
        }

        // 条件可读：ParallelOffset 是开关，false 时读 Offset 抛 0x80004005。
        // 绝不能反过来用 Offset 是否为 0 去推断类型（那会把同轴误判成零距离配合）。
        var parallelOffset = TryGet(() => Convert.ToBoolean(relation.ParallelOffset), false);
        var normalsAligned = TryGet(() => Convert.ToBoolean(relation.NormalsAligned), false);

        var offset = 0d;
        var isAxial = string.Equals(interfaceName, MateTypeMapperNames.Axial, StringComparison.Ordinal);
        var isGround = string.Equals(interfaceName, MateTypeMapperNames.Ground, StringComparison.Ordinal);
        // Ground 关系本来就没有 Offset；Axial 在 ParallelOffset=false 时读它会抛 0x80004005。
        // 两者都是正常状态，不去读，也不产出诊断——诊断只留给真正的意外。
        if (!isGround && (!isAxial || parallelOffset))
        {
            if (!TryGet(() => Convert.ToDouble(relation.Offset), out var value))
                diagnostics.Add("Offset 不可读");
            else
                offset = value;
        }

        RelationGeometry? geometry1 = null;
        RelationGeometry? geometry2 = null;
        if (!string.Equals(interfaceName, MateTypeMapperNames.Ground, StringComparison.Ordinal))
        {
            geometry1 = ReadGeometry(relationObject, 1, diagnostics);
            geometry2 = ReadGeometry(relationObject, 2, diagnostics);
        }

        return new AssemblyRelation(
            Path.GetFullPath(assemblyPath),
            index,
            interfaceName,
            occurrence1,
            occurrence2,
            geometry1,
            geometry2,
            offset,
            normalsAligned,
            parallelOffset,
            isSuppressed,
            diagnostics.Count == 0 ? null : string.Join("；", diagnostics));
    }

    /// <summary>
    /// <c>GetGeometryN(out I4 类型, out R8 点XYZ, out R8 向量XYZ)</c>。
    /// 七个参数全部是 out，按真实 VT 播种后一次成功；传空 VARIANT 恒 DISP_E_TYPEMISMATCH。
    /// </summary>
    private static RelationGeometry? ReadGeometry(object relationObject, int side, List<string> diagnostics)
    {
        var member = "GetGeometry" + side.ToString();
        object[] args = [0, 0d, 0d, 0d, 0d, 0d, 0d];
        var modifiers = new ParameterModifier(args.Length);
        for (var index = 0; index < args.Length; index++)
            modifiers[index] = true;

        try
        {
            relationObject.GetType().InvokeMember(
                member,
                BindingFlags.InvokeMethod,
                binder: null,
                target: relationObject,
                args: args,
                modifiers: [modifiers],
                culture: null,
                namedParameters: null);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"{member} 不可读：{ex.Message}");
            return null;
        }

        try
        {
            var point = new[] { Convert.ToDouble(args[1]), Convert.ToDouble(args[2]), Convert.ToDouble(args[3]) };
            var direction = new[] { Convert.ToDouble(args[4]), Convert.ToDouble(args[5]), Convert.ToDouble(args[6]) };
            if (point.Concat(direction).Any(value => !double.IsFinite(value)))
            {
                diagnostics.Add($"{member} 返回非有限值");
                return null;
            }

            return new RelationGeometry(Convert.ToInt32(args[0]), point, direction);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"{member} 结果解析失败：{ex.Message}");
            return null;
        }
    }

    private static string? ReadOccurrenceName(dynamic relation, string member)
    {
        object? occurrence = null;
        try
        {
            occurrence = relation.GetType().InvokeMember(
                member, BindingFlags.GetProperty, null, relation, null);
            return occurrence is null ? null : Convert.ToString(((dynamic)occurrence).Name);
        }
        catch
        {
            return null;
        }
        finally
        {
            ComRelease.One(occurrence);
        }
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static bool TryGet(Func<double> getter, out double value)
    {
        try { value = getter(); return true; }
        catch { value = 0; return false; }
    }
}

/// <summary>接口名常量。与 UI 侧 <c>MateTypeMapper</c> 的同名常量必须一致。</summary>
internal static class MateTypeMapperNames
{
    public const string Ground = "GroundRelation3d";
    public const string Axial = "AxialRelation3d";
    public const string Planar = "PlanarRelation3d";
}
