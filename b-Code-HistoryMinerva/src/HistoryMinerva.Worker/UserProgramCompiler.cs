using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SWuse.Api;
using SWuse.Contracts;

namespace SWuse.Worker;

internal sealed class CompiledUserProgram : IDisposable
{
    private readonly UserProgramLoadContext? _loadContext;
    private readonly Assembly? _assembly;

    public CompiledUserProgram(IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
    }

    public CompiledUserProgram(UserProgramLoadContext loadContext, Assembly assembly, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        _loadContext = loadContext;
        _assembly = assembly;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Verifies the entry declaration without running user code or starting SolidWorks.
    /// This is deliberately shared by dry-run and production execution so a dry-run is
    /// an honest preflight for the same source set.
    /// </summary>
    public void ValidateEntry(string? requestedEntryType)
    {
        _ = SelectEntryType(requestedEntryType);
    }

    public PartProgram CreateProgram(string? requestedEntryType)
    {
        var selected = SelectEntryType(requestedEntryType);
        if (Activator.CreateInstance(selected) is not PartProgram program)
            throw new InvalidOperationException("零件入口必须有公共无参构造函数：" + selected.FullName);
        return program;
    }

    private Type SelectEntryType(string? requestedEntryType)
    {
        if (_assembly is null)
            throw new InvalidOperationException("编译没有生成可执行程序集。");
        var candidates = _assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(PartProgram).IsAssignableFrom(type)
                && type.GetCustomAttribute<SwuseEntryAttribute>() is not null)
            .ToArray();
        var selected = string.IsNullOrWhiteSpace(requestedEntryType)
            ? candidates.Length == 1 ? candidates[0] : null
            : candidates.SingleOrDefault(type => string.Equals(type.FullName, requestedEntryType, StringComparison.Ordinal)
                || string.Equals(type.Name, requestedEntryType, StringComparison.Ordinal));
        if (selected is not null)
            return selected;
        var reason = string.IsNullOrWhiteSpace(requestedEntryType)
            ? $"需要恰有一个带 [SwuseEntry] 的 PartProgram，当前找到 {candidates.Length} 个。"
            : "未找到指定的 [SwuseEntry] PartProgram：" + requestedEntryType;
        throw new InvalidOperationException(reason);
    }

    public void Dispose()
    {
        _loadContext?.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

internal static class UserProgramCompiler
{
    public static CompiledUserProgram Compile(SWuseBuildRequest request)
    {
        var syntaxTrees = request.SourceFiles.Select(path =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
        var compilation = CSharpCompilation.Create(
            "SWuse.UserBuild",
            syntaxTrees,
            TrustedReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        var diagnostics = emitted.Diagnostics.Select(ToDiagnostic).ToArray();
        if (!emitted.Success)
            return new CompiledUserProgram(diagnostics);

        image.Position = 0;
        var context = new UserProgramLoadContext();
        var assembly = context.LoadFromStream(image);
        return new CompiledUserProgram(context, assembly, diagnostics);
    }

    private static IEnumerable<MetadataReference> TrustedReferences()
    {
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("运行时没有提供受信任平台程序集清单。");
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return MetadataReference.CreateFromFile(path);
        yield return MetadataReference.CreateFromFile(typeof(PartProgram).Assembly.Location);
    }

    private static BuildDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var file = diagnostic.Location.IsInSource ? lineSpan.Path : null;
        int? line = diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Line + 1 : null;
        int? column = diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Character + 1 : null;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => BuildDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => BuildDiagnosticSeverity.Warning,
            _ => BuildDiagnosticSeverity.Info,
        };
        return new BuildDiagnostic(severity, diagnostic.GetMessage(), file, line, column);
    }
}

internal sealed class UserProgramLoadContext : AssemblyLoadContext
{
    public UserProgramLoadContext() : base("SWuse.UserBuild", isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
        => string.Equals(assemblyName.Name, typeof(PartProgram).Assembly.GetName().Name, StringComparison.Ordinal)
            ? typeof(PartProgram).Assembly
            : null;
}
