using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HistoryMinerva.Contracts;
using HistoryMinerva;
using SWuse.Contracts;

namespace SWuse;

internal static class SWuseWorkerClient
{
    public static string ResolveWorkerPath(MappingRuntimePaths? runtimePaths = null)
        => WorkerLocator.Locate(runtimePaths ?? MappingRuntimePaths.CreateAppShellFallback());

    public static async Task<SWuseBuildResult> RunAsync(SWuseBuildRequest request, CancellationToken cancellationToken)
    {
        var workerPath = ResolveWorkerPath();
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("未找到 HistoryMinerva Worker，请重新构建或同步模块。", workerPath);

        var requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            HistoryMinervaIdentity.DataDirectoryName,
            HistoryMinervaIdentity.DataDirectoryName,
            "requests");
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, SWuseJson.CreateOptions()),
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = workerPath,
                    Arguments = "--request " + Quote(requestPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            if (!process.Start())
                throw new InvalidOperationException("无法启动 SWuse Worker。");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<SWuseBuildResult>(output, SWuseJson.CreateOptions());
            if (result is not null)
                return result;
            throw new InvalidDataException("Worker 没有返回有效 JSON。stderr=" + error);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    private static string Quote(string path) => '"' + path.Replace("\"", "\\\"") + '"';

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
