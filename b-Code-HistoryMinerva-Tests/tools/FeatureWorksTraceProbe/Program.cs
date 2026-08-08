using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;
using SolidWorks.Interop.swconst;

namespace FeatureWorksTraceProbe;

internal static class Program
{
    internal const string FeatureWorksProgIdForTrace = "FeatureWorks.FeatureWorksApp";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var options = Options.Parse(args);
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);

        using var writer = new StreamWriter(options.OutputPath, false, new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        using var tracer = new TraceSession(writer);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            using var process = WaitForSolidWorks(options.ProcessId, cancellation.Token);
            var app = BindByPid(process, TimeSpan.FromSeconds(30), cancellation.Token)
                ?? throw new TimeoutException($"Cannot bind SolidWorks_PID_{process.Id}.");
            try
            {
                tracer.Attach(app, process.Id);
                Console.WriteLine($"READY pid={process.Id} log={options.OutputPath}");
                Console.Out.Flush();
                tracer.Run(options.PollMilliseconds, cancellation.Token);
            }
            finally
            {
                tracer.Detach();
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            tracer.Write("trace-stopped", new { reason = "timeout-or-cancel" });
            return 0;
        }
        catch (Exception ex)
        {
            tracer.Write("trace-failed", new
            {
                error = ex.Message,
                hresult = $"0x{unchecked((uint)ex.HResult):X8}",
            });
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Process WaitForSolidWorks(int? expectedPid, CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedPid is int pid)
            {
                try { return Process.GetProcessById(pid); }
                catch (ArgumentException) { }
            }
            else
            {
                var processes = Process.GetProcessesByName("SLDWORKS");
                if (processes.Length == 1)
                    return processes[0];
                foreach (var process in processes)
                    process.Dispose();
                if (processes.Length > 1)
                    throw new InvalidOperationException("Multiple SolidWorks processes are running; pass --pid explicitly.");
            }

            if (!announced)
            {
                Console.WriteLine("WAITING_FOR_SOLIDWORKS");
                Console.Out.Flush();
                announced = true;
            }
            PumpAndWait(250);
        }
    }

    private static ISldWorks? BindByPid(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var monikerName = $"SolidWorks_PID_{process.Id}";
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = GetRunningObject(monikerName);
            if (instance is ISldWorks application)
                return application;
            process.Refresh();
            if (process.HasExited)
                return null;
            PumpAndWait(250);
        }
        return null;
    }

    private static object? GetRunningObject(string displayName)
    {
        IBindCtx? context = null;
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        try
        {
            if (CreateBindCtx(0, out context) != 0 || context is null)
                return null;
            context.GetRunningObjectTable(out table);
            if (table is null)
                return null;
            table.EnumRunning(out enumerator);
            if (enumerator is null)
                return null;
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(context, null, out var name);
                    if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    table.GetObject(moniker, out var instance);
                    return instance;
                }
                finally
                {
                    ReleaseCom(moniker);
                }
            }
            return null;
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(table);
            ReleaseCom(context);
        }
    }

    internal static void PumpAndWait(int milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            Thread.Sleep(10);
        }
        while (stopwatch.ElapsedMilliseconds < milliseconds);
    }

    internal static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }
}

internal sealed class TraceSession(StreamWriter writer) : IDisposable
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly object _writeGate = new();
    private ISldWorks? _app;
    private DSldWorksEvents_Event? _appEvents;
    private ModelDoc2? _model;
    private string? _documentIdentity;
    private string? _lastSnapshot;
    private long _lastHeartbeat;

    public void Attach(ISldWorks app, int pid)
    {
        _app = app;
        _appEvents = (DSldWorksEvents_Event)app;
        _appEvents.CommandOpenPreNotify += OnCommandOpen;
        _appEvents.CommandCloseNotify += OnCommandClose;
        _appEvents.ActiveDocChangeNotify += OnActiveDocChange;
        _appEvents.ActiveModelDocChangeNotify += OnActiveDocChange;
        _appEvents.FileOpenPostNotify += OnFileOpenPost;
        Write("trace-ready", new
        {
            pid,
            revision = Safe(() => app.RevisionNumber()),
            featureWorks = IsFeatureWorksAvailable(),
        });
        RefreshDocumentSubscription(force: true);
        CaptureSnapshot(force: true);
    }

    public void Run(int pollMilliseconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshDocumentSubscription(force: false);
            CaptureSnapshot(force: false);
            if (_elapsed.ElapsedMilliseconds - _lastHeartbeat >= 5000)
            {
                _lastHeartbeat = _elapsed.ElapsedMilliseconds;
                Write("heartbeat", ReadCommandState());
            }
            Program.PumpAndWait(pollMilliseconds);
        }
    }

    public void Detach()
    {
        if (_appEvents is not null)
        {
            try { _appEvents.CommandOpenPreNotify -= OnCommandOpen; } catch { }
            try { _appEvents.CommandCloseNotify -= OnCommandClose; } catch { }
            try { _appEvents.ActiveDocChangeNotify -= OnActiveDocChange; } catch { }
            try { _appEvents.ActiveModelDocChangeNotify -= OnActiveDocChange; } catch { }
            try { _appEvents.FileOpenPostNotify -= OnFileOpenPost; } catch { }
        }
        _appEvents = null;
        _app = null;
    }

    public void Dispose() => Detach();

    public void Write(string kind, object? data)
    {
        var record = new
        {
            timestamp = DateTimeOffset.Now,
            elapsedMs = _elapsed.ElapsedMilliseconds,
            kind,
            data,
        };
        lock (_writeGate)
            writer.WriteLine(JsonSerializer.Serialize(record));
    }

    private int OnCommandOpen(int command, int userCommand)
    {
        Write("command-open", new { command, name = CommandName(command), userCommand, state = ReadCommandState() });
        CaptureSnapshot(force: true);
        return 0;
    }

    private int OnCommandClose(int command, int reason)
    {
        Write("command-close", new { command, name = CommandName(command), reason, state = ReadCommandState() });
        CaptureSnapshot(force: true);
        return 0;
    }

    private int OnActiveDocChange()
    {
        Write("active-document-change", ReadDocumentHeader());
        RefreshDocumentSubscription(force: true);
        CaptureSnapshot(force: true);
        return 0;
    }

    private int OnFileOpenPost(string fileName)
    {
        Write("file-open-post", new { fileName });
        return 0;
    }

    private void RefreshDocumentSubscription(bool force)
    {
        var active = _app?.ActiveDoc as ModelDoc2;
        var identity = active is null ? null : $"{Safe(() => active.GetPathName())}|{Safe(() => active.GetTitle())}";
        if (!force && string.Equals(identity, _documentIdentity, StringComparison.Ordinal))
            return;

        _documentIdentity = identity;
        _model = active;
        Write("document-subscribed", ReadDocumentHeader());
    }

    private void CaptureSnapshot(bool force)
    {
        var snapshot = new
        {
            document = ReadDocumentHeader(),
            command = ReadCommandState(),
            featureWorks = IsFeatureWorksAvailable(),
            selection = ReadSelection(),
            features = ReadFeatures(),
        };
        var json = JsonSerializer.Serialize(snapshot);
        if (!force && string.Equals(json, _lastSnapshot, StringComparison.Ordinal))
            return;
        _lastSnapshot = json;
        Write("snapshot", snapshot);
    }

    private object ReadDocumentHeader()
        => new
        {
            title = _model is null ? null : Safe(() => _model.GetTitle()),
            path = _model is null ? null : Safe(() => _model.GetPathName()),
            type = _model is null ? 0 : Safe(() => _model.GetType()),
        };

    private object ReadCommandState()
    {
        var id = 0;
        var title = string.Empty;
        var userCommand = false;
        try { _app?.GetRunningCommandInfo(out id, out title, out userCommand); } catch { }
        return new
        {
            inProgress = Safe(() => _app?.CommandInProgress ?? false),
            id,
            name = CommandName(id),
            title,
            userCommand,
        };
    }

    private bool IsFeatureWorksAvailable()
    {
        try
        {
            return _app?.GetAddInObject(Program.FeatureWorksProgIdForTrace) is not null;
        }
        catch { return false; }
    }

    private object ReadSelection()
    {
        if (_model is null)
            return new { count = 0, items = Array.Empty<object>() };
        try
        {
            var manager = (SelectionMgr)_model.SelectionManager;
            var count = manager.GetSelectedObjectCount2(-1);
            var items = new List<object>();
            for (var index = 1; index <= count; index++)
            {
                var type = manager.GetSelectedObjectType3(index, -1);
                items.Add(new
                {
                    index,
                    type,
                    typeName = Enum.GetName(typeof(swSelectType_e), type),
                    mark = manager.GetSelectedObjectMark(index),
                });
            }
            return new { count, items = items.ToArray() };
        }
        catch (Exception ex)
        {
            return new { count = -1, items = Array.Empty<object>(), error = ex.Message };
        }
    }

    private object[] ReadFeatures()
    {
        if (_model is null)
            return [];
        var features = new List<object>();
        Feature? feature = null;
        try
        {
            feature = (Feature?)_model.FirstFeature();
            while (feature is not null && features.Count < 512)
            {
                var next = (Feature?)feature.GetNextFeature();
                try
                {
                    features.Add(new
                    {
                        index = features.Count,
                        name = Safe(() => feature.Name),
                        type = Safe(() => feature.GetTypeName2()),
                    });
                }
                finally { feature = next; }
            }
            return features.ToArray();
        }
        catch (Exception ex)
        {
            return [new { index = -1, name = "(read-failed)", type = ex.Message }];
        }
    }

    private static string? CommandName(int command) => Enum.GetName(typeof(swCommands_e), command);

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}

internal sealed record Options(string OutputPath, int TimeoutSeconds, int PollMilliseconds, int? ProcessId)
{
    public static Options Parse(string[] args)
    {
        string? output = null;
        var timeout = 900;
        var poll = 100;
        int? pid = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output": output = Path.GetFullPath(args[++index]); break;
                case "--timeout-seconds": timeout = int.Parse(args[++index]); break;
                case "--poll-ms": poll = int.Parse(args[++index]); break;
                case "--pid": pid = int.Parse(args[++index]); break;
                default: throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--output is required.");
        if (timeout is < 30 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (poll is < 50 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(poll));
        return new Options(output, timeout, poll, pid);
    }
}
