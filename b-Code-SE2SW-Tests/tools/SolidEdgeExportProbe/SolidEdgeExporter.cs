using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SolidEdgeExportProbe;

internal enum ProbeStage
{
    Preflight,
    ComRegistration,
    AppLaunch,
    AppConfigure,
    DocumentOpen,
    Export,
    OutputVerify,
    DocumentClose,
    AppQuit,
    Completed,
}

internal enum ErrorClass
{
    None,
    ComNotRegistered,
    AppLaunchFailed,
    LicenseUnavailable,
    InputMissing,
    InputInvalid,
    InputLocked,
    OutputExists,
    OutputNotWritable,
    OutputEmpty,
    OutputUnstable,
    CallRejected,
    Timeout,
    Cancelled,
    ExportFailed,
    Unknown,
}

internal enum ExportMethod
{
    /// <summary>SolidEdgeFramework.SolidEdgeDocument.SaveAs(target.x_t) —— 官方 Batch 样例路径。</summary>
    SaveAs,

    /// <summary>SolidEdgePart.PartDocument.SaveBody(...) —— 专用 Parasolid 导出，显式文本/二进制与版本。</summary>
    SaveBody,
}

internal sealed class ProbeResult
{
    public bool Success { get; set; }
    public string Stage { get; set; } = ProbeStage.Preflight.ToString();
    public string ErrorClass { get; set; } = SolidEdgeExportProbe.ErrorClass.None.ToString();
    public string? Message { get; set; }

    [JsonPropertyName("hresult")]
    public string? HResult { get; set; }

    public string Method { get; set; } = string.Empty;
    public FileFacts? Input { get; set; }
    public OutputFacts? Output { get; set; }
    public SolidEdgeFacts SolidEdge { get; set; } = new();
    public DocumentIdentity? DocumentIdentity { get; set; }
    public MessageFilterFacts MessageFilter { get; set; } = new();
    public Dictionary<string, long> TimingsMs { get; set; } = new();
    public bool SourceUnchanged { get; set; }
    public List<string> Notes { get; set; } = new();
}

internal class FileFacts
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? Sha256 { get; set; }
    public string? LastWriteUtc { get; set; }
}

internal sealed class OutputFacts : FileFacts
{
    /// <summary>写入稳定（连续采样长度不变）所需毫秒数。</summary>
    public long StableAfterMs { get; set; }

    /// <summary>Parasolid 头中的 FORMAT= 字段（text / binary），是判定文本格式的权威依据。</summary>
    public string? ParasolidFormat { get; set; }

    /// <summary>头部 1KB 是否全为 ASCII。中文系统下 DATE 字段是 ANSI 中文，会为 false。</summary>
    public bool HeaderIsPureAscii { get; set; }

    /// <summary>Parasolid 文本文件头首行，含模型器版本。</summary>
    public string? ParasolidHeader { get; set; }

    public string? ParasolidSchema { get; set; }
    public string? ParasolidModellerVersion { get; set; }
}

internal sealed class SolidEdgeFacts
{
    public bool ProgIdRegistered { get; set; }
    public string? ProgId { get; set; }
    public string? Clsid { get; set; }
    public string? LocalServer { get; set; }
    public string? Version { get; set; }
    public string? Name { get; set; }

    /// <summary>SEInstallDataLib.SEInstallData.GetVersion()，无需启动 Solid Edge。</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>SEInstallDataLib.SEInstallData.GetParasolidVersion()：安装自带的 Parasolid 版本。</summary>
    public string? InstalledParasolidVersion { get; set; }
    public int[] PreexistingPids { get; set; } = Array.Empty<int>();
    public int[] PidsAfterCreate { get; set; } = Array.Empty<int>();
    public bool CreatedNewInstance { get; set; }
    public bool AttachedToExistingSession { get; set; }
    public int OwnedPid { get; set; }
    public bool QuitCalled { get; set; }
    public int[] LeakedPids { get; set; } = Array.Empty<int>();
}

internal sealed class DocumentIdentity
{
    public string? FullNameBeforeExport { get; set; }
    public string? FullNameAfterExport { get; set; }
    public string? NameAfterExport { get; set; }
    public bool DirtyBeforeExport { get; set; }
    public bool DirtyAfterExport { get; set; }
    public int DocumentCountAfterExport { get; set; }
    public string? DocumentTypeAfterExport { get; set; }

    /// <summary>PartDocument.Models.Count：Solid Edge 中的“多实体”判据。</summary>
    public int ModelCount { get; set; } = -1;
}

internal sealed class MessageFilterFacts
{
    public bool Registered { get; set; }
    public int RejectedCallCount { get; set; }
    public int MaxObservedWaitMs { get; set; }
    public bool GaveUp { get; set; }
}

internal sealed class ProbeOptions
{
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public ExportMethod Method { get; set; } = ExportMethod.SaveBody;
    public int ParasolidVersion { get; set; } // 0 = seParasolidCurrentVersion
    public bool Binary { get; set; }
    public int RetryBudgetMs { get; set; } = 60_000;
    public int StageTimeoutMs { get; set; } = 180_000;
    public bool KeepAppAlive { get; set; }
    public int SelfCancelAfterMs { get; set; }
}

internal sealed class ProbeException : Exception
{
    public ProbeException(ProbeStage stage, ErrorClass errorClass, string message, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        ErrorClass = errorClass;
    }

    public ProbeStage Stage { get; }

    public ErrorClass ErrorClass { get; }
}

internal sealed class SolidEdgeExporter
{
    private const string ProgId = "SolidEdge.Application";
    private const string SolidEdgeProcessName = "Edge";

    // SolidEdgeConstants.SaveBodyConstants
    private const int seSaveBodyAsParasolidText = 2;
    private const int seSaveBodyAsParasolidBinary = 3;

    private readonly ProbeOptions _options;
    private readonly CancellationToken _cancellation;
    private readonly Stopwatch _stopwatch = new();
    private readonly ProbeResult _result = new();

    public SolidEdgeExporter(ProbeOptions options, CancellationToken cancellation)
    {
        _options = options;
        _cancellation = cancellation;
    }

    public ProbeResult Run()
    {
        _result.Method = _options.Method.ToString();

        OleMessageFilter? filter = null;
        SolidEdgeFramework.Application? app = null;
        SolidEdgeFramework.Documents? documents = null;
        SolidEdgeFramework.SolidEdgeDocument? document = null;

        bool? originalDisplayAlerts = null;
        bool? originalVisible = null;
        bool restoreVisible = false;

        try
        {
            Preflight();

            CheckComRegistration();

            filter = Time(ProbeStage.ComRegistration, () =>
                OleMessageFilter.Register(_options.RetryBudgetMs, _cancellation));
            _result.MessageFilter.Registered = true;

            int[] before = GetSolidEdgePids();
            _result.SolidEdge.PreexistingPids = before;

            app = Time(ProbeStage.AppLaunch, CreateApplication);

            int[] after = GetSolidEdgePids();
            _result.SolidEdge.PidsAfterCreate = after;
            int[] newPids = after.Except(before).ToArray();
            _result.SolidEdge.CreatedNewInstance = newPids.Length > 0;
            _result.SolidEdge.AttachedToExistingSession = newPids.Length == 0 && before.Length > 0;
            _result.SolidEdge.OwnedPid = newPids.Length == 1 ? newPids[0] : 0;

            if (_result.SolidEdge.AttachedToExistingSession)
            {
                _result.Notes.Add(
                    "CoCreateInstance 未产生新的 Edge.exe 进程：本次复用了用户已有会话，退出阶段不会调用 Quit()。");
            }

            Time(ProbeStage.AppConfigure, () =>
            {
                _result.SolidEdge.Version = TryGet(() => app!.Version);
                _result.SolidEdge.Name = TryGet(() => app!.Name);

                originalDisplayAlerts = TryGetStruct(() => app!.DisplayAlerts);
                app!.DisplayAlerts = false;

                if (_result.SolidEdge.CreatedNewInstance)
                {
                    originalVisible = TryGetStruct(() => app.Visible);
                    app.Visible = false;
                    restoreVisible = false; // 自己创建的实例最终会退出，无需还原
                }
                else
                {
                    // 复用用户会话时绝不修改可见性。
                    restoreVisible = false;
                }

                app.DoIdle();
                return true;
            });

            documents = Time(ProbeStage.DocumentOpen, () => app!.Documents);

            document = Time(ProbeStage.DocumentOpen, () =>
            {
                object opened;
                try
                {
                    opened = documents!.Open(_options.InputPath);
                }
                catch (COMException ex)
                {
                    throw new ProbeException(
                        ProbeStage.DocumentOpen,
                        ClassifyComException(ex, ErrorClass.InputInvalid),
                        $"Documents.Open 失败：{ex.Message}",
                        ex);
                }

                if (opened is not SolidEdgeFramework.SolidEdgeDocument doc)
                {
                    throw new ProbeException(
                        ProbeStage.DocumentOpen,
                        ErrorClass.InputInvalid,
                        "Documents.Open 返回的对象不是 SolidEdgeDocument。");
                }

                app!.DoIdle();
                return doc;
            });

            var identity = new DocumentIdentity
            {
                FullNameBeforeExport = TryGet(() => document!.FullName),
                DirtyBeforeExport = TryGetStruct(() => document!.Dirty) ?? false,
                ModelCount = document is SolidEdgePart.PartDocument p
                    ? TryGetStruct(() => p.Models.Count) ?? -1
                    : -1,
            };
            _result.DocumentIdentity = identity;

            Time(ProbeStage.Export, () =>
            {
                Export(app!, document!);
                app!.DoIdle();
                return true;
            });

            identity.FullNameAfterExport = TryGet(() => document!.FullName);
            identity.NameAfterExport = TryGet(() => document!.Name);
            identity.DirtyAfterExport = TryGetStruct(() => document!.Dirty) ?? false;
            identity.DocumentCountAfterExport = TryGetStruct(() => documents!.Count) ?? -1;
            identity.DocumentTypeAfterExport = TryGet(() => document!.Type.ToString());

            Time(ProbeStage.OutputVerify, () =>
            {
                _result.Output = VerifyOutput();
                return true;
            });

            Time(ProbeStage.DocumentClose, () =>
            {
                document!.Close(false);
                app!.DoIdle();
                return true;
            });
            Marshal.FinalReleaseComObject(document);
            document = null;

            _result.SourceUnchanged = _result.Input is not null
                && FileFactsFor(_options.InputPath).Sha256 == _result.Input.Sha256;

            _result.Stage = ProbeStage.Completed.ToString();
            _result.Success = true;
        }
        catch (ProbeException ex)
        {
            _result.Success = false;
            _result.Stage = ex.Stage.ToString();
            _result.ErrorClass = _cancellation.IsCancellationRequested
                ? ErrorClass.Cancelled.ToString()
                : ex.ErrorClass.ToString();
            _result.Message = ex.Message;
            _result.HResult = FormatHResult(ex.InnerException ?? ex);
        }
        catch (OperationCanceledException)
        {
            _result.Success = false;
            _result.ErrorClass = ErrorClass.Cancelled.ToString();
            _result.Message = "已取消。";
        }
        catch (Exception ex)
        {
            _result.Success = false;

            // 取消时消息过滤器返回 CANCEL_CALL，调用方看到的是 RPC_E_CALL_REJECTED，
            // 必须还原成“取消”而不是“被拒绝”。
            ErrorClass errorClass = _cancellation.IsCancellationRequested
                ? ErrorClass.Cancelled
                : ex is COMException com
                    ? ClassifyComException(com, ErrorClass.Unknown)
                    : ErrorClass.Unknown;

            _result.ErrorClass = errorClass.ToString();
            _result.Message = ex.Message;
            _result.HResult = FormatHResult(ex);
        }
        finally
        {
            // 确定性清理：Document -> Documents -> Application。
            if (document is not null)
            {
                TryRun(() => document.Close(false));
                TryRun(() => Marshal.FinalReleaseComObject(document));
            }

            if (documents is not null)
            {
                TryRun(() => Marshal.FinalReleaseComObject(documents));
            }

            if (app is not null)
            {
                if (originalDisplayAlerts is bool alerts)
                {
                    TryRun(() => app.DisplayAlerts = alerts);
                }

                if (restoreVisible && originalVisible is bool visible)
                {
                    TryRun(() => app.Visible = visible);
                }

                TryRun(app.DoIdle);

                // 只退出本探针创建的实例；复用用户会话时绝不 Quit。
                if (_result.SolidEdge.CreatedNewInstance && !_options.KeepAppAlive)
                {
                    TryRun(app.Quit);
                    _result.SolidEdge.QuitCalled = true;
                }

                TryRun(() => Marshal.FinalReleaseComObject(app));
            }

            filter?.Revoke();
            if (filter is not null)
            {
                _result.MessageFilter.RejectedCallCount = filter.RejectedCallCount;
                _result.MessageFilter.MaxObservedWaitMs = filter.MaxObservedWaitMs;
                _result.MessageFilter.GaveUp = filter.GaveUp;
            }

            if (_result.SolidEdge.QuitCalled && _result.SolidEdge.OwnedPid != 0)
            {
                _result.SolidEdge.LeakedPids = WaitForExit(_result.SolidEdge.OwnedPid, 30_000)
                    ? Array.Empty<int>()
                    : new[] { _result.SolidEdge.OwnedPid };
            }
        }

        return _result;
    }

    private void Preflight()
    {
        _result.Stage = ProbeStage.Preflight.ToString();

        if (string.IsNullOrWhiteSpace(_options.InputPath) || !Path.IsPathRooted(_options.InputPath))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.InputMissing, "--input 必须是绝对路径。");
        }

        if (string.IsNullOrWhiteSpace(_options.OutputPath) || !Path.IsPathRooted(_options.OutputPath))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputNotWritable, "--output 必须是绝对路径。");
        }

        if (!File.Exists(_options.InputPath))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.InputMissing,
                $"输入文件不存在：{_options.InputPath}");
        }

        if (!string.Equals(Path.GetExtension(_options.InputPath), ".par", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.InputInvalid,
                "首版只接受 Solid Edge 零件文件 .par。");
        }

        if (string.Equals(
                Path.GetFullPath(_options.InputPath),
                Path.GetFullPath(_options.OutputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputNotWritable,
                "输入路径与输出路径相同。");
        }

        if (File.Exists(_options.OutputPath))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputExists,
                $"输出文件已存在，探针拒绝覆盖：{_options.OutputPath}");
        }

        string outputDir = Path.GetDirectoryName(_options.OutputPath)
            ?? throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputNotWritable, "无法解析输出目录。");

        if (!Directory.Exists(outputDir))
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputNotWritable,
                $"输出目录不存在：{outputDir}");
        }

        // 真正写一个探测文件，比检查 ACL 更可靠。
        string canary = Path.Combine(outputDir, $".se2sw-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(canary, Array.Empty<byte>());
            File.Delete(canary);
        }
        catch (Exception ex)
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.OutputNotWritable,
                $"输出目录不可写：{outputDir} —— {ex.Message}", ex);
        }

        // 独占打开源文件以确认未被占用；随后立刻释放，交给 Solid Edge 打开。
        try
        {
            using FileStream fs = File.Open(_options.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new ProbeException(ProbeStage.Preflight, ErrorClass.InputLocked,
                $"源文件被占用：{_options.InputPath} —— {ex.Message}", ex);
        }

        _result.Input = FileFactsFor(_options.InputPath);
    }

    private void CheckComRegistration()
    {
        _result.Stage = ProbeStage.ComRegistration.ToString();
        _result.SolidEdge.ProgId = ProgId;

        Type? type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
        {
            throw new ProbeException(ProbeStage.ComRegistration, ErrorClass.ComNotRegistered,
                $"ProgID 未注册：{ProgId}");
        }

        _result.SolidEdge.ProgIdRegistered = true;
        _result.SolidEdge.Clsid = type.GUID.ToString("B").ToUpperInvariant();
        _result.SolidEdge.LocalServer = ReadLocalServerPath(type.GUID);

        ReadInstallData();
    }

    /// <summary>
    /// SEInstallData 是进程内 ActiveX DLL，读取版本不会启动 Edge.exe，
    /// 适合在真正启动 CAD 之前做环境判据。
    /// </summary>
    private void ReadInstallData()
    {
        object? installData = null;
        try
        {
            installData = new SEInstallDataLib.SEInstallData();
            var data = (SEInstallDataLib.SEInstallData)installData;
            _result.SolidEdge.InstalledVersion = data.GetVersion();
            _result.SolidEdge.InstalledParasolidVersion = data.GetParasolidVersion();
        }
        catch (Exception ex)
        {
            _result.Notes.Add($"SEInstallData 读取失败（不影响导出）：{ex.Message}");
        }
        finally
        {
            if (installData is not null)
            {
                TryRun(() => Marshal.FinalReleaseComObject(installData));
            }
        }
    }

    private SolidEdgeFramework.Application CreateApplication()
    {
        Type type = Type.GetTypeFromProgID(ProgId, throwOnError: true)!;
        object instance;
        try
        {
            instance = Activator.CreateInstance(type)
                ?? throw new ProbeException(ProbeStage.AppLaunch, ErrorClass.AppLaunchFailed,
                    "Activator.CreateInstance 返回 null。");
        }
        catch (COMException ex)
        {
            throw new ProbeException(
                ProbeStage.AppLaunch,
                ClassifyComException(ex, ErrorClass.AppLaunchFailed),
                $"创建 {ProgId} 失败：{ex.Message}",
                ex);
        }

        if (instance is not SolidEdgeFramework.Application app)
        {
            throw new ProbeException(ProbeStage.AppLaunch, ErrorClass.AppLaunchFailed,
                "创建的对象不是 SolidEdgeFramework.Application。");
        }

        return app;
    }

    private void Export(SolidEdgeFramework.Application app, SolidEdgeFramework.SolidEdgeDocument document)
    {
        try
        {
            if (_options.Method == ExportMethod.SaveAs)
            {
                // 官方 Batch 样例路径：由目标扩展名选择导出器。
                document.SaveAs(_options.OutputPath);
                return;
            }

            if (document is not SolidEdgePart.PartDocument part)
            {
                throw new ProbeException(ProbeStage.Export, ErrorClass.InputInvalid,
                    "SaveBody 方式要求文档是 SolidEdgePart.PartDocument。");
            }

            int bodyType = _options.Binary ? seSaveBodyAsParasolidBinary : seSaveBodyAsParasolidText;
            part.SaveBody(
                _options.OutputPath,
                bodyType,
                _options.ParasolidVersion,
                Type.Missing,
                Type.Missing);
        }
        catch (ProbeException)
        {
            throw;
        }
        catch (COMException ex)
        {
            throw new ProbeException(
                ProbeStage.Export,
                ClassifyComException(ex, ErrorClass.ExportFailed),
                $"导出失败：{ex.Message}",
                ex);
        }
    }

    private OutputFacts VerifyOutput()
    {
        // 仅“SaveAs/SaveBody 未抛异常”不足以判定成功。
        var sw = Stopwatch.StartNew();
        long lastLength = -1;
        int stableSamples = 0;

        while (sw.ElapsedMilliseconds < _options.StageTimeoutMs)
        {
            _cancellation.ThrowIfCancellationRequested();

            if (!File.Exists(_options.OutputPath))
            {
                Thread.Sleep(100);
                continue;
            }

            long length;
            try
            {
                using FileStream fs = File.Open(
                    _options.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                length = fs.Length;
            }
            catch (IOException)
            {
                // 仍被导出器独占，说明还在写入。
                stableSamples = 0;
                Thread.Sleep(100);
                continue;
            }

            if (length > 0 && length == lastLength)
            {
                stableSamples++;
                if (stableSamples >= 3)
                {
                    break;
                }
            }
            else
            {
                stableSamples = 0;
            }

            lastLength = length;
            Thread.Sleep(100);
        }

        if (!File.Exists(_options.OutputPath))
        {
            throw new ProbeException(ProbeStage.OutputVerify, ErrorClass.ExportFailed,
                "调用返回成功但输出文件不存在。");
        }

        var facts = FileFactsFor(_options.OutputPath);
        var output = new OutputFacts
        {
            Path = facts.Path,
            Length = facts.Length,
            Sha256 = facts.Sha256,
            LastWriteUtc = facts.LastWriteUtc,
            StableAfterMs = sw.ElapsedMilliseconds,
        };

        if (output.Length == 0)
        {
            throw new ProbeException(ProbeStage.OutputVerify, ErrorClass.OutputEmpty,
                "输出文件为空。");
        }

        if (stableSamples < 3)
        {
            throw new ProbeException(ProbeStage.OutputVerify, ErrorClass.OutputUnstable,
                $"输出文件在 {_options.StageTimeoutMs} ms 内长度仍未稳定。");
        }

        ReadParasolidHeader(output);
        return output;
    }

    private static void ReadParasolidHeader(OutputFacts output)
    {
        byte[] head = new byte[1024];
        int read;
        using (FileStream fs = File.OpenRead(output.Path))
        {
            read = fs.Read(head, 0, head.Length);
        }

        output.HeaderIsPureAscii = head.Take(read).All(b => b is >= 0x09 and <= 0x7E);

        string text = System.Text.Encoding.ASCII.GetString(head, 0, read);

        const string formatMarker = "FORMAT=";
        int fmt = text.IndexOf(formatMarker, StringComparison.Ordinal);
        if (fmt >= 0)
        {
            int start = fmt + formatMarker.Length;
            int end = text.IndexOfAny(new[] { ';', '\r', '\n' }, start);
            output.ParasolidFormat = (end > start ? text[start..end] : text[start..]).Trim();
        }
        string firstLine = text.Split('\n')[0].Trim('\r', ' ');
        output.ParasolidHeader = firstLine;

        // 权威版本信息来自 “TRANSMIT FILE created by modeller version <ver> <schema>”。
        // 注意：头部另有一处 SCH_<当前建模器模式> 引用，与请求的导出版本无关，不能用它判版本。
        const string marker = "modeller version";
        int mv = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (mv >= 0)
        {
            int start = mv + marker.Length;
            int end = text.IndexOfAny(new[] { ';', '\r', '\n' }, start);
            string tail = (end > start ? text[start..end] : text[start..]).Trim();
            string[] parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            output.ParasolidModellerVersion = parts.Length > 0 ? parts[0] : tail;
            output.ParasolidSchema = parts.FirstOrDefault(p => p.StartsWith("SCH_", StringComparison.Ordinal));
        }
    }

    private static FileFacts FileFactsFor(string path)
    {
        var info = new FileInfo(path);
        using FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] hash = SHA256.HashData(fs);
        return new FileFacts
        {
            Path = info.FullName,
            Length = info.Length,
            Sha256 = Convert.ToHexString(hash),
            LastWriteUtc = info.LastWriteTimeUtc.ToString("O"),
        };
    }

    private static int[] GetSolidEdgePids()
    {
        var pids = new List<int>();
        foreach (Process process in Process.GetProcessesByName(SolidEdgeProcessName))
        {
            using (process)
            {
                pids.Add(process.Id);
            }
        }

        pids.Sort();
        return pids.ToArray();
    }

    private static bool WaitForExit(int pid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // 进程已不存在
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static string? ReadLocalServerPath(Guid clsid)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey($@"CLSID\{clsid:B}\LocalServer32");
        return key?.GetValue(null) as string;
    }

    private static ErrorClass ClassifyComException(COMException ex, ErrorClass fallback) => unchecked((uint)ex.HResult) switch
    {
        0x80010001 => ErrorClass.CallRejected,   // RPC_E_CALL_REJECTED
        0x8001010A => ErrorClass.CallRejected,   // RPC_E_SERVERCALL_RETRYLATER
        0x80080005 => ErrorClass.AppLaunchFailed, // CO_E_SERVER_EXEC_FAILURE
        0x80040154 => ErrorClass.ComNotRegistered, // REGDB_E_CLASSNOTREG
        0x800401F3 => ErrorClass.ComNotRegistered, // CO_E_CLASSSTRING
        0x80070005 => ErrorClass.LicenseUnavailable, // E_ACCESSDENIED（无授权时常见）
        _ => fallback,
    };

    private static string FormatHResult(Exception ex) =>
        $"0x{unchecked((uint)ex.HResult):X8}";

    private T Time<T>(ProbeStage stage, Func<T> action)
    {
        _cancellation.ThrowIfCancellationRequested();
        _result.Stage = stage.ToString();
        _stopwatch.Restart();
        try
        {
            return action();
        }
        finally
        {
            string key = stage.ToString();
            _result.TimingsMs[key] = _result.TimingsMs.TryGetValue(key, out long previous)
                ? previous + _stopwatch.ElapsedMilliseconds
                : _stopwatch.ElapsedMilliseconds;
        }
    }

    private static string? TryGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetStruct(Func<bool> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetStruct(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 清理路径吞掉异常，保证后续清理仍会执行。
        }
    }
}
