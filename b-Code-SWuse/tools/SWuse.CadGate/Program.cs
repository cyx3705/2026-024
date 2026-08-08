using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using SWuse.Contracts;

var keepArtifacts = args.Any(argument => string.Equals(argument, "--keep", StringComparison.OrdinalIgnoreCase));
var moduleRoot = FindModuleRoot();
var workerPath = Path.Combine(moduleRoot, "src", "SWuse.Worker", "bin", "Release", "net8.0-windows", "win-x64", "SWuse.Worker.exe");
if (!File.Exists(workerPath))
    throw new FileNotFoundException("Release SWuse.Worker.exe was not found. Build SWuse first.", workerPath);

var beforeSolidWorks = ProcessIds("SLDWORKS");
if (beforeSolidWorks.Count != 0)
    throw new InvalidOperationException("SWuse.CadGate requires SolidWorks to be closed so it can verify process cleanup without touching a user session.");
var root = Path.Combine(Path.GetTempPath(), "swuse-cad-gate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var sourcePath = Path.Combine(root, "DemoPart.cs");
var outputPath = Path.Combine(root, "output", "Demo.SLDPRT");
try
{
    File.WriteAllText(sourcePath, """
        using SWuse.Api;

        [SwuseEntry]
        public sealed class DemoPart : PartProgram
        {
            public override void Build(PartBuilder part)
            {
                var baseSketch = part.Sketch("Base", ReferencePlane.Front, sketch =>
                    sketch.CenteredRectangle(0, 0, 0.06, 0.04));
                part.Extrude("BaseBoss", baseSketch, 0.01);

                var holeSketch = part.Sketch("Hole", ReferencePlane.Front, sketch =>
                    sketch.Circle(0, 0, 0.005));
                part.CutExtrude("CenterHole", holeSketch, 0.01);
            }
        }
        """);
    var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)));
    var requestPath = Path.Combine(root, "request.json");
    var request = new SWuseBuildRequest(root, outputPath, [sourcePath]);
    File.WriteAllText(requestPath, JsonSerializer.Serialize(request, SWuseJson.CreateOptions()));

    using var worker = Process.Start(new ProcessStartInfo
    {
        FileName = workerPath,
        Arguments = "--request " + Quote(requestPath),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to start SWuse.Worker.");
    var stdout = worker.StandardOutput.ReadToEnd();
    var stderr = worker.StandardError.ReadToEnd();
    if (!worker.WaitForExit(120_000))
        throw new TimeoutException("SWuse.Worker did not complete in two minutes.");
    var result = JsonSerializer.Deserialize<SWuseBuildResult>(stdout, SWuseJson.CreateOptions())
        ?? throw new InvalidDataException("SWuse.Worker did not return JSON. stderr=" + stderr);
    if (worker.ExitCode != 0 || !result.Success)
        throw new InvalidOperationException("SWuse.Worker failed: " + result.Summary + Environment.NewLine + stderr);
    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        throw new InvalidDataException("Worker reported success but did not create a non-empty SLDPRT.");
    var sourceHashAfter = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)));
    if (!string.Equals(sourceHash, sourceHashAfter, StringComparison.Ordinal))
        throw new InvalidDataException("Worker modified the source file.");

    var features = ReadFeatureNames(outputPath);
    Require(features, "BaseBoss");
    Require(features, "CenterHole");
    WaitForNoNewSolidWorks(beforeSolidWorks, TimeSpan.FromSeconds(30));
    Console.WriteLine("SWuse.CadGate: PASS");
    Console.WriteLine("Output=" + outputPath);
    Console.WriteLine("Features=" + string.Join(", ", features));
}
catch
{
    Console.Error.WriteLine("SWuse.CadGate artifacts retained at " + root);
    throw;
}
finally
{
    if (keepArtifacts)
        Console.WriteLine("SWuse.CadGate artifacts retained at " + root);
    else if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

static IReadOnlyList<string> ReadFeatureNames(string outputPath)
{
    const int documentTypePart = 1;
    const int openSilent = 1;
    var comType = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
        ?? throw new InvalidOperationException("SolidWorks COM registration was not found.");
    var applicationInterface = LoadApplicationInterface(comType);
    var modelInterface = GetInterface(applicationInterface, "IModelDoc2");
    var featureInterface = GetInterface(applicationInterface, "IFeature");
    object? application = null;
    object? document = null;
    try
    {
        application = Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("SolidWorks returned a null automation instance.");
        SetProperty(applicationInterface, application, "Visible", false);
        SetProperty(applicationInterface, application, "UserControl", false);
        object?[] openParameters = [outputPath, documentTypePart, openSilent, string.Empty, 0, 0];
        document = Invoke(applicationInterface, application, "OpenDoc6", openParameters)
            ?? throw new InvalidOperationException("SolidWorks returned no document when reopening the result.");
        var errors = Convert.ToInt32(openParameters[4]);
        var warnings = Convert.ToInt32(openParameters[5]);
        if (errors != 0)
            throw new InvalidOperationException($"SolidWorks could not reopen the result. errors={errors}, warnings={warnings}");
        var names = new List<string>();
        object? feature = Invoke(modelInterface, document, "FirstFeature");
        while (feature is not null)
        {
            var name = Convert.ToString(GetProperty(featureInterface, feature, "Name"));
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
            var next = Invoke(featureInterface, feature, "GetNextFeature");
            Release(feature);
            feature = next;
        }
        return names;
    }
    finally
    {
        if (document is not null && application is not null)
        {
            try
            {
                var title = Convert.ToString(Invoke(modelInterface, document, "GetTitle"));
                if (!string.IsNullOrWhiteSpace(title))
                    Invoke(applicationInterface, application, "CloseDoc", title);
            }
            catch
            {
                // Cleanup is best-effort; the caller still receives the primary verification error.
            }
        }
        Release(document);
        if (application is not null)
        {
            try { Invoke(applicationInterface, application, "ExitApp"); } catch { }
        }
        Release(application);
    }
}

static void Require(IEnumerable<string> names, string required)
{
    if (!names.Contains(required, StringComparer.Ordinal))
        throw new InvalidDataException("The reopened SLDPRT is missing feature '" + required + "'. Found: " + string.Join(", ", names));
}

static void WaitForNoNewSolidWorks(IReadOnlySet<int> before, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (ProcessIds("SLDWORKS").All(before.Contains))
            return;
        Thread.Sleep(250);
    }
    var remaining = ProcessIds("SLDWORKS").Where(id => !before.Contains(id));
    throw new InvalidOperationException("SWuse left SolidWorks processes running: " + string.Join(", ", remaining));
}

static HashSet<int> ProcessIds(string name)
{
    var ids = new HashSet<int>();
    foreach (var process in Process.GetProcessesByName(name))
    {
        using (process)
            ids.Add(process.Id);
    }
    return ids;
}

static string FindModuleRoot()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
    {
        if (File.Exists(Path.Combine(current.FullName, "build", "SWuse.Version.props")))
            return current.FullName;
    }
    throw new DirectoryNotFoundException("Could not locate the SWuse module root from " + AppContext.BaseDirectory);
}

static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';

static void Release(object? value)
{
    if (value is not null && Marshal.IsComObject(value))
        Marshal.FinalReleaseComObject(value);
}

static Type LoadApplicationInterface(Type comType)
{
    using var key = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + comType.GUID.ToString("B") + @"\LocalServer32");
    var server = Convert.ToString(key?.GetValue(null));
    if (string.IsNullOrWhiteSpace(server))
        throw new InvalidOperationException("SolidWorks LocalServer32 registry entry is missing.");
    var executable = Environment.ExpandEnvironmentVariables(server.Trim().Trim('"'));
    var interopPath = Path.Combine(Path.GetDirectoryName(executable)!, "api", "redist", "SolidWorks.Interop.sldworks.dll");
    return Assembly.LoadFrom(interopPath).GetType("SolidWorks.Interop.sldworks.ISldWorks")
        ?? throw new TypeLoadException("SolidWorks ISldWorks interop type is missing.");
}

static Type GetInterface(Type applicationInterface, string name)
    => applicationInterface.Assembly.GetType("SolidWorks.Interop.sldworks." + name)
        ?? throw new TypeLoadException("SolidWorks interop type is missing: " + name);

static object? Invoke(Type interfaceType, object target, string methodName, params object?[] parameters)
{
    var method = interfaceType.GetMethod(methodName)
        ?? throw new MissingMethodException(interfaceType.FullName, methodName);
    try
    {
        return method.Invoke(target, parameters);
    }
    catch (TargetInvocationException exception) when (exception.InnerException is not null)
    {
        throw exception.InnerException;
    }
}

static object? GetProperty(Type interfaceType, object target, string propertyName)
{
    var property = interfaceType.GetProperty(propertyName)
        ?? throw new MissingMemberException(interfaceType.FullName, propertyName);
    return property.GetValue(target);
}

static void SetProperty(Type interfaceType, object target, string propertyName, object value)
{
    var property = interfaceType.GetProperty(propertyName)
        ?? throw new MissingMemberException(interfaceType.FullName, propertyName);
    property.SetValue(target, value);
}
