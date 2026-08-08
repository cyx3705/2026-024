using System.Diagnostics;
using System.Text.Json;
using SWuse.Api;
using SWuse.Contracts;

namespace SWuse.Worker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        SWuseBuildResult result;
        try
        {
            var requestPath = ReadRequestPath(args);
            var request = JsonSerializer.Deserialize<SWuseBuildRequest>(
                File.ReadAllText(requestPath),
                SWuseJson.CreateOptions())
                ?? throw new InvalidDataException("SWuse Worker 请求为空。");
            result = BuildExecutor.Execute(request, stopwatch);
        }
        catch (Exception ex)
        {
            result = new SWuseBuildResult(
                false,
                ex.Message,
                [new BuildDiagnostic(BuildDiagnosticSeverity.Error, ex.Message)],
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }

        Console.Out.Write(JsonSerializer.Serialize(result, SWuseJson.CreateOptions()));
        return result.Success ? 0 : 1;
    }

    private static string ReadRequestPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--request", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[index + 1]);
        }
        throw new InvalidDataException("用法：SWuse.Worker --request <request.json>");
    }
}

internal static class BuildExecutor
{
    public static SWuseBuildResult Execute(SWuseBuildRequest request, Stopwatch stopwatch)
    {
        WorkerRequestValidator.Validate(request);
        using var compiled = UserProgramCompiler.Compile(request);
        if (compiled.Diagnostics.Any(item => item.Severity == BuildDiagnosticSeverity.Error))
        {
            return new SWuseBuildResult(
                false,
                "C# 编译失败。",
                compiled.Diagnostics,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }

        if (request.DryRun)
        {
            try
            {
                compiled.ValidateEntry(request.EntryType);
            }
            catch (Exception ex)
            {
                return EntryFailure(compiled.Diagnostics, ex, stopwatch);
            }
            return new SWuseBuildResult(
                true,
                "C# 编译与入口验证通过；未启动 SolidWorks。",
                compiled.Diagnostics,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }

        PartProgram program;
        try
        {
            program = compiled.CreateProgram(request.EntryType);
        }
        catch (Exception ex)
        {
            return EntryFailure(compiled.Diagnostics, ex, stopwatch);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPartPath)!);
        using var backend = SolidWorksPartBackend.Create();
        program.Build(new PartBuilder(backend));
        backend.Save(request.OutputPartPath, request.Overwrite);
        return new SWuseBuildResult(
            true,
            $"已生成 SolidWorks 零件：{request.OutputPartPath}",
            compiled.Diagnostics,
            request.OutputPartPath,
            stopwatch.ElapsedMilliseconds);
    }

    private static SWuseBuildResult EntryFailure(
        IReadOnlyList<BuildDiagnostic> diagnostics,
        Exception exception,
        Stopwatch stopwatch)
    {
        var allDiagnostics = diagnostics
            .Append(new BuildDiagnostic(BuildDiagnosticSeverity.Error, exception.Message))
            .ToArray();
        return new SWuseBuildResult(
            false,
            exception.Message,
            allDiagnostics,
            ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
    }
}
