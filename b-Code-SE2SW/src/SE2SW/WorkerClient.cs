using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class WorkerClient
{
    private static readonly TimeSpan StageInactivityTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 装配构建的无进度预算。
    ///
    /// 装配阶段每个节点才报一条事件，而单个节点要插入组件、逐个设变换、
    /// 重建配合、再存盘——25 个组件的节点安静超过 180 秒是常态，不是卡死。
    /// 现场事故：按零件的 180 秒预算去卡装配，装配管线被反复误杀。
    /// </summary>
    private static readonly TimeSpan AssemblyInactivityTimeout = TimeSpan.FromMinutes(20);

    /// <summary>零件按 180 秒卡；装配用自己的预算。看门狗防的是真死，不是慢。</summary>
    private static TimeSpan InactivityBudgetFor(string verb)
        => verb == WorkerProtocol.AssemblyBuildVerb ? AssemblyInactivityTimeout : StageInactivityTimeout;
    private static readonly JsonSerializerOptions JsonOptions = WorkerProtocol.CreateJsonOptions();

    public static string WorkerPath
        => WorkerLocator.Locate();

    public async Task<int> RunAsync(
        BatchRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        => await RunWorkerAsync(
            WorkerProtocol.PartsRequestVerb,
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task<AssemblyProbeResult> ProbeAssemblyAsync(
        AssemblyProbeRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var exitCode = await RunWorkerAsync(
            WorkerProtocol.AssemblyProbeVerb,
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (exitCode is not (0 or 1))
            throw new InvalidOperationException($"装配探查工作进程失败，退出码 {exitCode}。");
        if (!File.Exists(request.ResultPath))
            throw new InvalidDataException("装配探查未生成结果清单。");

        await using var stream = File.OpenRead(request.ResultPath);
        return await JsonSerializer.DeserializeAsync<AssemblyProbeResult>(
                   stream,
                   JsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("装配探查结果清单为空。");
    }

    public async Task<int> RunAssemblyAsync(
        AssemblyBatchRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        => await RunWorkerAsync(
            WorkerProtocol.AssemblyBuildVerb,
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunWorkerAsync<TRequest>(
        string verb,
        string batchId,
        TRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SE2SWIdentity.HostApplicationDataDirectoryName,
            SE2SWIdentity.ModuleApplicationDataDirectoryName,
            SE2SWIdentity.RequestsDirectoryName);
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, batchId + ".json");
        var cancellationPath = Path.Combine(requestDirectory, batchId + ".cancel");
        TryDelete(cancellationPath);
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = WorkerPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add(verb);
            process.StartInfo.ArgumentList.Add(requestPath);
            process.StartInfo.ArgumentList.Add(WorkerProtocol.CancellationArgument);
            process.StartInfo.ArgumentList.Add(cancellationPath);
            if (!process.Start())
                throw new InvalidOperationException("无法启动 SE2SW 工作进程。");

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    File.WriteAllText(cancellationPath, "cancel");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            });

            var stderrTask = process.StandardError.ReadToEndAsync();
            var inactivityBudget = InactivityBudgetFor(verb);
            var timedOut = false;
            while (true)
            {
                var lineTask = process.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();
                var timeoutTask = Task.Delay(inactivityBudget);
                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var completed = await Task.WhenAny(lineTask, timeoutTask, cancellationTask).ConfigureAwait(false);
                if (completed != lineTask)
                {
                    TrySignalCancellation(cancellationPath);
                    timedOut = completed == timeoutTask;
                    if (timedOut)
                    {
                        progress(new WorkerEvent(
                            batchId,
                            null,
                            ConversionStage.Failed,
                            $"CAD 阶段连续 {inactivityBudget.TotalSeconds:0} 秒无进度，工作进程已终止。",
                            IsError: true,
                            ErrorClass: ConversionErrorClass.Timeout));
                    }
                    await ForceTerminateAfterGracePeriodAsync(process).ConfigureAwait(false);
                }

                var line = await lineTask.ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions);
                    if (workerEvent is not null)
                        progress(workerEvent);
                }
                catch (JsonException)
                {
                    progress(new WorkerEvent(batchId, null, ConversionStage.Failed, line, IsError: true));
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                progress(new WorkerEvent(batchId, null, ConversionStage.Failed, stderr.Trim(), IsError: true));
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut)
                return 4;
            return process.ExitCode;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(cancellationPath);
        }
    }

    private static void TrySignalCancellation(string cancellationPath)
    {
        try
        {
            File.WriteAllText(cancellationPath, "cancel");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task ForceTerminateAfterGracePeriodAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
