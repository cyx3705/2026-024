using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace HistoryMinerva.Bom;

/// <summary>
/// V4.11：把刚写好的 BOM 画成一张同名 PNG，放在 BOM 旁边。
///
/// 现场的做法是开 Excel 截一张表发给供应商和采购，让人在聊天窗口里不开附件就看得到清单。
/// 这里**不驱动 Excel**：在 Vulcan 宿主进程里起 Excel COM，一个弹窗或一次挂起就把整个宿主拖住，
/// 而机器上也未必装着 Excel。改为直接读 xlsx 里的版式事实——列宽、行高、合并格、边框、
/// 填充、字体、对齐——用 WPF 按 Excel 的像素口径画出来。画的是写好的那个文件，
/// 所以截图与表格永远是同一份内容，不会出现两处各算一遍对不上的情况。
///
/// 只画 <c>dimension</c> 声明的区域；公式格没有缓存值时留空（交付的报价单由供应商填价）。
/// </summary>
internal static class BomSheetImageRenderer
{
    private const string SheetPath = "xl/worksheets/sheet1.xml";

    /// <summary>
    /// 默认字体（等线 11 号）的最大数字宽度，像素。Excel 列宽单位是「字符数」，
    /// 换算成像素是 <c>宽度 × 数字宽 + 5</c>：缺省 8.38 字符正好是 72 像素。
    /// </summary>
    private const double MaxDigitWidth = 8;

    private const double DefaultColumnWidth = 8.38;
    private const double DefaultRowHeightPoints = 14.25;

    /// <summary>输出分辨率。1.5 倍接近现场截图时 Excel 的缩放，文字在聊天窗口里也看得清。</summary>
    private const double Scale = 1.5;

    private const double CellPadding = 2;

    private static readonly FontFamily FallbackFont = new("等线, 微软雅黑, Microsoft YaHei, 宋体, SimSun");

    /// <summary>读 <paramref name="workbookPath"/>，把 sheet1 画成 PNG 写到 <paramref name="imagePath"/>。</summary>
    public static void Render(string workbookPath, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var sheet = SheetModel.Load(workbookPath);

        // WPF 的绘制对象只能活在 STA 线程上。打包跑在线程池上，这里单开一条线程画完就走，
        // 不去碰宿主的 UI 线程——那条线程此刻正在刷进度。
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var png = Draw(sheet);
                WriteAtomically(imagePath, png);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "HistoryMinerva.BomImage",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException($"BOM 截图生成失败：{failure.Message}", failure);
    }

    private static byte[] Draw(SheetModel sheet)
    {
        var columnLefts = Offsets(sheet.FirstColumn, sheet.LastColumn, sheet.ColumnWidth);
        var rowTops = Offsets(sheet.FirstRow, sheet.LastRow, sheet.RowHeight);
        var width = columnLefts[^1];
        var height = rowTops[^1];

        Rect CellRect(int row, int column, int lastRow, int lastColumn)
            => new(
                new Point(columnLefts[column - sheet.FirstColumn], rowTops[row - sheet.FirstRow]),
                new Point(columnLefts[lastColumn - sheet.FirstColumn + 1], rowTops[lastRow - sheet.FirstRow + 1]));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            // 第一遍：填充与文字。合并格只在左上角那一格画一次，其余被覆盖的格跳过。
            for (var row = sheet.FirstRow; row <= sheet.LastRow; row++)
            {
                for (var column = sheet.FirstColumn; column <= sheet.LastColumn; column++)
                {
                    var merge = sheet.MergeAt(row, column);
                    if (merge is { } covered && (covered.Top != row || covered.Left != column))
                        continue;

                    var rect = merge is { } region
                        ? CellRect(region.Top, region.Left, Math.Min(region.Bottom, sheet.LastRow), Math.Min(region.Right, sheet.LastColumn))
                        : CellRect(row, column, row, column);
                    var cell = sheet.Cell(row, column);
                    var style = sheet.Style(cell?.StyleIndex ?? sheet.RowStyle(row));
                    if (style.Fill is { } fill)
                        context.DrawRectangle(new SolidColorBrush(fill), null, rect);

                    if (cell is null || cell.Text.Length == 0)
                        continue;

                    var textRect = rect;
                    if (merge is null && !style.Wrap && IsLeftFlowing(style, cell))
                    {
                        // Excel 的溢出：不换行的左对齐文字可以一路画进右边的空格子里。
                        var right = column;
                        while (right < sheet.LastColumn
                               && sheet.MergeAt(row, right + 1) is null
                               && sheet.Cell(row, right + 1) is not { Text.Length: > 0 })
                        {
                            right++;
                        }

                        textRect = CellRect(row, column, row, right);
                    }

                    DrawText(context, cell, style, textRect);
                }
            }

            // 第二遍：边框。每一格画自己声明的边，合并区内部的边不画——与 Excel 的显示一致。
            for (var row = sheet.FirstRow; row <= sheet.LastRow; row++)
            {
                for (var column = sheet.FirstColumn; column <= sheet.LastColumn; column++)
                {
                    var cell = sheet.Cell(row, column);
                    var style = sheet.Style(cell?.StyleIndex ?? sheet.RowStyle(row));
                    if (style.Border.IsEmpty)
                        continue;
                    var rect = CellRect(row, column, row, column);
                    var merge = sheet.MergeAt(row, column);
                    if (style.Border.Left > 0 && (merge is null || merge.Value.Left == column))
                        DrawEdge(context, style.Border.Left, rect.TopLeft, rect.BottomLeft);
                    if (style.Border.Right > 0 && (merge is null || merge.Value.Right == column))
                        DrawEdge(context, style.Border.Right, rect.TopRight, rect.BottomRight);
                    if (style.Border.Top > 0 && (merge is null || merge.Value.Top == row))
                        DrawEdge(context, style.Border.Top, rect.TopLeft, rect.TopRight);
                    if (style.Border.Bottom > 0 && (merge is null || merge.Value.Bottom == row))
                        DrawEdge(context, style.Border.Bottom, rect.BottomLeft, rect.BottomRight);
                }
            }
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * Scale),
            (int)Math.Ceiling(height * Scale),
            96 * Scale,
            96 * Scale,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>「常规」对齐下文字靠左、数字靠右；只有靠左的会溢出到右边。</summary>
    private static bool IsLeftFlowing(CellStyle style, CellValue cell)
        => style.Horizontal switch
        {
            "left" => true,
            null or "" or "general" => !cell.IsNumber,
            _ => false,
        };

    private static void DrawText(DrawingContext context, CellValue cell, CellStyle style, Rect rect)
    {
        var typeface = new Typeface(
            style.FontFamily,
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        FormattedText Format(string content)
            => new(
                content,
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                typeface,
                style.FontSizePoints * 96 / 72,
                new SolidColorBrush(style.FontColor),
                Scale);

        var available = Math.Max(1, rect.Width - (2 * CellPadding));
        var wrap = style.Wrap;
        var text = Format(wrap ? cell.Text : SingleLine(cell.Text));
        if (wrap)
        {
            text.MaxTextWidth = available;
            // 行高是模板定死的，换行后放不下就退回单行裁切：半截上下各露一点的两行字比一行裁掉尾巴难认。
            // 单行时连单元格里的硬换行一起去掉，与 Excel 不换行时的显示一致。
            if (text.Height > rect.Height)
            {
                text = Format(SingleLine(cell.Text));
                wrap = false;
            }
        }

        var horizontal = style.Horizontal switch
        {
            "center" or "centerContinuous" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            "left" => TextAlignment.Left,
            _ => cell.IsNumber ? TextAlignment.Right : TextAlignment.Left,
        };
        double x = horizontal switch
        {
            TextAlignment.Center => rect.Left + ((rect.Width - text.WidthIncludingTrailingWhitespace) / 2),
            TextAlignment.Right => rect.Right - CellPadding - text.WidthIncludingTrailingWhitespace,
            _ => rect.Left + CellPadding,
        };
        if (wrap)
        {
            // 换行文字按行宽排版，对齐交给 FormattedText 自己做。
            text.TextAlignment = horizontal;
            x = rect.Left + CellPadding;
        }

        double y = style.Vertical switch
        {
            "top" => rect.Top + 1,
            "center" => rect.Top + ((rect.Height - text.Height) / 2),
            _ => rect.Bottom - 1 - text.Height,
        };

        context.PushClip(new RectangleGeometry(rect));
        context.DrawText(text, new Point(x, y));
        context.Pop();
    }

    private static string SingleLine(string text)
        => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    private static void DrawEdge(DrawingContext context, double thickness, Point from, Point to)
    {
        var pen = new Pen(Brushes.Black, thickness);
        // 像素对齐：细线落在半像素上会被抗锯齿糊成两像素的灰线。
        var half = thickness / 2;
        var guidelines = new GuidelineSet();
        guidelines.GuidelinesX.Add(from.X + half);
        guidelines.GuidelinesX.Add(to.X + half);
        guidelines.GuidelinesY.Add(from.Y + half);
        guidelines.GuidelinesY.Add(to.Y + half);
        context.PushGuidelineSet(guidelines);
        context.DrawLine(pen, from, to);
        context.Pop();
    }

    /// <summary>从第一个到最后一个的累计偏移，末尾多一项作为总长。</summary>
    private static double[] Offsets(int first, int last, Func<int, double> size)
    {
        var offsets = new double[last - first + 2];
        for (var index = first; index <= last; index++)
            offsets[index - first + 1] = offsets[index - first] + size(index);
        return offsets;
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // ------------------------------------------------------------------ 版式读数

    private readonly record struct MergeRegion(int Top, int Left, int Bottom, int Right);

    private sealed record CellValue(string Text, bool IsNumber, int StyleIndex);

    /// <summary>四条边的线宽，0 表示没有边。</summary>
    private readonly record struct BorderSides(double Left, double Right, double Top, double Bottom)
    {
        public bool IsEmpty => Left <= 0 && Right <= 0 && Top <= 0 && Bottom <= 0;
    }

    private sealed record CellStyle(
        FontFamily FontFamily,
        double FontSizePoints,
        bool Bold,
        bool Italic,
        Color FontColor,
        Color? Fill,
        BorderSides Border,
        string? Horizontal,
        string? Vertical,
        bool Wrap);

    private sealed class SheetModel
    {
        private static readonly XNamespace Main = OpenXmlSheetEditor.Main;

        private readonly Dictionary<(int Row, int Column), CellValue> _cells = [];
        private readonly Dictionary<int, double> _columnWidths = [];
        private readonly Dictionary<int, double> _rowHeights = [];
        private readonly Dictionary<int, int> _rowStyles = [];
        private readonly List<MergeRegion> _merges = [];
        private readonly List<CellStyle> _styles = [];
        private CellStyle _defaultStyle = null!;
        private double _defaultRowHeight = DefaultRowHeightPoints;
        private double _defaultColumnWidth = DefaultColumnWidth;

        public int FirstRow { get; private set; } = 1;
        public int LastRow { get; private set; } = 1;
        public int FirstColumn { get; private set; } = 1;
        public int LastColumn { get; private set; } = 1;

        public static SheetModel Load(string workbookPath)
        {
            using var archive = ZipFile.OpenRead(workbookPath);
            var sheet = OpenXmlSheetEditor.ReadXml(archive, SheetPath)
                ?? throw new InvalidDataException($"BOM 缺少 {SheetPath}。");
            var model = new SheetModel();
            model.ReadStyles(archive);
            model.ReadSheet(sheet, ReadSharedStrings(archive));
            return model;
        }

        public CellValue? Cell(int row, int column) => _cells.GetValueOrDefault((row, column));

        public int RowStyle(int row) => _rowStyles.GetValueOrDefault(row);

        public CellStyle Style(int index)
            => index >= 0 && index < _styles.Count ? _styles[index] : _defaultStyle;

        public MergeRegion? MergeAt(int row, int column)
        {
            foreach (var merge in _merges)
            {
                if (row >= merge.Top && row <= merge.Bottom && column >= merge.Left && column <= merge.Right)
                    return merge;
            }

            return null;
        }

        public double ColumnWidth(int column)
            => Math.Round((_columnWidths.TryGetValue(column, out var width) ? width : _defaultColumnWidth)
                          * MaxDigitWidth + 5);

        public double RowHeight(int row)
            => Math.Round((_rowHeights.TryGetValue(row, out var points) ? points : _defaultRowHeight) * 96 / 72);

        private void ReadSheet(XDocument sheet, IReadOnlyList<string> shared)
        {
            var root = sheet.Root!;
            if (root.Element(Main + "sheetFormatPr") is { } format)
            {
                _defaultRowHeight = ReadDouble(format, "defaultRowHeight") ?? _defaultRowHeight;
                _defaultColumnWidth = ReadDouble(format, "defaultColWidth") ?? _defaultColumnWidth;
            }

            foreach (var column in root.Element(Main + "cols")?.Elements(Main + "col") ?? [])
            {
                var min = (int?)column.Attribute("min") ?? 0;
                var max = (int?)column.Attribute("max") ?? min;
                var hidden = (string?)column.Attribute("hidden") is "1" or "true";
                var width = hidden ? 0 : ReadDouble(column, "width") ?? _defaultColumnWidth;
                // 隐藏列画成零宽：换算公式里的 +5 不能加给它。
                for (var index = min; index <= max && index <= 16384; index++)
                    _columnWidths[index] = hidden ? -5 / MaxDigitWidth : width;
            }

            var lastRow = 1;
            var lastColumn = 1;
            foreach (var row in root.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
            {
                var rowIndex = (int?)row.Attribute("r") ?? 0;
                if (rowIndex <= 0)
                    continue;
                if ((string?)row.Attribute("hidden") is "1" or "true")
                    _rowHeights[rowIndex] = 0;
                else if (ReadDouble(row, "ht") is { } height)
                    _rowHeights[rowIndex] = height;
                if ((string?)row.Attribute("customFormat") is "1" or "true")
                    _rowStyles[rowIndex] = (int?)row.Attribute("s") ?? 0;

                foreach (var cell in row.Elements(Main + "c"))
                {
                    var reference = (string?)cell.Attribute("r");
                    if (!TryParseReference(reference, out var cellRow, out var cellColumn))
                        continue;
                    var value = ReadValue(cell, shared, out var isNumber);
                    _cells[(cellRow, cellColumn)] = new CellValue(value, isNumber, (int?)cell.Attribute("s") ?? 0);
                    lastRow = Math.Max(lastRow, cellRow);
                    lastColumn = Math.Max(lastColumn, cellColumn);
                }
            }

            foreach (var merge in root.Element(Main + "mergeCells")?.Elements(Main + "mergeCell") ?? [])
            {
                var parts = ((string?)merge.Attribute("ref") ?? string.Empty).Split(':');
                if (parts.Length == 2
                    && TryParseReference(parts[0], out var top, out var left)
                    && TryParseReference(parts[1], out var bottom, out var right))
                {
                    _merges.Add(new MergeRegion(top, left, bottom, right));
                }
            }

            // dimension 是 Excel 自己记的已用区域，比按单元格推算更贴近「用户看到的那张表」。
            var dimension = ((string?)root.Element(Main + "dimension")?.Attribute("ref") ?? string.Empty).Split(':');
            if (dimension.Length == 2
                && TryParseReference(dimension[0], out var firstRow, out var firstColumn)
                && TryParseReference(dimension[1], out var dimensionRow, out var dimensionColumn))
            {
                FirstRow = firstRow;
                FirstColumn = firstColumn;
                LastRow = dimensionRow;
                LastColumn = dimensionColumn;
            }
            else
            {
                LastRow = lastRow;
                LastColumn = lastColumn;
            }

            // 模板作者把视图滚到了哪里，截图就从哪里开始：机加件清单顶上两行是内部备注，
            // 现场截图从来不带它们（视图左上角停在 A3）。
            var topLeft = (string?)root.Element(Main + "sheetViews")?.Element(Main + "sheetView")?.Attribute("topLeftCell");
            if (TryParseReference(topLeft, out var viewRow, out var viewColumn))
            {
                FirstRow = Math.Clamp(viewRow, FirstRow, LastRow);
                FirstColumn = Math.Clamp(viewColumn, FirstColumn, LastColumn);
            }
        }

        private static string ReadValue(XElement cell, IReadOnlyList<string> shared, out bool isNumber)
        {
            isNumber = false;
            var type = (string?)cell.Attribute("t");
            switch (type)
            {
                case "inlineStr":
                    return string.Concat(cell.Element(Main + "is")?.Descendants(Main + "t").Select(text => text.Value) ?? []);
                case "s":
                    return int.TryParse(cell.Element(Main + "v")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                           && index >= 0 && index < shared.Count
                        ? shared[index]
                        : string.Empty;
                case "str":
                case "e":
                    return cell.Element(Main + "v")?.Value ?? string.Empty;
                case "b":
                    return cell.Element(Main + "v")?.Value == "1" ? "TRUE" : "FALSE";
                default:
                    var raw = cell.Element(Main + "v")?.Value ?? string.Empty;
                    if (raw.Length == 0)
                        return string.Empty;
                    isNumber = true;
                    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? number.ToString("0.##########", CultureInfo.InvariantCulture)
                        : raw;
            }
        }

        private void ReadStyles(ZipArchive archive)
        {
            var theme = ReadThemeColors(archive);
            var styles = OpenXmlSheetEditor.ReadXml(archive, "xl/styles.xml")?.Root;
            var fonts = styles?.Element(Main + "fonts")?.Elements(Main + "font").ToArray() ?? [];
            var fills = styles?.Element(Main + "fills")?.Elements(Main + "fill").ToArray() ?? [];
            var borders = styles?.Element(Main + "borders")?.Elements(Main + "border").ToArray() ?? [];

            CellStyle Build(XElement? xf)
            {
                var font = At(fonts, (int?)xf?.Attribute("fontId") ?? 0);
                var fill = At(fills, (int?)xf?.Attribute("fillId") ?? 0);
                var border = At(borders, (int?)xf?.Attribute("borderId") ?? 0);
                var alignment = xf?.Element(Main + "alignment");
                var name = (string?)font?.Element(Main + "name")?.Attribute("val");
                return new CellStyle(
                    string.IsNullOrWhiteSpace(name) ? FallbackFont : new FontFamily(name + ", " + FallbackFont.Source),
                    ReadDouble(font?.Element(Main + "sz"), "val") ?? 11,
                    IsOn(font?.Element(Main + "b")),
                    IsOn(font?.Element(Main + "i")),
                    ResolveColor(font?.Element(Main + "color"), theme) ?? Colors.Black,
                    ReadFill(fill, theme),
                    new BorderSides(
                        EdgeWidth(border?.Element(Main + "left")),
                        EdgeWidth(border?.Element(Main + "right")),
                        EdgeWidth(border?.Element(Main + "top")),
                        EdgeWidth(border?.Element(Main + "bottom"))),
                    (string?)alignment?.Attribute("horizontal"),
                    (string?)alignment?.Attribute("vertical") ?? "bottom",
                    (string?)alignment?.Attribute("wrapText") is "1" or "true");
            }

            _defaultStyle = Build(null);
            foreach (var xf in styles?.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
                _styles.Add(Build(xf));
        }

        private static Color? ReadFill(XElement? fill, IReadOnlyList<Color> theme)
        {
            var pattern = fill?.Element(Main + "patternFill");
            if ((string?)pattern?.Attribute("patternType") != "solid")
                return null;
            return ResolveColor(pattern!.Element(Main + "fgColor"), theme);
        }

        private static double EdgeWidth(XElement? edge)
            => (string?)edge?.Attribute("style") switch
            {
                null or "" or "none" => 0,
                "medium" or "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "double" => 2,
                "thick" => 3,
                _ => 1,
            };

        private static Color? ResolveColor(XElement? color, IReadOnlyList<Color> theme)
        {
            if (color is null)
                return null;
            Color? baseColor = null;
            if ((string?)color.Attribute("rgb") is { Length: >= 6 } rgb)
                baseColor = ParseHex(rgb[^6..]);
            else if ((int?)color.Attribute("theme") is { } themeIndex && themeIndex >= 0 && themeIndex < theme.Count)
                baseColor = theme[themeIndex];
            else if ((int?)color.Attribute("indexed") is { } indexed)
                baseColor = indexed switch
                {
                    1 or 9 => Colors.White,
                    2 or 10 => Colors.Red,
                    22 => Color.FromRgb(0xC0, 0xC0, 0xC0),
                    23 => Color.FromRgb(0x80, 0x80, 0x80),
                    _ => Colors.Black,
                };
            else if ((string?)color.Attribute("auto") is "1" or "true")
                baseColor = Colors.Black;

            if (baseColor is not { } value)
                return null;
            var tint = ReadDouble(color, "tint") ?? 0;
            return tint == 0 ? value : ApplyTint(value, tint);
        }

        /// <summary>Excel 的明暗调：负数按比例压暗，正数向白色推。</summary>
        private static Color ApplyTint(Color color, double tint)
        {
            byte Channel(byte channel)
                => (byte)Math.Clamp(
                    Math.Round(tint < 0 ? channel * (1 + tint) : channel + ((255 - channel) * tint)), 0, 255);

            return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
        }

        /// <summary>主题色表。Excel 的主题索引把前两对的深浅对调：0=lt1、1=dk1、2=lt2、3=dk2。</summary>
        private static IReadOnlyList<Color> ReadThemeColors(ZipArchive archive)
        {
            XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var scheme = OpenXmlSheetEditor.ReadXml(archive, "xl/theme/theme1.xml")?
                .Descendants(drawing + "clrScheme").FirstOrDefault();
            Color Read(string name, Color fallback)
            {
                var slot = scheme?.Element(drawing + name);
                var hex = (string?)slot?.Element(drawing + "srgbClr")?.Attribute("val")
                          ?? (string?)slot?.Element(drawing + "sysClr")?.Attribute("lastClr");
                return hex is { Length: 6 } ? ParseHex(hex) : fallback;
            }

            return
            [
                Read("lt1", Colors.White),
                Read("dk1", Colors.Black),
                Read("lt2", Color.FromRgb(0xE8, 0xE8, 0xE8)),
                Read("dk2", Color.FromRgb(0x0E, 0x28, 0x41)),
                .. new[] { "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" }
                    .Select(name => Read(name, Colors.Black)),
            ];
        }

        private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
            => OpenXmlSheetEditor.ReadXml(archive, "xl/sharedStrings.xml")?.Root?
                   .Elements(Main + "si")
                   .Select(item => string.Concat(item.Descendants(Main + "t").Select(text => text.Value)))
                   .ToArray()
               ?? [];

        private static bool TryParseReference(string? reference, out int row, out int column)
        {
            row = 0;
            column = 0;
            var letters = OpenXmlSheetEditor.ColumnOf(reference);
            if (letters.Length == 0 || reference is null)
                return false;
            foreach (var letter in letters)
                column = (column * 26) + (letter - 'A' + 1);
            return int.TryParse(reference[letters.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out row)
                   && row > 0;
        }

        private static XElement? At(XElement[] items, int index)
            => index >= 0 && index < items.Length ? items[index] : null;

        private static bool IsOn(XElement? flag)
            => flag is not null && (string?)flag.Attribute("val") is null or "1" or "true";

        private static double? ReadDouble(XElement? element, string attribute)
            => double.TryParse(
                (string?)element?.Attribute(attribute),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : null;

        private static Color ParseHex(string hex)
            => Color.FromRgb(
                byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
