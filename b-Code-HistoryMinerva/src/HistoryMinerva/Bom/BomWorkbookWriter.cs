using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using HistoryMinerva.Contracts;

namespace HistoryMinerva.Bom;

/// <summary>
/// V4.10：按现场模板生成两张 BOM。
///
/// 模板是**权威**，不是参考：两个 <c>.xlsx</c> 原样嵌在 <c>HistoryMinerva.dll</c> 里
/// （见 <c>Assets/</c>），本类只往里插数据行，从不重建表头、边框或合并格。
/// 换模板＝换那两个文件，不改代码。
///
/// 只填用户点名的那几列，其余全部留空：
///
/// <list type="bullet">
///   <item><b>机加件清单</b>（数据从第 6 行起）：A 序号、B 零件图号、C 零件名称、D 数量。
///         E..V 是供应商填的报价栏。</item>
///   <item><b>外购件清单</b>（数据从第 6 行起）：A 序号、D 规格、E 名称、F 数量、
///         H 品牌（表头「备注[参考供应商]」，V4.10.4）。B 工位、C 物料编码、G 交货时间、I 备注留给采购。</item>
/// </list>
/// </summary>
public static class BomWorkbookWriter
{
    private const string SheetPath = "xl/worksheets/sheet1.xml";
    private const int FirstDataRow = 6;

    /// <summary>嵌入资源名。<c>&lt;EmbeddedResource&gt;</c> 按目录拼前缀，改路径要同步改这里。</summary>
    private const string MachinedTemplateResource = "HistoryMinerva.Assets.机加件清单.xlsx";
    private const string PurchasedTemplateResource = "HistoryMinerva.Assets.外购件清单.xlsx";

    /// <summary>写机加件清单。</summary>
    /// <param name="plan">本轮打包计划，只取 <see cref="PackagePlan.Machined"/>。</param>
    /// <param name="outputPath">产物全路径，落在 <c>BOM/</c> 目录里。</param>
    /// <returns>写进去的数据行数。</returns>
    public static int WriteMachined(PackagePlan plan, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var entries = plan.Machined;
        var rows = entries.Select((entry, index) => (IReadOnlyList<SheetCellValue>)
        [
            SheetCellValue.Number("A", index + 1),
            SheetCellValue.Text("B", entry.DrawingNumber),
            SheetCellValue.Text("C", entry.PartName),
            SheetCellValue.Number("D", entry.Quantity),
        ]).ToArray();

        Write(MachinedTemplateResource, outputPath, rows, (sheet, first, last) =>
        {
            // 模板里这两条公式已经是 #REF!（原始数据区在做成模板时被整块删掉了）。
            // 行一插，它们仍然是 #REF!，而这张表是要发给供应商的——交付一份带
            // 三个错误值的报价单，比重新指一次范围更糟。语义按表头逐字对上：
            //   A9「单价总计」→ D9 = 含税单价(U)那一列之和
            //   G9「总价」    → H9 = 数量(D) 与 含税单价(U) 的乘积和
            OpenXmlSheetEditor.SetFormula(sheet, RowShifted("D9", first, last), $"SUM(U{first}:U{last})");
            OpenXmlSheetEditor.SetFormula(
                sheet, RowShifted("H9", first, last), $"SUMPRODUCT(D{first}:D{last},U{first}:U{last})");
        });
        return entries.Count;
    }

    /// <summary>写外购件清单。</summary>
    /// <param name="plan">本轮打包计划，只取 <see cref="PackagePlan.Purchased"/>。</param>
    /// <param name="outputPath">产物全路径，落在 <c>BOM/</c> 目录里。</param>
    /// <param name="brands">
    /// V4.10.4：按 <see cref="PackagePartEntry.Id"/> 取的品牌，写进 H 列。为 null 或缺这一行时 H 列留空——
    /// 「没查过」与「查过、写 N/A」在清单上必须分得开。
    /// </param>
    /// <param name="names">
    /// V4.16：AI 补出来的名称，按 <see cref="PackagePartEntry.Id"/> 取。只有文件名里没有名称的外购件才会有这一项，
    /// 有就写进 E 列；文件名里本来有的名称永远不被替换。
    /// </param>
    /// <returns>写进去的数据行数（合并后的）。</returns>
    public static int WritePurchased(
        PackagePlan plan,
        string outputPath,
        IReadOnlyDictionary<string, string>? brands = null,
        IReadOnlyDictionary<string, string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = MergePurchased(plan.Purchased, names);
        var rows = lines.Select((line, index) => (IReadOnlyList<SheetCellValue>)
        [
            SheetCellValue.Number("A", index + 1),
            SheetCellValue.Text("D", line.Entry.Specification),
            SheetCellValue.Text("E", line.Name),
            SheetCellValue.Number("F", line.Quantity),
            .. BrandCell(brands, line.Entry),
        ]).ToArray();

        Write(PurchasedTemplateResource, outputPath, rows, afterFill: null);
        return lines.Count;
    }

    /// <summary>外购件清单上的一行：取这一行的那个件（排在最上面的那个）、写上去的名称与合计数量。</summary>
    internal sealed record PurchasedLine(PackagePartEntry Entry, string Name, int Quantity);

    /// <summary>
    /// V4.16：同规格、同名称的外购件只占一行，数量加到排在最上面的那一行上（用户的话：下面的有重叠就在上方加数量）。
    ///
    /// 计划里已经按路径合过一次（同一个文件出现多次）；这里合的是**不同文件、同一种货**——
    /// 同一颗螺钉被复制进两个文件夹，采购买的仍是一种。顺序沿用计划的（特征树从上到下），合并不改先后。
    /// 规格和名称都按不分大小写、去首尾空白比较；两样都空的不合并（说不清是不是同一种）。
    /// </summary>
    internal static IReadOnlyList<PurchasedLine> MergePurchased(
        IReadOnlyList<PackagePartEntry> entries,
        IReadOnlyDictionary<string, string>? names = null)
    {
        var lines = new List<PurchasedLine>(entries.Count);
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = entry.PartName.Length == 0 && names is not null && names.TryGetValue(entry.Id, out var found)
                ? found
                : entry.PartName;
            var key = entry.Specification.Trim() + "\u001f" + name.Trim();
            if (key.Length > 1 && indexByKey.TryGetValue(key, out var index))
            {
                lines[index] = lines[index] with { Quantity = lines[index].Quantity + entry.Quantity };
                continue;
            }

            if (key.Length > 1)
                indexByKey[key] = lines.Count;
            lines.Add(new PurchasedLine(entry, name, entry.Quantity));
        }

        return lines;
    }

    private static SheetCellValue[] BrandCell(IReadOnlyDictionary<string, string>? brands, PackagePartEntry entry)
        => brands is not null && brands.TryGetValue(entry.Id, out var brand)
            ? [SheetCellValue.Text("H", brand)]
            : [];

    private static void Write(
        string resourceName,
        string outputPath,
        IReadOnlyList<IReadOnlyList<SheetCellValue>> rows,
        Action<XDocument, int, int>? afterFill)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var workbook = new MemoryStream();
        using (var template = OpenTemplate(resourceName))
            template.CopyTo(workbook);

        using (var archive = new ZipArchive(workbook, ZipArchiveMode.Update, leaveOpen: true))
        {
            var sheet = OpenXmlSheetEditor.ReadXml(archive, SheetPath)
                ?? throw new InvalidDataException($"BOM 模板缺少 {SheetPath}。");
            var (first, last) = OpenXmlSheetEditor.FillDataRows(sheet, FirstDataRow, rows);
            afterFill?.Invoke(sheet, first, last);
            OpenXmlSheetEditor.WriteXml(archive, SheetPath, sheet);
            OpenXmlSheetEditor.RemoveCalculationChain(archive);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        // 先落临时文件再原子替换：直接往目标写，中途失败会留下半个 zip，
        // 而用户下一步就是把这个目录整个发给供应商。
        var temporaryPath = outputPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, workbook.ToArray());
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// 数据行插入以后，模板里原本在数据区之后的那些行都往下挪了。
    /// 公式所在的行号要跟着挪，否则会写进一个空行里。
    /// </summary>
    private static string RowShifted(string reference, int firstDataRow, int lastDataRow)
    {
        var column = OpenXmlSheetEditor.ColumnOf(reference);
        var row = int.Parse(reference[column.Length..], System.Globalization.CultureInfo.InvariantCulture);
        var delta = lastDataRow - firstDataRow;
        return column + (row > firstDataRow ? row + delta : row);
    }

    private static Stream OpenTemplate(string resourceName)
    {
        var stream = typeof(BomWorkbookWriter).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"模块里没有嵌入 BOM 模板 {resourceName}。请检查 HistoryMinerva.csproj 的 EmbeddedResource 声明。");

        // Git LFS 指针识别。模板在仓库里显式退出了 LFS（见 Assets/.gitattributes），
        // 但如果有人把那条规则改回去，没装 LFS 的克隆会把一段 130 字节的文本编进
        // dll，而构建照样成功——报「不是有效的 zip」等于什么都没说。
        var head = new byte[64];
        var read = stream.Read(head, 0, head.Length);
        stream.Position = 0;
        if (Encoding.ASCII.GetString(head, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal))
        {
            stream.Dispose();
            throw new InvalidDataException(
                $"嵌入的 BOM 模板 {resourceName} 是一个 Git LFS 指针，不是真正的 xlsx。"
                + "这份构建用的源码树没有拉取 LFS 对象，请先 git lfs pull 再重新构建模块。");
        }

        return stream;
    }
}
