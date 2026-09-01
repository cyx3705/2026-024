using System.Collections.ObjectModel;

namespace HistoryMinerva;

/// <summary>零件表那一份行数据。集合本身与按 Id 找行都在这里，其余分部只管往里填。</summary>
public sealed partial class AssemblyViewModel
{
    private readonly ConversionFileRowCollection _rows = [];

    public ObservableCollection<ConversionFileRow> Parts => _rows;

    /// <summary>
    /// 按行 Id 找行。O(1)，不是 <c>FirstOrDefault</c>。
    ///
    /// 找行发生在三条高频路径上：单元格动作、批量刷值、Worker 逐条事件。
    /// 线性扫在几百个零件的装配上会把这三条路一起拖慢，而且都落在 UI 线程。
    /// </summary>
    internal ConversionFileRow? FindRow(string? id) => _rows.Find(id);
}
