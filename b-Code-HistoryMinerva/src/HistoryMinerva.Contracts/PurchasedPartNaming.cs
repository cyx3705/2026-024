using System.Text;

namespace HistoryMinerva.Contracts;

/// <summary>
/// V4.10：把外购件的文件名切成「规格」和「名称」两栏。
///
/// 外购件清单模板里这两栏的现场约定是：**名称写中文，规格写型号**。所以判据不是
/// 位置也不是分隔符，而是字符本身——中文字符归名称，其余归规格：
///
/// <code>
/// GB70 M8x20 内六角螺钉  →  规格 "GB70 M8x20"    名称 "内六角螺钉"
/// 深沟球轴承 6205        →  规格 "6205"          名称 "深沟球轴承"
/// SKF-6205-2RS           →  规格 "SKF-6205-2RS"  名称 ""（没有中文就留空）
/// 油封 TC-25-40-7        →  规格 "TC-25-40-7"    名称 "油封"
/// </code>
///
/// **不按第一个空格切**（图号那条规则）：标准件的名字里中文既可能在前也可能在后，
/// 按位置切会把「深沟球轴承 6205」的规格写成「深沟球轴承」。
///
/// 空白只当软分隔符：它同时进两个桶，因此中文与型号交替出现时各自那一段仍然分得开，
/// 但「M8x20内六角螺钉GB70」这种中英夹杂无空格的名字，规格会拼成 <c>M8x20GB70</c>——
/// 这是本规则已知的边界，靠给文件名留个空格就能避开。
/// </summary>
public static class PurchasedPartNaming
{
    /// <param name="fileName">外购件文件名，带不带扩展名都可以。</param>
    /// <param name="specification">名称里的非中文字段，已折叠多余空白。</param>
    /// <param name="name">名称里的中文字段，已折叠多余空白；没有中文时是空串。</param>
    public static void Split(string fileName, out string specification, out string name)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var specificationBuilder = new StringBuilder(stem.Length);
        var nameBuilder = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            if (char.IsWhiteSpace(character))
            {
                // 软分隔符：两个桶都收一个空格，谁都不会把隔着空白的两段粘成一个词。
                specificationBuilder.Append(' ');
                nameBuilder.Append(' ');
            }
            else if (IsChinese(character))
            {
                nameBuilder.Append(character);
            }
            else
            {
                specificationBuilder.Append(character);
            }
        }

        specification = Collapse(specificationBuilder);
        name = Collapse(nameBuilder);
    }

    /// <summary>
    /// 中日韩表意文字与中文标点。范围照抄 Unicode 分区，不靠 <c>char.GetUnicodeCategory</c>——
    /// 那个把全角括号和拉丁括号归成同一类，型号里的半角括号会被判成中文。
    /// </summary>
    private static bool IsChinese(char character) => character
        is >= '一' and <= '鿿'      // CJK 统一表意文字
        or >= '㐀' and <= '䶿'      // 扩展 A
        or >= '豈' and <= '﫿'      // 兼容表意文字
        or >= '　' and <= '〿'      // 中文标点
        or >= '！' and <= '･';     // 全角字符

    /// <summary>折叠连续空白并去掉两端空白。软分隔符会留下一串空格，这里收干净。</summary>
    private static string Collapse(StringBuilder builder)
    {
        var result = new StringBuilder(builder.Length);
        var pendingSpace = false;
        foreach (var character in builder.ToString())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
                result.Append(' ');
            pendingSpace = false;
            result.Append(character);
        }

        return result.ToString();
    }
}
