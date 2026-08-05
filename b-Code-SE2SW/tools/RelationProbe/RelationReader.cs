namespace RelationProbe;

/// <summary>
/// 关系对象的补采。几何引用与几何数据由 <see cref="MethodProbe"/> 的通用带参试探覆盖
/// （<c>GetElement1/2</c>、<c>GetGeometry1/2</c>）；这里只负责把两侧组件归属压成标量。
/// </summary>
internal static class RelationReader
{
    private static readonly string[] OccurrenceMembers =
        ["Occurrence1", "Occurrence2", "Occurrence", "Part1", "Part2", "Part"];

    /// <summary>
    /// 两侧组件只留"实例名 | 零件路径"。整个 Occurrence 对象图递归下去会把输出撑到几十 MB，
    /// 而 V3.5 需要的只是"这条关系挂在哪两个组件上"。
    /// </summary>
    public static void Enrich(object relation, ComDump dump)
    {
        foreach (var name in OccurrenceMembers)
        {
            var occurrence = Com.TryGetProperty(relation, name);
            if (occurrence is null)
                continue;
            try
            {
                var instance = Com.TryGetValue(occurrence, "Name", string.Empty);
                var path = Com.TryGetValue(occurrence, "PartFileName", string.Empty);
                if (string.IsNullOrEmpty(path))
                    path = Com.TryGetValue(occurrence, "SubOccurrenceFileName", string.Empty);
                dump.Occurrences[name] = $"{instance} | {path}";
            }
            finally
            {
                ObjectDumper.Release(occurrence);
            }
        }
    }
}
