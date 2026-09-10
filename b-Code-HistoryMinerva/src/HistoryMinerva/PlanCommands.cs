using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.10.2：**无状态**的只读计划命令。
///
/// 其余 <c>minerva.*</c> 命令都绑在页面当前选择上：调用方必须先 <c>ui.content</c>、
/// 再 <c>ui.source</c>、才能 <c>conversion.run</c>，于是同一时刻只有一份状态、
/// 没有并发、没有幂等，返回的还只是一句中文结论文本。别的模块想用 Minerva 的能力，
/// 最省事的做法反而是自己抄一份计划逻辑——那时候同宿主多模块就不如单体应用。
///
/// 这一组命令把「对这个路径做这件事」补上：
/// <list type="bullet">
///   <item>入参就是路径本身，**不碰 <see cref="AssemblyViewModel"/>、不碰页面状态**；</item>
///   <item>结果放进 <c>CommandResult.Data</c>，类型就是 <c>HistoryMinerva.Contracts</c> 里
///         已经公开的 <see cref="PackagePlan"/> / <see cref="AssemblyRenamePlan"/>，
///         消费方引用同一份合同程序集直接强转，不必解析中文文本；</item>
///   <item>只读且不隐藏，因此同时投影进 MCP，AI 也能问出「这台设备有哪些零件、各几个」。</item>
/// </list>
///
/// 它们**只解析、只计算，不改任何文件**：装配体以只读方式走一遍，计划全在内存里算完。
/// 真要动文件仍然走页面那条 <c>minerva.conversion.run</c>——写入需要用户在场确认，
/// 而不是被另一个模块顺手调掉。
/// </summary>
public sealed class PlanCommands : IModuleContextAware
{
    private MappingRuntimePaths _runtimePaths = MappingRuntimePaths.CreateHistoryVulcanDefault();
    private readonly Func<MappingRuntimePaths, IAssemblyProbe> _probeFactory;
    private bool _attached;

    public PlanCommands()
        : this(paths => new WorkerAssemblyProbe(new WorkerClient(paths)))
    {
    }

    /// <summary>测试注入点。生产路径恒为真 Worker。</summary>
    internal PlanCommands(Func<MappingRuntimePaths, IAssemblyProbe> probeFactory)
        => _probeFactory = probeFactory ?? throw new ArgumentNullException(nameof(probeFactory));

    private static readonly ParameterSpec PathParameter = new()
    {
        Name = "path",
        Description = "SolidWorks 总装配体 .SLDASM 的绝对路径",
        Required = true,
    };

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_attached)
            throw new InvalidOperationException("HistoryMinerva plan command context has already been attached.");

        _runtimePaths = MappingRuntimePaths.CreateHistoryVulcanDefault();
        _attached = true;
        context.RegisterCommands(registry =>
        {
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".plan.package",
                Domain = HistoryMinervaIdentity.CommandRoot,
                CommandClass = "plan",
                Summary = "只读解析装配体并返回整体打包计划（机加件/外购件两张表、数量、待导产物）",
                Example = HistoryMinervaIdentity.CommandRoot
                          + @".plan.package path=D:\设备\GHLSS-06-00 总装.SLDASM",
                Readonly = true,
                Parameters = [PathParameter],
                Handler = PlanPackageAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".plan.rename",
                Domain = HistoryMinervaIdentity.CommandRoot,
                CommandClass = "plan",
                Summary = "只读解析装配体并返回属性整备改名计划（不改任何文件）",
                Example = HistoryMinervaIdentity.CommandRoot
                          + @".plan.rename path=D:\设备\GHLSS-06-00 总装.SLDASM prefix=ZS-LHL",
                Readonly = true,
                Parameters =
                [
                    PathParameter,
                    new ParameterSpec
                    {
                        Name = "prefix",
                        Description = "图号前缀；省略则按根装配文件名推断",
                        Required = false,
                    },
                    new ParameterSpec
                    {
                        Name = "clearnumber",
                        Description = "为 true 时按空前缀出计划，即删图号；此时忽略 prefix",
                        Required = false,
                    },
                ],
                Handler = PlanRenameAsync,
            });
        });
    }

    private async Task<CommandResult> PlanPackageAsync(CommandContext command)
    {
        var probe = await TryProbeAsync(command).ConfigureAwait(false);
        if (probe.Failure is not null)
            return probe.Failure;

        var plan = PackagePlanner.Create(probe.Result!);
        return CommandResult.Ok(FormatPackageSummary(plan), plan);
    }

    private async Task<CommandResult> PlanRenameAsync(CommandContext command)
    {
        var probe = await TryProbeAsync(command).ConfigureAwait(false);
        if (probe.Failure is not null)
            return probe.Failure;

        var result = probe.Result!;
        string prefix;
        if (IsTrue(command.GetString("clearnumber")))
        {
            // 空前缀不是非法输入，而是「这一轮把图号改成空」。它与改成 ZS-LHL 走的是
            // 同一条计划，只是值不同（DEC-057）——所以这里只挑值，不挑分支。
            prefix = string.Empty;
        }
        else
        {
            var written = command.GetString("prefix");
            prefix = written is null
                ? DrawingNumber.InferPrefix(Path.GetFileName(result.SourceAssemblyPath))
                : written.Trim();
        }

        AssemblyRenamePlan plan;
        try
        {
            plan = PropertyPrepPlanner.Create(result, prefix);
        }
        catch (InvalidDataException ex)
        {
            return CommandResult.Fail(ex.Message);
        }

        return CommandResult.Ok(FormatRenameSummary(plan), plan);
    }

    /// <summary>
    /// 校验路径、校验环境、只读跑一遍探查。
    ///
    /// 三道校验都排在启动 Worker 之前：路径写错时不该先开一次 SolidWorks 再说不行。
    /// </summary>
    private async Task<ProbeAttempt> TryProbeAsync(CommandContext command)
    {
        var path = command.GetString("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return ProbeAttempt.Failed("需要 path：SolidWorks 总装配体 .SLDASM 的绝对路径。");
        if (!Path.IsPathFullyQualified(path)
            || !ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidWorksAssemblyExtension))
        {
            return ProbeAttempt.Failed("path 必须是绝对路径的 SolidWorks .SLDASM 装配体。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return ProbeAttempt.Failed($"装配体不存在：{fullPath}");

        var probe = _probeFactory(_runtimePaths);
        try
        {
            probe.ValidateEnvironment();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return ProbeAttempt.Failed(ex.Message);
        }

        var batchId = Guid.NewGuid().ToString("N");
        var resultDirectory = _runtimePaths.ProbesDirectory;
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, batchId + ".result.json");
        try
        {
            command.Progress?.Report($"正在只读解析 {Path.GetFileName(fullPath)}");
            var request = new AssemblyProbeRequest(
                batchId, fullPath, resultPath, ConversionSourceFormat.SolidWorks);
            var result = await probe.ProbeAsync(
                request,
                workerEvent => command.Progress?.Report(
                    ConversionProgressPresenter.FormatMessage(workerEvent)),
                command.Cancellation).ConfigureAwait(false);
            return ProbeAttempt.Succeeded(result);
        }
        catch (OperationCanceledException)
        {
            return ProbeAttempt.Failed("Minerva 解析已取消。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            return ProbeAttempt.Failed(ex.Message);
        }
        finally
        {
            // 探查结果 JSON 是运行态中间文件，不是消费面：留在盘上只会让人去那里捞。
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
        }
    }

    private static string FormatPackageSummary(PackagePlan plan)
    {
        var head = plan.BlockingIssues.Count > 0
            ? "整体打包计划不可执行：" + string.Join("；", plan.BlockingIssues)
            : $"整体打包计划：机加件 {plan.Machined.Count} 种、外购件 {plan.Purchased.Count} 种，"
              + $"待导 STEP {plan.StepTargets.Count} 个、工程图 {plan.DrawingTargets.Count} 张";
        return plan.Warnings.Count > 0 ? head + "；警告：" + string.Join("；", plan.Warnings) : head;
    }

    private static string FormatRenameSummary(AssemblyRenamePlan plan)
    {
        if (plan.BlockingIssues.Count > 0)
            return "改名计划不可执行：" + string.Join("；", plan.BlockingIssues);

        var renamed = plan.Entries.Count(entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath));
        var prefixText = plan.DrawingPrefix.Length == 0 ? "（空前缀＝删图号）" : plan.DrawingPrefix;
        var head = $"改名计划：前缀 {prefixText}，编号 {plan.Entries.Count} 个文件，"
                   + $"其中 {renamed} 个需要改名，未编号 {plan.Unnumbered.Count} 个";
        return plan.Warnings.Count > 0 ? head + "；警告：" + string.Join("；", plan.Warnings) : head;
    }

    /// <summary>与 <c>minerva.ui.options</c> 同一套真值判定，不在两处各写一份。</summary>
    private static bool IsTrue(string? value)
    {
        var text = value?.Trim();
        return text is not null
               && (text.Equals("开启", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("开", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("1", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct ProbeAttempt(AssemblyProbeResult? Result, CommandResult? Failure)
    {
        public static ProbeAttempt Succeeded(AssemblyProbeResult result) => new(result, null);

        public static ProbeAttempt Failed(string message) => new(null, CommandResult.Fail(message));
    }
}

/// <summary>只读装配探查。抽出来是为了让计划命令的测试不必装 SolidWorks、不必启动 Worker。</summary>
internal interface IAssemblyProbe
{
    /// <summary>Worker 在不在、SolidWorks COM 注册没有。不满足时抛，由调用方转成失败结果。</summary>
    void ValidateEnvironment();

    Task<AssemblyProbeResult> ProbeAsync(
        AssemblyProbeRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken);
}

internal sealed class WorkerAssemblyProbe(WorkerClient client) : IAssemblyProbe
{
    public void ValidateEnvironment()
        => PreflightValidator.ValidateEnvironment(client.WorkerPath, ConversionSourceFormat.SolidWorks);

    public Task<AssemblyProbeResult> ProbeAsync(
        AssemblyProbeRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        => client.ProbeAssemblyAsync(request, progress, cancellationToken);
}
