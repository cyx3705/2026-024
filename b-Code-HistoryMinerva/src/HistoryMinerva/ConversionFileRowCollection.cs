using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HistoryMinerva;

/// <summary>
/// 零件表的行集合：按行 Id 建索引，并支持一次性整表替换。
///
/// 两件事都冲着同一个毛病去——属性整备表在几百个零件下「改一格卡一下」：
///
/// * 逐行 <c>Add</c> 会发出 2N 条集合变更通知，DataGrid 与 Aurora 表格每一条都要重排一次。
///   <see cref="ReplaceAll"/> 把整表换掉只发一条 Reset。
/// * 单元格动作、批量刷值和 Worker 事件都按行 Id 找行，原先每次都是 <c>FirstOrDefault</c>
///   线性扫。一次写入几十条事件乘几百行，就是几万次字符串比较，全落在 UI 线程上。
///
/// 索引跟着集合变更自动维护，因此没有"忘了同步"这条路可走。
/// </summary>
internal sealed class ConversionFileRowCollection : ObservableCollection<ConversionFileRow>
{
    private readonly Dictionary<string, ConversionFileRow> _byId = new(StringComparer.Ordinal);

    /// <summary>按行 Id 取行。找不到返回 null。</summary>
    public ConversionFileRow? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : _byId.GetValueOrDefault(id);

    /// <summary>
    /// 把整表换成 <paramref name="rows"/>，只发一条 Reset 通知。
    ///
    /// 走 <see cref="Collection{T}.Items"/> 绕开逐条通知是 <see cref="ObservableCollection{T}"/>
    /// 的既定用法：改完再自己把 Count、索引器和 Reset 三条通知补齐，绑定才不会停在旧值上。
    /// </summary>
    public void ReplaceAll(IEnumerable<ConversionFileRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);
        Reindex();
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        // 无条件重建：Clear() 也是一条 Reset，漏过它索引就会留着一整表已经不存在的行。
        // 消费者在 base 之后才收到通知，因此他们看到的索引一定是当前这一份。
        Reindex();
        base.OnCollectionChanged(e);
    }

    private void Reindex()
    {
        _byId.Clear();
        foreach (var row in Items)
            _byId[row.Id] = row;
    }
}
