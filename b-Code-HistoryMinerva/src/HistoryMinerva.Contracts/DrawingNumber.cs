namespace HistoryMinerva.Contracts;

/// <summary>
/// SolidWorks 属性整备（本版仅改名）使用的图号。
/// 前缀由用户手写，例如 <c>ZS-LHL</c>；数字段按所选装配体的层级自动生成。
/// </summary>
public readonly record struct DrawingNumber(string Prefix, IReadOnlyList<int> Tokens)
{
    public const int AssemblyMarker = 0;

    public bool IsRootAssembly => Tokens.Count == 1 && Tokens[0] == AssemblyMarker;

    public bool IsMajorAssembly => Tokens.Count == 2 && Tokens[^1] == AssemblyMarker;

    public bool IsMinorAssembly => Tokens.Count == 3 && Tokens[^1] == AssemblyMarker;

    public bool IsAssembly => Tokens.Count > 0 && Tokens[^1] == AssemblyMarker;

    public string Text
        => Tokens.Count == 0
            ? Prefix
            : Prefix + "-" + string.Join("-", Tokens.Select(token => token.ToString("00")));

    public DrawingNumber Child(int sequence, bool asAssembly)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (IsRootAssembly)
        {
            return asAssembly
                ? new DrawingNumber(Prefix, [sequence, AssemblyMarker])
                : new DrawingNumber(Prefix, [sequence]);
        }

        if (IsMajorAssembly)
        {
            return asAssembly
                ? new DrawingNumber(Prefix, [Tokens[0], sequence, AssemblyMarker])
                : new DrawingNumber(Prefix, [Tokens[0], sequence]);
        }

        // 小组件只允许零件层级；更深的装配体一律按零件编号。
        var stem = Tokens.Count > 0 && Tokens[^1] == AssemblyMarker
            ? Tokens.Take(Tokens.Count - 1).ToArray()
            : Tokens.ToArray();
        var tokens = new int[stem.Length + 1];
        stem.CopyTo(tokens, 0);
        tokens[^1] = sequence;
        return new DrawingNumber(Prefix, tokens);
    }

    public static DrawingNumber RootAssembly(string prefix)
        => new(NormalizePrefix(prefix), [AssemblyMarker]);

    public static string NormalizePrefix(string prefix)
    {
        var trimmed = prefix.Trim();
        if (trimmed.Length == 0)
            throw new InvalidDataException("图号前缀不能为空。");
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidDataException("图号前缀不能包含空格或文件名非法字符。");
        }

        return trimmed;
    }

    public static bool TryParseFileName(string fileName, string prefix, out DrawingNumber number, out string originalName)
    {
        number = default;
        originalName = Path.GetFileNameWithoutExtension(fileName);
        var normalized = NormalizePrefix(prefix);
        var stem = originalName;
        if (!stem.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = stem[normalized.Length..];
        if (rest.Length == 0 || rest[0] != '-')
            return false;

        rest = rest[1..];
        var tokens = new List<int>();
        while (rest.Length > 0)
        {
            var separator = rest.IndexOfAny(['-', ' ']);
            var segment = separator < 0 ? rest : rest[..separator];
            if (segment.Length == 0 || !int.TryParse(segment, out var token) || token < 0)
                break;
            tokens.Add(token);
            if (separator < 0)
            {
                rest = string.Empty;
                break;
            }

            if (rest[separator] == ' ')
            {
                rest = rest[(separator + 1)..];
                break;
            }

            rest = rest[(separator + 1)..];
        }

        if (tokens.Count == 0)
            return false;

        number = new DrawingNumber(normalized, tokens);
        originalName = rest.Trim();
        return true;
    }

    /// <summary>
    /// 按第一个空格洗掉图号：<c>图号 原名称.ext</c> → 原名称。
    /// 没有空格、空格在两端或洗完后原名为空时返回 false。
    /// </summary>
    public static bool TryStripBySpace(string fileName, out string drawingToken, out string originalName)
    {
        drawingToken = string.Empty;
        originalName = Path.GetFileNameWithoutExtension(fileName);
        var space = originalName.IndexOf(' ');
        if (space <= 0)
            return false;

        drawingToken = originalName[..space].Trim();
        originalName = originalName[(space + 1)..].Trim();
        return drawingToken.Length > 0 && originalName.Length > 0;
    }

    public static string FormatFileName(DrawingNumber number, string originalName, string extension)
    {
        var name = originalName.Trim();
        return string.IsNullOrEmpty(name)
            ? number.Text + extension
            : number.Text + " " + name + extension;
    }
}
