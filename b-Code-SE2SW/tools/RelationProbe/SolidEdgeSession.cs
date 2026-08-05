using System.Diagnostics;

namespace RelationProbe;

/// <summary>
/// 一次 Solid Edge 会话。所有权规则与生产 Worker 的 CadProcessOwnership 一致：
/// 只有能证明实例是本进程新建的，才允许 Quit；附着到用户已开的会话时绝不退出、绝不改可见性。
/// </summary>
internal sealed class SolidEdgeSession : IDisposable
{
    private const string ProgId = "SolidEdge.Application";
    private const string ProcessName = "Edge";

    private readonly object _application;
    private readonly bool _owns;
    private readonly bool? _originalDisplayAlerts;
    private readonly List<string> _notes;
    private bool _disposed;

    private SolidEdgeSession(object application, bool owns, bool? originalDisplayAlerts, List<string> notes)
    {
        _application = application;
        _owns = owns;
        _originalDisplayAlerts = originalDisplayAlerts;
        _notes = notes;
    }

    public bool OwnsInstance => _owns;

    public static SolidEdgeSession Start(List<string> notes)
    {
        var before = GetPids();
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
            ?? throw new InvalidOperationException("SolidEdge.Application 未注册。");
        var application = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Solid Edge COM 返回空实例。");
        Com.TryInvoke(application, "DoIdle");

        var after = GetPids();
        var created = after.Except(before).ToArray();
        var owns = created.Length == 1;
        notes.Add(owns
            ? $"本探针新建了 Solid Edge 进程 {created[0]}，结束时会退出它。"
            : "附着到已有的 Solid Edge 会话，探针结束时不会退出它，也不修改其可见性。");

        bool? originalDisplayAlerts = null;
        if (owns)
        {
            originalDisplayAlerts = Com.TryGetValue(application, "DisplayAlerts", true);
            Com.TrySetProperty(application, "DisplayAlerts", false);
            Com.TrySetProperty(application, "Visible", false);
        }

        return new SolidEdgeSession(application, owns, originalDisplayAlerts, notes);
    }

    public object OpenDocument(string path)
    {
        var documents = Com.GetProperty(_application, "Documents")
            ?? throw new InvalidOperationException("Application.Documents 返回 null。");
        try
        {
            var document = Com.Invoke(documents, "Open", path)
                ?? throw new InvalidOperationException($"Documents.Open 返回 null：{path}");
            Com.TryInvoke(_application, "DoIdle");
            return document;
        }
        finally
        {
            ObjectDumper.Release(documents);
        }
    }

    public void CloseDocument(object document)
    {
        // 一律不保存。样件是生产资产，任何写入都是探针缺陷。
        Com.TryInvoke(document, "Close", false);
        Com.TryInvoke(_application, "DoIdle");
        ObjectDumper.Release(document);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_owns)
        {
            if (_originalDisplayAlerts is bool alerts)
                Com.TrySetProperty(_application, "DisplayAlerts", alerts);
            Com.TryInvoke(_application, "Quit");
        }

        ObjectDumper.Release(_application);
        if (!_owns)
            return;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && GetPids().Length > 0)
            Thread.Sleep(250);
        _notes.Add($"退出等待结束，剩余 Solid Edge 进程 {GetPids().Length} 个。");
    }

    public static int[] GetPids()
    {
        try { return Process.GetProcessesByName(ProcessName).Select(process => process.Id).OrderBy(id => id).ToArray(); }
        catch { return []; }
    }
}
