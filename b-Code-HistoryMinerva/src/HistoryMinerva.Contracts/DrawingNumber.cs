namespace HistoryMinerva.Contracts;

/// <summary>
/// SolidWorks 属性整备使用的图号。
/// 前缀由用户手写，例如 <c>ZS-LHL</c>；数字段按所选装配体的层级自动生成。
///
/// **V4.9：前缀允许为空**。空前缀不是"非法输入"，而是一个正常状态——它表示
/// 这一轮把图号改成空，也就是删图号。因此文件名规则只有一条：
/// <c>图号 名称.ext</c>，图号为空时退化成 <c>名称.ext</c>。
/// V4.8 之前删图号是另一条"按空格洗"的独立管线，两条路各有一份计划、一份校验、
/// 一份 Worker 分支，而它们算出来的目标文件名本来就是同一个。
/// </summary>
public readonly record struct DrawingNumber(string Prefix, IReadOnlyList<int> Tokens)
{
    public const int AssemblyMarker = 0;

    /// <summary>根装配的编号尾巴。<c>前缀-00</c> 里的那个 <c>00</c>。</summary>
    private const string RootSuffix = "-00";

    public bool IsRootAssembly => Tokens.Count == 1 && Tokens[0] == AssemblyMarker;

    public bool IsMajorAssembly => Tokens.Count == 2 && Tokens[^1] == AssemblyMarker;

    public bool IsMinorAssembly => Tokens.Count == 3 && Tokens[^1] == AssemblyMarker;

    public bool IsAssembly => Tokens.Count > 0 && Tokens[^1] == AssemblyMarker;

    /// <summary>
    /// 渲染出来的图号文本。
    ///
    /// **前缀为空时恒为空串**，而不是 <c>-01</c>：层级序号本身没有意义，它只是挂在前缀
    /// 后面的定位符。前缀一空，整个图号就该消失，文件名只剩名称。
    /// 层级仍然照算——用户把前缀填回来，同一个零件还是同一个号。
    /// </summary>
    public string Text
        => Prefix.Length == 0
            ? string.Empty
            : Tokens.Count == 0
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

    /// <summary>
    /// 归一化前缀。**空前缀合法**（＝删图号），空格和文件名非法字符仍然不收：
    /// 空格会让"图号 名称"这条规则自己解析不了自己，非法字符会让改名当场失败。
    /// </summary>
    public static string NormalizePrefix(string prefix)
    {
        var trimmed = (prefix ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidDataException("图号前缀不能包含空格或文件名非法字符。");
        }

        return trimmed;
    }

    /// <summary>
    /// 按第一个空格把文件名切成「图号段」和「名称」。**这是本模块识别既有图号的唯一口径。**
    ///
    /// 不校验图号段长什么样：现场的零件名里有大量不符合命名规则的旧号
    /// （手写的、别的项目带过来的、带括号的），而这一段马上就要被本轮的新号整体替换掉，
    /// 拿规则去卡它只会把这些文件判成"没有图号"，于是它们的真名被当成图号留在了新名字里。
    ///
    /// 没有空格时图号段为空、整个主名就是名称——一个还没编过号的零件正是这样。
    /// </summary>
    public static void SplitFileName(string fileName, out string drawingToken, out string partName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        var space = stem.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            drawingToken = string.Empty;
            partName = stem;
            return;
        }

        drawingToken = stem[..space].Trim();
        partName = stem[(space + 1)..].Trim();

        // "图号 " 后面什么都没有：那一段不是图号，是这个零件的名字本身。
        if (partName.Length == 0)
        {
            drawingToken = string.Empty;
            partName = stem;
        }
    }

    /// <summary>
    /// 从根装配的文件名推断图号前缀，供导入时预填「图号前缀」框。
    ///
    /// 根装配的号是 <c>前缀-00</c>，所以去掉尾巴上的 <c>-00</c> 就是前缀。
    /// 尾巴不是 <c>-00</c>（用户手工改过、或这个装配本来就不按规则命名）时，
    /// 整段当前缀用——猜错了用户改一个框就是了，而猜"没有前缀"会让整表看起来没编过号。
    /// 文件名里没有空格时返回空串：那是一个还没编号的装配。
    /// </summary>
    public static string InferPrefix(string fileName)
    {
        SplitFileName(fileName, out var token, out _);
        if (token.Length == 0)
            return string.Empty;
        if (token.EndsWith(RootSuffix, StringComparison.Ordinal))
            token = token[..^RootSuffix.Length];
        try
        {
            return NormalizePrefix(token);
        }
        catch (InvalidDataException)
        {
            // 旧名字里带着文件名非法字符（真实存在，例如全角冒号）。宁可留空让用户自己填，
            // 也不能把一个改名一定会失败的前缀预填进去。
            return string.Empty;
        }
    }

    public static bool TryParseFileName(string fileName, string prefix, out DrawingNumber number, out string originalName)
    {
        number = default;
        originalName = Path.GetFileNameWithoutExtension(fileName);
        var normalized = NormalizePrefix(prefix);
        if (normalized.Length == 0)
            return false;
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
    /// 拼文件名：<c>图号 名称.ext</c>。图号为空（删图号）时只剩 <c>名称.ext</c>，
    /// 不留那个会长在最前面的空格。
    /// </summary>
    public static string FormatFileName(DrawingNumber number, string originalName, string extension)
        => FormatFileName(number.Text, originalName, extension);

    /// <inheritdoc cref="FormatFileName(DrawingNumber,string,string)"/>
    public static string FormatFileName(string drawingText, string originalName, string extension)
    {
        var name = (originalName ?? string.Empty).Trim();
        var number = (drawingText ?? string.Empty).Trim();
        if (number.Length == 0)
            return name + extension;
        return name.Length == 0 ? number + extension : number + " " + name + extension;
    }
}
