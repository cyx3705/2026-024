using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HistoryMinerva.Bom;

/// <summary>
/// 一个 <c>&lt;c&gt;</c> 单元格的待写值。<see cref="IsNumber"/> 决定写成数字还是内联文本。
/// </summary>
internal readonly record struct SheetCellValue(string Column, string Value, bool IsNumber)
{
    public static SheetCellValue Number(string column, int value)
        => new(column, value.ToString(CultureInfo.InvariantCulture), IsNumber: true);

    public static SheetCellValue Text(string column, string? value)
        => new(column, (value ?? string.Empty).Trim(), IsNumber: false);
}

/// <summary>
/// 在**不动模板样式**的前提下往 <c>sheet1.xml</c> 里插数据行。
///
/// 为什么是手写 OOXML 而不是引入 ClosedXML / EPPlus：本仓三个工程至今零 NuGet 依赖，
/// 锁定还原、离线构建和候选包边界都建立在这一点上；而这里要做的事只有一件——
/// 把模板里那一行空数据行复制 N 份、把后面的行整体往下推。为它引一个几 MB 的
/// 电子表格库，换来的是一份新的锁文件、一条新的供应链和一次候选包边界改动。
///
/// **样式必须来自模板行本身**。BOM 是要发给供应商的，边框、字号、列宽、合并格
/// 都是模板作者定好的；自己拼一个 <c>&lt;c&gt;</c> 出来会得到一张没有框线的白表。
/// 所以这里的做法是克隆模板行、只换里面的值。
///
/// 文本一律写成 <c>inlineStr</c>（<c>&lt;is&gt;&lt;t&gt;</c>），不进 <c>sharedStrings.xml</c>：
/// 共享字符串表要同步维护 <c>count</c> / <c>uniqueCount</c> 和全表索引，
/// 而我们对表里已有的字符串一无所知，改错一个索引就是整张表的文字错位。
/// </summary>
internal static class OpenXmlSheetEditor
{
    internal static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>
    /// 把 <paramref name="rows"/> 写进从 <paramref name="firstDataRow"/> 开始的数据区，
    /// 并把它后面的所有行、合并格、维度与筛选区整体下移。
    /// </summary>
    /// <param name="sheet"><c>sheet1.xml</c> 的 XML 文档，就地修改。</param>
    /// <param name="firstDataRow">模板里那一行空数据行的行号，本轮从它开始铺数据。</param>
    /// <param name="rows">每行一组单元格值。空清单时保留模板那一行空行。</param>
    /// <returns>数据区实际占用的行号区间（含两端）。</returns>
    public static (int FirstRow, int LastRow) FillDataRows(
        XDocument sheet,
        int firstDataRow,
        IReadOnlyList<IReadOnlyList<SheetCellValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(rows);
        var sheetData = sheet.Root?.Element(Main + "sheetData")
            ?? throw new InvalidDataException("BOM 模板缺少 sheetData。");
        var templateRow = sheetData.Elements(Main + "row")
                .FirstOrDefault(row => RowIndex(row) == firstDataRow)
            ?? throw new InvalidDataException($"BOM 模板第 {firstDataRow} 行不存在，无法作为数据行样板。");

        var rowCount = Math.Max(rows.Count, 1);
        var delta = rowCount - 1;

        // 先推后面的行，再插新行：反过来做的话新插的行会被自己这一轮再推一次。
        if (delta > 0)
        {
            foreach (var row in sheetData.Elements(Main + "row"))
            {
                var index = RowIndex(row);
                if (index > firstDataRow)
                    SetRowIndex(row, index + delta);
            }

            ShiftReferences(sheet, firstDataRow, delta);
        }

        var built = new List<XElement>(rowCount);
        for (var offset = 0; offset < rowCount; offset++)
        {
            var row = new XElement(templateRow);
            SetRowIndex(row, firstDataRow + offset);
            if (offset < rows.Count)
            {
                foreach (var value in rows[offset])
                    WriteCell(row, value);
            }

            built.Add(row);
        }

        templateRow.ReplaceWith(built);
        return (firstDataRow, firstDataRow + rowCount - 1);
    }

    /// <summary>
    /// 把一条公式重新指到给定区间，并丢掉缓存值。
    ///
    /// 丢缓存值是必须的：模板里存着上一次算出来的结果（多半还是 <c>#REF!</c>），
    /// 不清掉的话 Excel 打开时直接显示那个旧值，要等用户手工重算才会更新——
    /// 而收到表格的是供应商，他不会知道要按 Ctrl+Alt+F9。
    /// </summary>
    public static void SetFormula(XDocument sheet, string cellReference, string formula)
    {
        var cell = FindCell(sheet, cellReference);
        if (cell is null)
            return;
        cell.Attribute("t")?.Remove();
        cell.RemoveNodes();
        cell.Add(new XElement(Main + "f", formula));
    }

    /// <summary>
    /// 删掉计算链。行一挪，<c>calcChain.xml</c> 里记的单元格坐标就全是旧的，
    /// Excel 会判成「文件已损坏，需要修复」。这个部件是纯缓存，删掉后 Excel 自己重建。
    /// </summary>
    public static void RemoveCalculationChain(ZipArchive archive)
    {
        const string calcChainPath = "xl/calcChain.xml";
        var calcChain = archive.GetEntry(calcChainPath);
        if (calcChain is null)
            return;
        calcChain.Delete();

        RewriteXml(archive, "[Content_Types].xml", document =>
        {
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            document.Root?.Elements(types + "Override")
                .Where(item => string.Equals(
                    (string?)item.Attribute("PartName"), "/" + calcChainPath, StringComparison.Ordinal))
                .Remove();
        });

        RewriteXml(archive, "xl/_rels/workbook.xml.rels", document =>
        {
            XNamespace relationships =
                "http://schemas.openxmlformats.org/package/2006/relationships";
            document.Root?.Elements(relationships + "Relationship")
                .Where(item => string.Equals(
                    (string?)item.Attribute("Target"), "calcChain.xml", StringComparison.Ordinal))
                .Remove();
        });
    }

    /// <summary>读一个 zip 部件为 XML。部件不存在时返回 null。</summary>
    public static XDocument? ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>整体替换一个 zip 部件。ZipArchive 不支持原地缩短，只能删了重建。</summary>
    public static void WriteXml(ZipArchive archive, string path, XDocument document)
    {
        archive.GetEntry(path)?.Delete();
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        // Excel 认 UTF-8 无 BOM 的 XML 声明；带 BOM 的部件在部分版本上直接报文件损坏。
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static void RewriteXml(
        ZipArchive archive,
        string path,
        Action<XDocument> edit)
    {
        var document = ReadXml(archive, path);
        if (document is null)
            return;
        edit(document);
        WriteXml(archive, path, document);
    }

    private static void WriteCell(XElement row, SheetCellValue value)
    {
        var rowIndex = RowIndex(row);
        var cell = row.Elements(Main + "c").FirstOrDefault(item =>
            string.Equals(ColumnOf((string?)item.Attribute("r")), value.Column, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
        {
            // 模板行没有这一格（列被整列省略过）。补一个无样式的格总比丢掉数据好。
            cell = new XElement(Main + "c", new XAttribute("r", value.Column + rowIndex));
            row.Add(cell);
        }

        cell.Attribute("t")?.Remove();
        cell.RemoveNodes();
        if (value.Value.Length == 0)
            return;

        if (value.IsNumber)
        {
            cell.Add(new XElement(Main + "v", value.Value));
            return;
        }

        cell.Add(new XAttribute("t", "inlineStr"));
        cell.Add(new XElement(Main + "is", new XElement(Main + "t", value.Value)));
    }

    /// <summary>合并格、维度和自动筛选都按 <c>A1:V10</c> 记范围，行一挪就得跟着挪。</summary>
    private static void ShiftReferences(XDocument sheet, int firstDataRow, int delta)
    {
        var root = sheet.Root;
        if (root is null)
            return;

        foreach (var attribute in root.Descendants()
                     .Where(element => element.Name == Main + "mergeCell"
                         || element.Name == Main + "dimension"
                         || element.Name == Main + "autoFilter")
                     .Select(element => element.Attribute("ref"))
                     .Where(item => item is not null)
                     .ToArray())
        {
            attribute!.Value = ShiftRange(attribute.Value, firstDataRow, delta);
        }
    }

    private static string ShiftRange(string range, int firstDataRow, int delta)
        => string.Join(':', range.Split(':').Select(part => ShiftReference(part, firstDataRow, delta)));

    private static string ShiftReference(string reference, int firstDataRow, int delta)
    {
        var column = ColumnOf(reference);
        var digits = reference[column.Length..];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var row))
            return reference;
        return row > firstDataRow
            ? column + (row + delta).ToString(CultureInfo.InvariantCulture)
            : reference;
    }

    private static XElement? FindCell(XDocument sheet, string cellReference)
        => sheet.Root?.Element(Main + "sheetData")?
            .Elements(Main + "row")
            .Elements(Main + "c")
            .FirstOrDefault(cell => string.Equals(
                (string?)cell.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase));

    private static int RowIndex(XElement row)
        => int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static void SetRowIndex(XElement row, int index)
    {
        row.SetAttributeValue("r", index.ToString(CultureInfo.InvariantCulture));
        foreach (var cell in row.Elements(Main + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            if (string.IsNullOrEmpty(reference))
                continue;
            cell.SetAttributeValue("r", ColumnOf(reference) + index.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary><c>"AB12"</c> → <c>"AB"</c>。空引用返回空串。</summary>
    internal static string ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return string.Empty;
        var length = 0;
        while (length < reference.Length && char.IsAsciiLetter(reference[length]))
            length++;
        return reference[..length].ToUpperInvariant();
    }
}
