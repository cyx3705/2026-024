using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.11 件别：属性整备与整体打包两张表共用的「机加件 / 外购件 / 参考」改写记账。
/// </summary>
public sealed partial class AssemblyViewModel
{
    /// <summary>
    /// 用户改写过的件别，按源文件全路径记账。没记的按路径取缺省（<see cref="PartKinds.Default"/>）。
    ///
    /// 与名称、材料记账同一个理由住在这里而不是行上：改一次前缀或件别整份计划重建，
    /// 行是派生的，这份账才是权威。与名称记账不同的是**重新解析不清它**——
    /// 件别不在磁盘上，重新去 SolidWorks 认一遍也认不回来，清掉就等于让用户再点一遍。
    /// 换了来源装配才清（<see cref="ClearSourceResults"/>）。
    /// </summary>
    private readonly Dictionary<string, PackagePartCategory> _kindEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这一行现在的件别。行不存在时返回 null。</summary>
    internal PackagePartCategory? GetPartKind(string rowId)
        => FindRow(rowId) is { } row && _sourceAssemblyPath.Length > 0
            ? PartKinds.Resolve(_kindEdits, row.SourcePath, _sourceAssemblyPath)
            : null;

    /// <summary>
    /// 点一下件别格：换到下一种，并按新件别重建当前那张表。
    /// </summary>
    /// <returns>换到的件别；行不存在、正在执行操作或件别锁定时返回 null，<paramref name="reason"/> 说明原因。</returns>
    internal PackagePartCategory? CyclePartKind(string rowId, out string reason)
    {
        reason = string.Empty;
        if (!CanEdit)
        {
            reason = "正在执行操作，件别未修改。";
            return null;
        }

        if (FindRow(rowId) is not { } row || _sourceAssemblyPath.Length == 0 || _probeResult is null)
        {
            reason = "请先解析装配体，再改件别。";
            return null;
        }

        if (PartKinds.IsFixed(row.SourcePath))
        {
            reason = $"「{ConversionPathLayout.ReferencePartsDirectoryName}」目录下的文件固定为参考，件别不能改。";
            return null;
        }

        var current = PartKinds.Resolve(_kindEdits, row.SourcePath, _sourceAssemblyPath);
        var next = PartKinds.Next(current, row.SourcePath, _sourceAssemblyPath);
        var key = Path.GetFullPath(row.SourcePath);
        // 改回缺省就把记账删掉：记账里只留真正偏离路径规则的件，换了文件夹结构也不会被旧记账顶着。
        if (next == PartKinds.Default(row.SourcePath, _sourceAssemblyPath))
            _kindEdits.Remove(key);
        else
            _kindEdits[key] = next;

        // 两张表都按件别算：属性整备要重排编号，打包要重算清单。
        // 本轮若已经完成，件别一变结论就不再是这一张表的结论，允许再来一轮。
        _conversionCompleted = false;
        ApplyRenamePreview();
        ApplyPackagePreview();
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanWrite));
        return next;
    }

    /// <summary>件别格的显示值。没有装配体来源时留空。</summary>
    private string KindCell(string sourcePath)
        => _sourceAssemblyPath.Length == 0
            ? string.Empty
            : PartKinds.Cell(PartKinds.Resolve(_kindEdits, sourcePath, _sourceAssemblyPath));

    /// <summary>改名收工后件别记账跟着文件搬到新名字上。</summary>
    private void RemapKindEdits(IReadOnlyDictionary<string, string> moved)
    {
        if (_kindEdits.Count == 0 || moved.Count == 0)
            return;
        var remapped = RenameReindex.RemapKeys(
            new Dictionary<string, PackagePartCategory>(_kindEdits, StringComparer.OrdinalIgnoreCase), moved);
        _kindEdits.Clear();
        foreach (var pair in remapped)
            _kindEdits[pair.Key] = pair.Value;
    }
}
