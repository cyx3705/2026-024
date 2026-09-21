using System.IO;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.11 件别：属性整备与整体打包两张表共用的「机加件 / 外购件 / 排除」改写记账。
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
            reason = $"「{ConversionPathLayout.ReferencePartsDirectoryName}」目录下的文件固定为排除，件别不能改。";
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

    /// <summary>
    /// V4.12 统一设置件别：把当前表里每一行都设成 <paramref name="kind"/>，然后重建两张表。
    ///
    /// 只动当前看得见的行，与「一键刷满材料」同一个口径。设成自制组件后新展开出来的内部件
    /// 仍按各自缺省，底下的框因此可能退回「件别」——那是实话：这一列确实不再统一。
    /// 改不了的行跳过而不是整批拒绝：<c>参考部件</c> 目录下的件固定为排除；
    /// 子文件夹里的装配体不能当自制组件（见 <see cref="PartKinds.CanBeMachined"/>）。
    /// </summary>
    /// <returns>实际改动的行数；正在执行或尚未解析时返回 null，<paramref name="reason"/> 说明原因。</returns>
    internal int? SetAllPartKinds(PackagePartCategory kind, out int skipped, out string reason)
    {
        skipped = 0;
        reason = string.Empty;
        if (!CanEdit)
        {
            reason = "正在执行操作，件别未修改。";
            return null;
        }

        if (_sourceAssemblyPath.Length == 0 || _probeResult is null || Parts.Count == 0)
        {
            reason = "请先解析装配体，再统一件别。";
            return null;
        }

        var changed = 0;
        foreach (var row in Parts.ToList())
        {
            var current = PartKinds.Resolve(_kindEdits, row.SourcePath, _sourceAssemblyPath);
            if (current == kind)
                continue;
            if (PartKinds.IsFixed(row.SourcePath)
                || (kind == PackagePartCategory.Machined
                    && !PartKinds.CanBeMachined(row.SourcePath, _sourceAssemblyPath)))
            {
                skipped++;
                continue;
            }

            var key = Path.GetFullPath(row.SourcePath);
            if (kind == PartKinds.Default(row.SourcePath, _sourceAssemblyPath))
                _kindEdits.Remove(key);
            else
                _kindEdits[key] = kind;
            changed++;
        }

        if (changed > 0)
        {
            _conversionCompleted = false;
            ApplyRenamePreview();
            ApplyPackagePreview();
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanWrite));
        }

        return changed;
    }

    /// <summary>
    /// 底下那个件别框此刻该显示什么：全表同一种件别就显示它，否则显示「件别」。
    /// 与材料等三个框同一套状态机（V4.12）：框是表格的读数，不是一个记住的选择。
    /// </summary>
    internal string KindBoxText()
    {
        if (_sourceAssemblyPath.Length == 0 || Parts.Count == 0)
            return PartKinds.BoxPlaceholder;
        PackagePartCategory? first = null;
        foreach (var row in Parts)
        {
            var kind = PartKinds.Resolve(_kindEdits, row.SourcePath, _sourceAssemblyPath);
            if (first is null)
                first = kind;
            else if (first != kind)
                return PartKinds.BoxPlaceholder;
        }

        return first is { } uniform ? PartKinds.Label(uniform) : PartKinds.BoxPlaceholder;
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
