namespace RelationProbe;

/// <summary>
/// 阶段 A：只读采集一个 .asm 的装配关系。不写任何文件，不碰 SolidWorks。
/// </summary>
internal static class SolidEdgeProbe
{
    public static DocumentProbe Probe(SolidEdgeSession session, string path, int maxDepth)
    {
        var result = new DocumentProbe { Path = Path.GetFullPath(path) };
        object? document = null;
        try
        {
            document = session.OpenDocument(result.Path);

            // 先问文档"你身上跟 Relation 有关的成员叫什么"，再决定从哪个入口取集合。
            var (_, _, documentMembers) = TypeLibrary.Describe(document);
            result.RelationEntryPoints = documentMembers
                .Where(member => member.Name.Contains("Relation", StringComparison.OrdinalIgnoreCase))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ToList();
            if (documentMembers.Count == 0)
                result.Notes.Add("AssemblyDocument 读不出类型信息，成员表为空——后续取值全部退化为试探。");

            ReadRelations(document, result, maxDepth);
            result.Occurrences = CollectOccurrences(document, result.Notes);
        }
        catch (Exception ex)
        {
            result.Error = ObjectDumper.Describe(ex);
        }
        finally
        {
            if (document is not null)
                session.CloseDocument(document);
        }

        return result;
    }

    private static void ReadRelations(object document, DocumentProbe result, int maxDepth)
    {
        var candidates = new List<string> { "Relations3d" };
        candidates.AddRange(result.RelationEntryPoints
            .Where(member => member.Kind == "get" && member.ParamCount == 0)
            .Select(member => member.Name));
        candidates.Add("Relations");

        object? collection = null;
        foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            collection = Com.TryGetProperty(document, name);
            if (collection is null)
                continue;
            result.RelationCollectionMember = name;
            break;
        }

        if (collection is null)
        {
            result.RelationCount = 0;
            result.Notes.Add(result.RelationEntryPoints.Count == 0
                ? "文档上没有任何名字含 Relation 的成员，关系集合入口不存在。"
                : "找到了含 Relation 的成员名，但取值全部失败，见 RelationEntryPoints。");
            return;
        }

        try
        {
            result.RelationCollection = ObjectDumper.Dump(collection, depth: maxDepth, maxDepth: maxDepth);
            result.RelationCount = Com.TryGetValue(collection, "Count", -1);

            var items = Com.Enumerate(collection, result.Notes);
            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        var dump = ObjectDumper.Dump(item, depth: 0, maxDepth: maxDepth);
                        RelationReader.Enrich(item, dump);
                        result.Relations.Add(dump);
                    }
                    catch (Exception ex)
                    {
                        result.Notes.Add($"关系对象展开失败：{ObjectDumper.Describe(ex)}");
                    }
                }
            }
            finally
            {
                foreach (var item in items)
                    ObjectDumper.Release(item);
            }

            if (result.RelationCount < 0)
                result.RelationCount = result.Relations.Count;
        }
        finally
        {
            ObjectDumper.Release(collection);
        }
    }

    /// <summary>
    /// 递归采集 occurrence。与生产 explorer 同口径：SubOccurrence.GetMatrix 已经是顶层世界矩阵，
    /// 禁止再与父矩阵累乘。
    /// </summary>
    public static List<OccurrenceRecord> CollectOccurrences(object document, List<string> notes)
    {
        var records = new List<OccurrenceRecord>();
        var occurrences = Com.TryGetProperty(document, "Occurrences");
        if (occurrences is null)
        {
            notes.Add("文档上取不到 Occurrences。");
            return records;
        }

        try
        {
            var items = Com.Enumerate(occurrences, notes);
            try
            {
                foreach (var item in items)
                    ReadOccurrence(item, isSubOccurrence: false, records, notes);
            }
            finally
            {
                foreach (var item in items)
                    ObjectDumper.Release(item);
            }
        }
        finally
        {
            ObjectDumper.Release(occurrences);
        }

        return records;
    }

    private static void ReadOccurrence(
        object occurrence,
        bool isSubOccurrence,
        List<OccurrenceRecord> records,
        List<string> notes)
    {
        var matrix = ObjectDumper.TryReadRefArray(occurrence, "GetMatrix", 16) ?? [];
        var record = new OccurrenceRecord
        {
            Name = Com.TryGetValue(occurrence, "Name", "Occurrence"),
            SourcePath = Com.TryGetValue(
                occurrence,
                isSubOccurrence ? "SubOccurrenceFileName" : "PartFileName",
                string.Empty),
            IsSubAssembly = Com.TryGetValue(occurrence, "Subassembly", false),
            Visible = Com.TryGetValue(occurrence, "Visible", true),
            Matrix = matrix,
            Origin = matrix.Length == 16 ? [matrix[12], matrix[13], matrix[14]] : [],
        };
        records.Add(record);

        if (!record.IsSubAssembly)
            return;

        var children = Com.TryGetProperty(occurrence, "SubOccurrences");
        if (children is null)
        {
            notes.Add($"子装配 {record.Name} 取不到 SubOccurrences。");
            return;
        }

        try
        {
            var items = Com.Enumerate(children, notes);
            try
            {
                foreach (var item in items)
                    ReadOccurrence(item, isSubOccurrence: true, records, notes);
            }
            finally
            {
                foreach (var item in items)
                    ObjectDumper.Release(item);
            }
        }
        finally
        {
            ObjectDumper.Release(children);
        }
    }
}
