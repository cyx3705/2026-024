using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SWuse.Api;

namespace SWuse.Worker;

/// <summary>V0.1 的最小 SolidWorks COM 后端。所有 API 长度均为米。</summary>
internal sealed class SolidWorksPartBackend : IPartBackend, IDisposable
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int DocumentTypePart = 1;
    private const int DefaultPartTemplatePreference = 8;
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;
    private readonly CadProcessOwnership _ownership;
    private readonly Type _applicationInterface;
    private object? _application;
    private object? _document;
    private object? _activeSketch;
    private bool _disposed;

    private SolidWorksPartBackend(
        object application,
        object document,
        CadProcessOwnership ownership,
        Type applicationInterface)
    {
        _application = application;
        _document = document;
        _ownership = ownership;
        _applicationInterface = applicationInterface;
    }

    public static SolidWorksPartBackend Create()
    {
        var ownership = CadProcessOwnership.Capture(ProcessName);
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
            ?? throw new InvalidOperationException("未检测到 SolidWorks COM 注册（SldWorks.Application）。");
        var applicationInterface = SolidWorksInteropTypes.LoadApplicationInterface(type);
        var installDirectory = SolidWorksInteropTypes.GetInstallDirectory(type);
        object? application = null;
        object? document = null;
        var step = "create SolidWorks automation instance";
        try
        {
            application = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            ownership.WaitForNewProcess(TimeSpan.FromSeconds(5));
            if (ownership.OwnsInstance)
            {
                TryRun(() => SetProperty(applicationInterface, application, "Visible", false));
                TryRun(() => SetProperty(applicationInterface, application, "UserControl", false));
            }

            step = "read the configured SolidWorks part template";
            var configuredTemplate = Convert.ToString(Invoke(
                applicationInterface,
                application,
                "GetUserPreferenceStringValue",
                DefaultPartTemplatePreference));
            if (string.IsNullOrWhiteSpace(configuredTemplate))
            {
                throw new InvalidOperationException(
                    "SolidWorks 未配置可用的默认零件模板。请先在 SolidWorks 中设置 Part Template。");
            }
            step = "resolve the configured SolidWorks part template";
            var resolvedTemplate = Convert.ToString(Invoke(
                applicationInterface,
                application,
                "GetDocumentTemplate",
                DocumentTypePart,
                configuredTemplate,
                0,
                0d,
                0d));
            var template = string.IsNullOrWhiteSpace(resolvedTemplate) ? configuredTemplate : resolvedTemplate;
            var templateCandidates = SolidWorksPartTemplateResolver.FindCandidates(template, installDirectory);
            if (templateCandidates.Count == 0)
                throw new InvalidOperationException("未找到可用的实体 SolidWorks 零件模板。");

            step = "create a new SolidWorks part document";
            foreach (var candidate in templateCandidates)
            {
                for (var attempt = 1; attempt <= 10 && document is null; attempt++)
                {
                    document = Invoke(applicationInterface, application, "INewDocument2", candidate, 0, 0d, 0d);
                    if (document is null)
                        Thread.Sleep(500);
                }
            }
            if (document is null)
            {
                throw new InvalidOperationException(
                    "SolidWorks 无法用候选模板创建零件文档：" + string.Join(", ", templateCandidates));
            }
            var backend = new SolidWorksPartBackend(application, document, ownership, applicationInterface);
            application = null;
            document = null;
            return backend;
        }
        catch (Exception ex)
        {
            Release(document);
            if (application is not null && ownership.OwnsInstance)
                TryRun(() => Invoke(applicationInterface, application, "ExitApp"));
            Release(application);
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new InvalidOperationException(
                "SolidWorks 零件初始化失败（" + step + "）：" + detail,
                ex);
        }
    }

    public SketchRef BeginSketch(string name, ReferencePlane plane)
    {
        ThrowIfDisposed();
        if (_activeSketch is not null)
            throw new InvalidOperationException("上一个草图尚未结束。");
        var documentInterface = Interface("IModelDoc2");
        var sketchManagerInterface = Interface("ISketchManager");
        Invoke(documentInterface, _document!, "ClearSelection2", true);
        if (!SelectReferencePlane(plane))
        {
            throw new InvalidOperationException(
                "无法选择草图基准面：" + PlaneLabel(plane)
                + "。当前模板的候选基准面：" + DescribeReferencePlanes());
        }
        var manager = GetProperty(documentInterface, _document!, "SketchManager");
        try
        {
            Invoke(sketchManagerInterface, manager!, "InsertSketch", true);
            _activeSketch = GetProperty(sketchManagerInterface, manager!, "ActiveSketch")
                ?? throw new InvalidOperationException("SolidWorks 没有返回活动草图。");
            return new SketchRef(name);
        }
        finally
        {
            Release(manager);
        }
    }

    public void EndSketch(SketchRef sketch)
    {
        ThrowIfDisposed();
        if (_activeSketch is null)
            return;
        var documentInterface = Interface("IModelDoc2");
        var sketchManagerInterface = Interface("ISketchManager");
        var manager = GetProperty(documentInterface, _document!, "SketchManager");
        try
        {
            Invoke(sketchManagerInterface, manager!, "InsertSketch", true);
            RenameLatestSketchFeature(sketch.Name);
        }
        finally
        {
            Release(manager);
            Release(_activeSketch);
            _activeSketch = null;
        }
    }

    public void AddLine(SketchRef sketch, double x1, double y1, double x2, double y2)
    {
        EnsureActive(sketch);
        var documentInterface = Interface("IModelDoc2");
        var manager = GetProperty(documentInterface, _document!, "SketchManager");
        object? segment;
        try
        {
            segment = Invoke(Interface("ISketchManager"), manager!, "CreateLine", x1, y1, 0d, x2, y2, 0d);
        }
        finally
        {
            Release(manager);
        }
        if (segment is null)
            throw new InvalidOperationException("SolidWorks 创建草图线段失败。");
        Release(segment);
    }

    public void AddCircle(SketchRef sketch, double x, double y, double radius)
    {
        EnsureActive(sketch);
        var documentInterface = Interface("IModelDoc2");
        var manager = GetProperty(documentInterface, _document!, "SketchManager");
        object? segment;
        try
        {
            segment = Invoke(Interface("ISketchManager"), manager!, "CreateCircleByRadius", x, y, 0d, radius);
        }
        finally
        {
            Release(manager);
        }
        if (segment is null)
            throw new InvalidOperationException("SolidWorks 创建草图圆失败。");
        Release(segment);
    }

    public FeatureRef Extrude(string name, SketchRef sketch, double depthMeters, bool reverse)
    {
        SelectSketch(sketch);
        var documentInterface = Interface("IModelDoc2");
        var manager = GetProperty(documentInterface, _document!, "FeatureManager");
        object? feature;
        try
        {
            feature = Invoke(Interface("IFeatureManager"), manager!, "FeatureExtrusion2",
                true, reverse, false, 0, 0, depthMeters, 0d,
                false, false, false, false, 0d, 0d,
                false, false, false, false, true, true, true, 0, 0d, false);
        }
        finally
        {
            Release(manager);
        }
        if (feature is null)
            throw new InvalidOperationException("SolidWorks 凸台拉伸失败。");
        try
        {
            SetProperty(Interface("IFeature"), feature, "Name", name);
            return new FeatureRef(name);
        }
        finally
        {
            Release(feature);
        }
    }

    public FeatureRef CutExtrude(string name, SketchRef sketch, double depthMeters, bool reverse)
    {
        SelectSketch(sketch);
        var documentInterface = Interface("IModelDoc2");
        var manager = GetProperty(documentInterface, _document!, "FeatureManager");
        object? feature;
        try
        {
            feature = Invoke(Interface("IFeatureManager"), manager!, "FeatureCut3",
                true, reverse, true, 0, 0, depthMeters, 0d,
                false, false, false, false, 0d, 0d,
                false, false, false, false, false, true, true, false, false, false, 0, 0d, false);
        }
        finally
        {
            Release(manager);
        }
        if (feature is null)
            throw new InvalidOperationException("SolidWorks 切除拉伸失败。");
        try
        {
            SetProperty(Interface("IFeature"), feature, "Name", name);
            return new FeatureRef(name);
        }
        finally
        {
            Release(feature);
        }
    }

    public void Save(string path, bool overwrite)
    {
        ThrowIfDisposed();
        if (File.Exists(path) && !overwrite)
            throw new IOException("目标文件已存在：" + path);
        var documentInterface = Interface("IModelDoc2");
        var extension = GetProperty(documentInterface, _document!, "Extension");
        try
        {
            object?[] parameters = [path, SaveAsCurrentVersion, SaveAsSilent, null, null, 0, 0];
            var saved = Convert.ToBoolean(Invoke(Interface("IModelDocExtension"), extension!, "SaveAs3", parameters));
            var errors = Convert.ToInt32(parameters[5]);
            var warnings = Convert.ToInt32(parameters[6]);
            if (!saved || errors != 0)
                throw new IOException($"SolidWorks 保存失败：errors={errors}, warnings={warnings}");
        }
        finally
        {
            Release(extension);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Release(_activeSketch);
        _activeSketch = null;
        if (_document is not null)
        {
            var title = TryGetTitle(_document);
            if (!string.IsNullOrWhiteSpace(title))
                TryRun(() => Invoke(_applicationInterface, _application!, "CloseDoc", title));
        }
        Release(_document);
        _document = null;
        if (_ownership.OwnsInstance && _application is not null)
            TryRun(() => Invoke(_applicationInterface, _application, "ExitApp"));
        Release(_application);
        _application = null;
        if (_ownership.OwnsInstance)
            _ = _ownership.WaitForOwnedExit(TimeSpan.FromSeconds(30));
    }

    private void SelectSketch(SketchRef sketch)
    {
        ThrowIfDisposed();
        var documentInterface = Interface("IModelDoc2");
        var extension = GetProperty(documentInterface, _document!, "Extension");
        try
        {
            Invoke(documentInterface, _document!, "ClearSelection2", true);
            if (!Convert.ToBoolean(Invoke(
                    Interface("IModelDocExtension"),
                    extension!,
                    "SelectByID2",
                    sketch.Name,
                    "SKETCH",
                    0d,
                    0d,
                    0d,
                    false,
                    0,
                    null,
                    0)))
                throw new InvalidOperationException("无法选择草图：" + sketch.Name);
        }
        finally
        {
            Release(extension);
        }
    }

    private void EnsureActive(SketchRef sketch)
    {
        ThrowIfDisposed();
        if (_activeSketch is null)
            throw new InvalidOperationException("当前没有打开草图：" + sketch.Name);
    }

    private bool SelectReferencePlane(ReferencePlane plane)
    {
        var documentInterface = Interface("IModelDoc2");
        var featureInterface = Interface("IFeature");
        object? feature = Invoke(documentInterface, _document!, "FirstFeature");
        var targetIndex = (int)plane;
        var currentIndex = 0;
        try
        {
            while (feature is not null)
            {
                var typeName = Convert.ToString(Invoke(featureInterface, feature, "GetTypeName2")) ?? string.Empty;
                if (typeName.Contains("RefPlane", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentIndex == targetIndex)
                        return Convert.ToBoolean(Invoke(featureInterface, feature, "Select2", false, 0));
                    currentIndex++;
                }
                var next = Invoke(featureInterface, feature, "GetNextFeature");
                Release(feature);
                feature = next;
            }
            return false;
        }
        finally
        {
            Release(feature);
        }
    }

    private static string PlaneLabel(ReferencePlane plane) => plane switch
    {
        ReferencePlane.Front => "Front",
        ReferencePlane.Top => "Top",
        ReferencePlane.Right => "Right",
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    private void RenameLatestSketchFeature(string name)
    {
        var documentInterface = Interface("IModelDoc2");
        var featureInterface = Interface("IFeature");
        object? feature = Invoke(documentInterface, _document!, "FirstFeature");
        object? latestSketch = null;
        try
        {
            while (feature is not null)
            {
                var typeName = Convert.ToString(Invoke(featureInterface, feature, "GetTypeName2"));
                var next = Invoke(featureInterface, feature, "GetNextFeature");
                if (string.Equals(typeName, "ProfileFeature", StringComparison.Ordinal))
                {
                    Release(latestSketch);
                    latestSketch = feature;
                    feature = null;
                }
                else
                {
                    Release(feature);
                }
                feature = next;
            }
            if (latestSketch is null)
                throw new InvalidOperationException("SolidWorks 未找到刚创建的草图特征。");
            SetProperty(featureInterface, latestSketch, "Name", name);
        }
        finally
        {
            Release(feature);
            Release(latestSketch);
        }
    }

    private string DescribeReferencePlanes()
    {
        var documentInterface = Interface("IModelDoc2");
        var featureInterface = Interface("IFeature");
        object? feature = Invoke(documentInterface, _document!, "FirstFeature");
        var planes = new List<string>();
        try
        {
            while (feature is not null)
            {
                var typeName = Convert.ToString(Invoke(featureInterface, feature, "GetTypeName2")) ?? string.Empty;
                if (typeName.Contains("RefPlane", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Convert.ToString(GetProperty(featureInterface, feature, "Name")) ?? "<unnamed>";
                    planes.Add(name + "(" + typeName + ")");
                }
                var next = Invoke(featureInterface, feature, "GetNextFeature");
                Release(feature);
                feature = next;
            }
            return planes.Count == 0 ? "<none>" : string.Join(", ", planes);
        }
        finally
        {
            Release(feature);
        }
    }

    private string? TryGetTitle(object document)
    {
        try { return Convert.ToString(Invoke(Interface("IModelDoc2"), document, "GetTitle")); }
        catch { return null; }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }

    private static object? Invoke(Type interfaceType, object target, string methodName, params object?[] parameters)
    {
        var method = interfaceType.GetMethod(methodName)
            ?? throw new MissingMethodException(interfaceType.FullName, methodName);
        try
        {
            return method.Invoke(target, parameters);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object? GetProperty(Type interfaceType, object target, string propertyName)
    {
        var property = interfaceType.GetProperty(propertyName)
            ?? throw new MissingMemberException(interfaceType.FullName, propertyName);
        try
        {
            return property.GetValue(target);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void SetProperty(Type interfaceType, object target, string propertyName, object? value)
    {
        var property = interfaceType.GetProperty(propertyName)
            ?? throw new MissingMemberException(interfaceType.FullName, propertyName);
        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private Type Interface(string name)
        => _applicationInterface.Assembly.GetType("SolidWorks.Interop.sldworks." + name)
            ?? throw new TypeLoadException("未找到 SolidWorks API 接口：" + name);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SolidWorksPartBackend));
    }
}

internal static class SolidWorksInteropTypes
{
    private const string ApplicationInterfaceName = "SolidWorks.Interop.sldworks.ISldWorks";

    public static Type LoadApplicationInterface(Type progIdType)
    {
        var installDirectory = GetInstallDirectory(progIdType);
        var interopPath = Path.Combine(
            installDirectory,
            "api",
            "redist",
            "SolidWorks.Interop.sldworks.dll");
        if (!File.Exists(interopPath))
        {
            throw new FileNotFoundException(
                "SolidWorks COM 已注册，但未找到官方 API Interop。",
                interopPath);
        }
        return Assembly.LoadFrom(interopPath).GetType(ApplicationInterfaceName)
            ?? throw new TypeLoadException("未找到 SolidWorks 应用程序接口：" + ApplicationInterfaceName);
    }

    public static string GetInstallDirectory(Type progIdType)
        => Path.GetDirectoryName(ResolveLocalServer(progIdType))
            ?? throw new InvalidOperationException("SolidWorks COM 注册路径无效。");

    private static string ResolveLocalServer(Type progIdType)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(
            @"CLSID\" + progIdType.GUID.ToString("B") + @"\LocalServer32");
        var command = Convert.ToString(key?.GetValue(null));
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("SolidWorks COM 注册缺少 LocalServer32 路径。");
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var executable = File.Exists(expanded)
            ? expanded
            : expanded.StartsWith('"')
                ? expanded.Split('"', StringSplitOptions.RemoveEmptyEntries)[0]
                : expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!File.Exists(executable))
            throw new FileNotFoundException("SolidWorks COM 注册的本地服务程序不存在。", executable);
        return executable;
    }
}

internal static class SolidWorksPartTemplateResolver
{
    public static IReadOnlyList<string> FindCandidates(
        string? configuredTemplate,
        string installDirectory,
        string? commonApplicationData = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFile(configuredTemplate, candidates, seen);

        var folders = new List<string> { Path.Combine(installDirectory, "templates") };
        var programData = string.IsNullOrWhiteSpace(commonApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : commonApplicationData;
        var productRoot = Path.Combine(programData, "SOLIDWORKS");
        if (Directory.Exists(productRoot))
        {
            try
            {
                folders.AddRange(Directory.EnumerateDirectories(productRoot, "SOLIDWORKS *")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Path.Combine(path, "templates")));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.prtdot", SearchOption.TopDirectoryOnly)
                             .OrderBy(Priority)
                             .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddFile(path, candidates, seen);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return candidates;
    }

    private static int Priority(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "gb_part.prtdot", StringComparison.OrdinalIgnoreCase))
            return 0;
        return name.Contains("part", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static void AddFile(string? path, ICollection<string> candidates, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetExtension(path), ".prtdot", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return;
        }
        var fullPath = Path.GetFullPath(path);
        if (seen.Add(fullPath))
            candidates.Add(fullPath);
    }
}

internal sealed class CadProcessOwnership
{
    private readonly string _processName;
    private readonly HashSet<int> _before;

    private CadProcessOwnership(string processName)
    {
        _processName = processName;
        _before = GetProcessIds(processName);
    }

    public int OwnedProcessId { get; private set; }
    public bool OwnsInstance => OwnedProcessId > 0;

    public static CadProcessOwnership Capture(string processName) => new(processName);

    public void Resolve(long windowHandle)
    {
        var after = GetProcessIds(_processName);
        var newProcesses = after.Except(_before).ToArray();
        if (windowHandle != 0)
        {
            _ = GetWindowThreadProcessId(new IntPtr(windowHandle), out var processId);
            if (processId > 0 && !_before.Contains((int)processId) && after.Contains((int)processId))
            {
                OwnedProcessId = (int)processId;
                return;
            }
        }
        if (newProcesses.Length == 1)
            OwnedProcessId = newProcesses[0];
    }

    public void WaitForNewProcess(TimeSpan timeout)
    {
        if (OwnsInstance)
            return;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            Resolve(0);
            if (OwnsInstance)
                return;
            Thread.Sleep(100);
        }
    }

    public bool WaitForOwnedExit(TimeSpan timeout)
    {
        if (!OwnsInstance)
            return true;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(OwnedProcessId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
                result.Add(process.Id);
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
