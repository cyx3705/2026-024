using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AppShell.Core.Docking;
using AppShell.Core.Modules;
using SWuse;
using SWuse.Api;
using SWuse.Contracts;
using SWuse.Worker;

var root = Path.Combine(Path.GetTempPath(), "swuse-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    TestVersionAndIdentity();
    TestApiGeometryAndValidation();
    TestWorkspace(root);
    TestWorkerValidator(root);
    TestPhysicalTemplateFallback(root);
    TestDryRunCompiler(root);
    TestWorkerProtocol(root);
    TestShellHostAbstains();
    Console.WriteLine("SWuse.Smoke: PASS");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void TestVersionAndIdentity()
{
    Equal("0.1.1", typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3),
        "UI assembly version must come from the shared version props");
    Equal("0.1.1", typeof(PartBuilder).Assembly.GetName().Version?.ToString(3),
        "API assembly version must come from the shared version props");
    Equal("0.1.1", typeof(SWuseBuildRequest).Assembly.GetName().Version?.ToString(3),
        "Contracts assembly version must come from the shared version props");
    Equal("0.1.1", typeof(WorkerRequestValidator).Assembly.GetName().Version?.ToString(3),
        "Worker assembly version must come from the shared version props");
    Equal("0.1.1", new ModuleInfo().Version, "ModuleInfo must not retain a separate version literal");
    Equal("swuse", SWuseIdentity.CommandDomain, "Module command domain must remain stable");
    Equal("swuse", SWuseIdentity.WindowId, "Standalone window identity must remain stable");
}

static void TestApiGeometryAndValidation()
{
    var backend = new RecordingBackend();
    var part = new PartBuilder(backend);
    var sketch = part.Sketch("Base", ReferencePlane.Front, draw =>
    {
        draw.CenteredRectangle(0, 0, 0.06, 0.04);
        draw.Circle(0, 0, 0.005);
    });
    var boss = part.Extrude("Boss", sketch, 0.01);
    var cut = part.CutExtrude("Hole", sketch, 0.005, reverse: true);

    Equal("Base", sketch.Name, "Sketch references must retain their declared name");
    Equal("Boss", boss.Name, "Feature references must retain their declared name");
    Equal("Hole", cut.Name, "Feature references must retain their declared name");
    Equal(1, backend.BeginSketchCount, "Sketch must begin once");
    Equal(1, backend.EndSketchCount, "Sketch must close after drawing");
    Equal(4, backend.Lines.Count, "Centered rectangle must emit four lines");
    Equal(1, backend.Circles.Count, "Circle must be delegated to the backend");
    Equal(("Boss", 0.01d, false), backend.Extrudes.Single(), "Extrude arguments must be preserved in metres");
    Equal(("Hole", 0.005d, true), backend.Cuts.Single(), "Cut arguments must be preserved in metres");

    Throws<ArgumentOutOfRangeException>(() => part.Extrude("Invalid", sketch, 0));
    Throws<ArgumentOutOfRangeException>(() => part.Sketch("Invalid", ReferencePlane.Front, draw => draw.Circle(0, 0, 0)));
    Equal(2, backend.EndSketchCount, "A failed sketch body must still close the active sketch");
}

static void TestWorkspace(string root)
{
    var workspace = Path.Combine(root, "workspace");
    SWuseWorkspace.Initialize(workspace);
    var helper = SWuseWorkspace.CreateHelperClass(workspace);
    Directory.CreateDirectory(Path.Combine(workspace, "bin"));
    File.WriteAllText(Path.Combine(workspace, "bin", "Ignored.cs"), "public sealed class Ignored { }");

    var files = SWuseWorkspace.SourceFiles(workspace);
    Equal(2, files.Count, "Workspace must list the entry and helper source files only");
    True(files.Contains(helper, StringComparer.OrdinalIgnoreCase), "Helper source must be visible to the workspace");
    True(!files.Any(path => path.Contains("bin", StringComparison.OrdinalIgnoreCase)), "Build folders must be ignored");
    True(SWuseWorkspace.DefaultProgram.Contains("[SwuseEntry]", StringComparison.Ordinal),
        "Default source must declare a runnable entry");
}

static void TestWorkerValidator(string root)
{
    var workspace = Path.Combine(root, "validator-workspace");
    var outside = Path.Combine(root, "outside.cs");
    Directory.CreateDirectory(workspace);
    var source = Path.Combine(workspace, "Program.cs");
    File.WriteAllText(source, "public sealed class Program { }");
    File.WriteAllText(outside, "public sealed class Outside { }");
    var output = Path.Combine(root, "out", "Result.SLDPRT");

    WorkerRequestValidator.Validate(new SWuseBuildRequest(workspace, output, [source], DryRun: true));
    Throws<InvalidDataException>(() => WorkerRequestValidator.Validate(new SWuseBuildRequest(workspace, output, [outside], DryRun: true)));
    Throws<InvalidDataException>(() => WorkerRequestValidator.Validate(new SWuseBuildRequest(workspace, Path.Combine(root, "out", "Result.txt"), [source], DryRun: true)));
    Throws<InvalidDataException>(() => WorkerRequestValidator.Validate(new SWuseBuildRequest(workspace, output, [source, source], DryRun: true)));
}

static void TestPhysicalTemplateFallback(string root)
{
    var programData = Path.Combine(root, "template-program-data");
    var templates = Path.Combine(programData, "SOLIDWORKS", "SOLIDWORKS 2099", "templates");
    Directory.CreateDirectory(templates);
    var standard = Path.Combine(templates, "gb_part.prtdot");
    var secondary = Path.Combine(templates, "z_part.prtdot");
    File.WriteAllText(standard, "standard");
    File.WriteAllText(secondary, "secondary");

    var fallback = SolidWorksPartTemplateResolver.FindCandidates(
        "~BLANK_PART_TEMPLATE.prtdot",
        Path.Combine(root, "missing-install"),
        programData);
    Equal(standard, fallback.First(), "Virtual default template must fall back to a physical gb_part.prtdot");

    var configured = Path.Combine(root, "configured.prtdot");
    File.WriteAllText(configured, "configured");
    var configuredFirst = SolidWorksPartTemplateResolver.FindCandidates(
        configured,
        Path.Combine(root, "missing-install"),
        programData);
    Equal(configured, configuredFirst.First(), "A valid configured physical template must retain first priority");
}

static void TestDryRunCompiler(string root)
{
    var validWorkspace = CreateWorkspace(root, "valid", new Dictionary<string, string>
    {
        ["Helper.cs"] = "namespace UserBuild; public static class Units { public static double Mm(double value) => value / 1000d; }",
        ["Part.cs"] = "using SWuse.Api; using UserBuild; [SwuseEntry] public sealed class ValidPart : PartProgram { public override void Build(PartBuilder part) { var sketch = part.Sketch(\"Base\", ReferencePlane.Front, draw => draw.CenteredRectangle(0, 0, Units.Mm(60), Units.Mm(40))); part.Extrude(\"Boss\", sketch, Units.Mm(10)); } }",
    });
    var valid = ExecuteDryRun(validWorkspace, "Valid.SLDPRT");
    True(valid.Success, "Multiple source files with cross-file references must compile and validate");

    var noEntryWorkspace = CreateWorkspace(root, "no-entry", new Dictionary<string, string>
    {
        ["Part.cs"] = "using SWuse.Api; public sealed class NoEntry : PartProgram { public override void Build(PartBuilder part) { } }",
    });
    var noEntry = ExecuteDryRun(noEntryWorkspace, "NoEntry.SLDPRT");
    True(!noEntry.Success && noEntry.Summary.Contains("[SwuseEntry]", StringComparison.Ordinal),
        "Dry-run must reject a missing entry before SolidWorks starts");

    var multipleEntryWorkspace = CreateWorkspace(root, "multiple-entry", new Dictionary<string, string>
    {
        ["Parts.cs"] = "using SWuse.Api; [SwuseEntry] public sealed class First : PartProgram { public override void Build(PartBuilder part) { } } [SwuseEntry] public sealed class Second : PartProgram { public override void Build(PartBuilder part) { } }",
    });
    var multipleEntry = ExecuteDryRun(multipleEntryWorkspace, "Multiple.SLDPRT");
    True(!multipleEntry.Success && multipleEntry.Diagnostics.Any(item => item.Severity == BuildDiagnosticSeverity.Error),
        "Dry-run must reject more than one entry");

    var invalidWorkspace = CreateWorkspace(root, "invalid", new Dictionary<string, string>
    {
        ["Broken.cs"] = "using SWuse.Api; [SwuseEntry] public sealed class Broken : PartProgram { public override void Build(PartBuilder part) { this does not compile; } }",
    });
    var invalid = ExecuteDryRun(invalidWorkspace, "Invalid.SLDPRT");
    True(!invalid.Success && invalid.Diagnostics.Any(item => item.Severity == BuildDiagnosticSeverity.Error),
        "Roslyn compiler errors must be returned as structured diagnostics");
}

static void TestWorkerProtocol(string root)
{
    var workspace = CreateWorkspace(root, "worker-protocol", new Dictionary<string, string>
    {
        ["Part.cs"] = "using SWuse.Api; [SwuseEntry] public sealed class ProtocolPart : PartProgram { public override void Build(PartBuilder part) { } }",
    });
    var request = new SWuseBuildRequest(
        workspace,
        Path.Combine(workspace, "out", "Protocol.SLDPRT"),
        SWuseWorkspace.SourceFiles(workspace),
        DryRun: true);
    var requestPath = Path.Combine(workspace, "request.json");
    File.WriteAllText(requestPath, JsonSerializer.Serialize(request, SWuseJson.CreateOptions()));

    var workerPath = typeof(WorkerRequestValidator).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "SWuseWorkerPath")?.Value;
    if (string.IsNullOrWhiteSpace(workerPath))
    {
        // The metadata lives on the smoke assembly, not the referenced worker assembly.
        workerPath = Assembly.GetEntryAssembly()!
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SWuseWorkerPath").Value;
    }
    True(!string.IsNullOrWhiteSpace(workerPath) && File.Exists(workerPath), "Release worker must be available for protocol smoke");

    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        Arguments = Quote(workerPath!) + " --request " + Quote(requestPath),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to start SWuse.Worker for protocol smoke");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    True(process.WaitForExit(30_000), "Dry-run worker must finish promptly");
    Equal(0, process.ExitCode, "Worker dry-run must return success. stderr=" + error);
    var result = JsonSerializer.Deserialize<SWuseBuildResult>(output, SWuseJson.CreateOptions());
    True(result is { Success: true }, "Worker must return a valid successful JSON result");
    True(!Directory.Exists(Path.Combine(workspace, "out")), "Dry-run must not create an output directory");
}

static void TestShellHostAbstains()
{
    var registrar = new RecordingShellUiRegistrar();
    var module = new SWuseUiModule();
    ((IShellUiAware)module).ShellUi = registrar;
    module.CreateUi();
    Equal(0, registrar.Descriptors.Count, "Standalone SWuse must not register a docked AppShell tool window");
    True(!SWuseWindowHost.IsCreated, "SWuse module registration must not auto-start the standalone window");
    module.DestroyUi();
}

static SWuseBuildResult ExecuteDryRun(string workspace, string outputFileName)
    => BuildExecutor.Execute(
        new SWuseBuildRequest(workspace, Path.Combine(workspace, "out", outputFileName), SWuseWorkspace.SourceFiles(workspace), DryRun: true),
        Stopwatch.StartNew());

static string CreateWorkspace(string root, string name, IReadOnlyDictionary<string, string> files)
{
    var workspace = Path.Combine(root, name);
    Directory.CreateDirectory(workspace);
    foreach (var (fileName, contents) in files)
        File.WriteAllText(Path.Combine(workspace, fileName), contents);
    return workspace;
}

static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}. Expected={expected}, Actual={actual}");
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

sealed class RecordingBackend : IPartBackend
{
    public int BeginSketchCount { get; private set; }
    public int EndSketchCount { get; private set; }
    public List<(double X1, double Y1, double X2, double Y2)> Lines { get; } = [];
    public List<(double X, double Y, double Radius)> Circles { get; } = [];
    public List<(string Name, double Depth, bool Reverse)> Extrudes { get; } = [];
    public List<(string Name, double Depth, bool Reverse)> Cuts { get; } = [];

    public SketchRef BeginSketch(string name, ReferencePlane plane)
    {
        BeginSketchCount++;
        return new SketchRef(name);
    }

    public void EndSketch(SketchRef sketch) => EndSketchCount++;
    public void AddLine(SketchRef sketch, double x1, double y1, double x2, double y2) => Lines.Add((x1, y1, x2, y2));
    public void AddCircle(SketchRef sketch, double x, double y, double radius) => Circles.Add((x, y, radius));
    public FeatureRef Extrude(string name, SketchRef sketch, double depthMeters, bool reverse)
    {
        Extrudes.Add((name, depthMeters, reverse));
        return new FeatureRef(name);
    }

    public FeatureRef CutExtrude(string name, SketchRef sketch, double depthMeters, bool reverse)
    {
        Cuts.Add((name, depthMeters, reverse));
        return new FeatureRef(name);
    }
}

sealed class RecordingShellUiRegistrar : IShellUiRegistrar
{
    public List<ToolWindowDescriptor> Descriptors { get; } = [];
    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Descriptors.Add(descriptor);
        return new CallbackDisposable();
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }
}

sealed class CallbackDisposable : IDisposable
{
    public void Dispose()
    {
    }
}
