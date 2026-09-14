using System.Windows.Threading;

namespace HistoryMinerva;

/// <summary>
/// 后台操作回 UI 线程的唯一通道：排队、合批，按 DataBind 优先级一次排空。
/// Worker 事件、品牌查询与收尾结论都从线程池过来，谁都不许直接碰行对象或 StatusText。
/// </summary>
public sealed partial class AssemblyViewModel
{
    private void QueueUiUpdate(Action update)
    {
        lock (_dispatchGate)
        {
            if (_disposed)
                return;
            _pendingUiUpdates.Enqueue(update);
            if (_dispatchOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                return;
            _dispatchOperation = _uiDispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(DrainUiUpdates));
        }
    }

    private void DrainUiUpdates()
    {
        while (true)
        {
            Action update;
            lock (_dispatchGate)
            {
                if (_disposed || _pendingUiUpdates.Count == 0)
                {
                    _pendingUiUpdates.Clear();
                    _dispatchOperation = null;
                    return;
                }
                update = _pendingUiUpdates.Dequeue();
            }
            update();
        }
    }
}
