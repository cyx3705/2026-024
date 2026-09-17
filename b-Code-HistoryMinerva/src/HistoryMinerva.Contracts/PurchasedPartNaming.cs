namespace HistoryMinerva.Contracts;

/// <summary>
/// 把外购件的文件名切成「规格」和「名称」两栏。
///
/// 外购件清单模板里这两栏的现场约定是：**名称写中文，规格写型号**。V4.11.1 起按空白切成词，
/// 以词为单位判断——含汉字的词归名称，不含汉字的词归规格：
///
/// <code>
/// GB70 M8x20 内六角螺钉  →  规格 "GB70 M8x20"    名称 "内六角螺钉"
/// 深沟球轴承 6205        →  规格 "6205"          名称 "深沟球轴承"
/// SKF-6205-2RS           →  规格 "SKF-6205-2RS"  名称 ""（没有汉字就留空）
/// 油封 TC-25-40-7        →  规格 "TC-25-40-7"    名称 "油封"
/// G型O型圈 G56           →  规格 "G56"           名称 "G型O型圈"
/// M8x20内六角螺钉GB70    →  规格 "M8x20 GB70"    名称 "内六角螺钉"
/// </code>
///
/// **含汉字的词整体算名称**，词里夹着的字母（<c>G型O型圈</c> 的 G、O）是名字的一部分，
/// 不能抠出来当规格——V4.10 逐字符分拣时这个名字被拆成规格 <c>GO</c>、名称 <c>型型圈</c>（DEC-069）。
/// 唯一的例外是词头或词尾**贴着一段明显的型号**：不含汉字且带数字（<c>M8x20</c>、<c>GB70</c>），
/// 那一段剥出去归规格。没有数字的前缀（<c>G型</c> 的 G）不算明显的型号，留在名称里。
///
/// 「汉字」只认 CJK 表意文字，不含全角标点：<c>BNTB-M20（1.0）</c> 是一个型号，不是名称。
///
/// **不按第一个空格切**（图号那条规则）：标准件的名字里中文既可能在前也可能在后，
/// 按位置切会把「深沟球轴承 6205」的规格写成「深沟球轴承」。
/// </summary>
public static class PurchasedPartNaming
{
    /// <param name="fileName">外购件文件名，带不带扩展名都可以。</param>
    /// <param name="specification">不含汉字的词与剥出来的型号段，按原顺序以单个空格连接。</param>
    /// <param name="name">含汉字的词，按原顺序以单个空格连接；没有汉字时是空串。</param>
    public static void Split(string fileName, out string specification, out string name)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var specifications = new List<string>();
        var names = new List<string>();
        foreach (var word in stem.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var first = IndexOfIdeograph(word);
            if (first < 0)
            {
                specifications.Add(word);
                continue;
            }

            var last = LastIndexOfIdeograph(word);
            var start = 0;
            var end = word.Length;
            var head = word[..first];
            if (IsObviousSpecification(head))
            {
                specifications.Add(head);
                start = first;
            }

            var tail = word[(last + 1)..];
            if (IsObviousSpecification(tail))
                end = last + 1;

            names.Add(word[start..end]);
            if (end < word.Length)
                specifications.Add(tail);
        }

        specification = string.Join(' ', specifications);
        name = string.Join(' ', names);
    }

    /// <summary>贴在汉字前后的一段能不能算型号：非空且带数字。</summary>
    private static bool IsObviousSpecification(string segment)
        => segment.Any(char.IsAsciiDigit);

    private static int IndexOfIdeograph(string word)
    {
        for (var index = 0; index < word.Length; index++)
        {
            if (IsIdeograph(word[index]))
                return index;
        }

        return -1;
    }

    private static int LastIndexOfIdeograph(string word)
    {
        for (var index = word.Length - 1; index >= 0; index--)
        {
            if (IsIdeograph(word[index]))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// 中日韩表意文字。范围照抄 Unicode 分区，不靠 <c>char.GetUnicodeCategory</c>；
    /// 全角标点与全角字母不算——它们常出现在型号里（<c>BNTB-M20（1.0）</c>）。
    /// </summary>
    private static bool IsIdeograph(char character) => character
        is >= '一' and <= '鿿'      // CJK 统一表意文字
        or >= '㐀' and <= '䶿'      // 扩展 A
        or >= '豈' and <= '﫿';     // 兼容表意文字
}
