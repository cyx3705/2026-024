using System.Diagnostics;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V3.3：按拓扑序自底向上生成嵌套 <c>.SLDASM</c>。
///
/// 遵守每层同构原则（20 号文档 §3.1.1）：<see cref="BuildNode"/> 只看一个节点的直接子项，
/// 从不去查它的父级或祖先。顶层只是"没有父级的那一层"，走的是同一个函数。
/// 因此三层、四层、N 层不需要任何分支——递归已经在拓扑序里展开完了。
/// </summary>
internal static class SolidWorksNestedAssemblyBuilder
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int SaveAsCurrentVersion = 0;

    /// <summary>swFileLoadError_e.swFileWithSameTitleAlreadyOpen。</summary>
    private const int SwFileWithSameTitleAlreadyOpen = 65536;
    private const int SaveAsSilent = 1;

    public static AssemblyOutcome Build(
        IReadOnlyList<AssemblyNode> nodes,
        IReadOnlyDictionary<string, ConversionJob> successfulJobs,
        IReadOnlyDictionary<string, IReadOnlyList<AssemblyRelation>> relationsByAssembly,
        int partConverted,
        int partFailed,
        int skippedSuppressed,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? failedPartPaths = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;

        var metrics = new NestedAssemblyBuildMetrics(skippedSuppressed);
        var subAssemblyBuilt = 0;
        var maxOriginDeviation = 0d;
        var maxRotationDeviation = 0d;
        var reused = new List<string>();
        var skippedChildren = new List<string>();
        var mateOutcomes = new List<MateOutcome>();

        try
        {
            reporter.Report(null, ConversionStage.AssemblyBuild,
                $"正在按层级生成 {nodes.Count} 个 SolidWorks 装配体。");
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.ComNotRegistered, "未检测到 SolidWorks COM 注册。");
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.AppLaunchFailed, "SolidWorks COM 返回空实例。");
            dynamic application = applicationObject;
            interop = SolidWorksInteropBridge.Create(applicationObject, applicationType);
            ownership.Resolve(TryGetHandle(interop));
            if (ownership.OwnsInstance)
            {
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
            }
            else
            {
                reporter.Report(null, ConversionStage.AssemblyBuild,
                    "正在使用你已打开的 SolidWorks 会话，本次转换不会关闭它。",
                    errorClass: ConversionErrorClass.CadProcessOwnershipUnknown);
            }

            var template = ResolveTemplate(interop, reporter, cancellationToken);
            var assemblyOutputs = nodes.ToDictionary(
                node => Path.GetFullPath(node.SourceAssemblyPath),
                node => node.OutputPath,
                StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                metrics.AddPlannedNode(node);
                var nodeRelations = relationsByAssembly.GetValueOrDefault(Path.GetFullPath(node.SourceAssemblyPath));

                if (AssemblyNodeReusePlanner.CanReuse(node, nodeRelations is { Count: > 0 }))
                {
                    reused.Add(node.OutputPath);
                    reporter.Report(null, ConversionStage.Skipped,
                        $"复用已有装配产物，不再重复生成：{node.OutputPath}",
                        artifact: ConversionArtifactKind.SolidWorksAssembly,
                        reuseKind: ReuseKind.ExistingSolidWorksAssembly);
                    metrics.AddReusedNode(node);
                    if (!node.IsRoot)
                        subAssemblyBuilt++;
                    continue;
                }

                var result = BuildNode(
                    interop,
                    template,
                    node,
                    successfulJobs,
                    assemblyOutputs,
                    nodeRelations,
                    reporter,
                    cancellationToken,
                    skippedChildren);
                metrics.AddBuiltNode(result.Inserted, result.Fixed);
                maxOriginDeviation = Math.Max(maxOriginDeviation, result.MaxOriginDeviation);
                maxRotationDeviation = Math.Max(maxRotationDeviation, result.MaxRotationDeviation);
                if (result.Mate is not null)
                    mateOutcomes.Add(result.Mate);
                if (!node.IsRoot)
                    subAssemblyBuilt++;
            }

            return new AssemblyOutcome(
                metrics.ComponentTotal,
                metrics.ComponentInserted,
                metrics.ComponentFixed,
                partConverted,
                partFailed,
                metrics.SkippedSuppressed,
                maxOriginDeviation,
                stopwatch.ElapsedMilliseconds,
                BuildDiagnostic(nodes, maxRotationDeviation, reused, skippedChildren, failedPartPaths),
                SubAssemblyTotal: nodes.Count(node => !node.IsRoot),
                SubAssemblyBuilt: subAssemblyBuilt,
                MaxDepth: nodes.Count == 0 ? 1 : nodes.Max(node => node.Depth),
                Mate: Merge(mateOutcomes),
                ReusedAssemblyCount: metrics.ReusedAssemblyCount,
                ReusedAssemblyPlannedComponentCount: metrics.ReusedAssemblyPlannedComponentCount);
        }
        finally
        {
            if (interop is not null && ownership.OwnsInstance)
                TryRun(interop.ExitApplication);
            interop?.Dispose();
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>把各层的配合结果并成一份。逐项相加即可——每条关系只属于一层。</summary>
    private static MateOutcome? Merge(IReadOnlyList<MateOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return null;
        return new MateOutcome(
            outcomes.Sum(item => item.RelationTotal),
            outcomes.Sum(item => item.MateRebuilt),
            outcomes.Sum(item => item.SkippedSuppressed),
            outcomes.Sum(item => item.SkippedUnsupported),
            outcomes.Sum(item => item.FailedUnmatched),
            outcomes.Sum(item => item.FailedAmbiguous),
            outcomes.Sum(item => item.FailedRejected),
            outcomes.Sum(item => item.ComponentsLeftFixed),
            outcomes.Max(item => item.MaxDriftMeters),
            outcomes.SelectMany(item => item.Diagnostics).ToArray(),
            outcomes.Sum(item => item.GroundApplied));
    }

    private readonly record struct NodeResult(
        int Inserted,
        int Fixed,
        double MaxOriginDeviation,
        double MaxRotationDeviation,
        MateOutcome? Mate = null);

    /// <summary>
    /// 生成一个装配文件。只读 <paramref name="node"/> 的直接子项——需要祖先信息才能算出的结果，
    /// 一定是把世界矩阵当成了局部矩阵。
    /// </summary>
    private static NodeResult BuildNode(
        SolidWorksInteropBridge interop,
        string template,
        AssemblyNode node,
        IReadOnlyDictionary<string, ConversionJob> successfulJobs,
        IReadOnlyDictionary<string, string> assemblyOutputs,
        IReadOnlyList<AssemblyRelation>? relations,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        List<string> skippedChildren)
    {
        MateOutcome? mateOutcome = null;
        reporter.Report(null, ConversionStage.AssemblyBuild,
            $"正在生成第 {node.Depth} 层装配：{Path.GetFileName(node.OutputPath)}（{node.Children.Count} 个组件）",
            artifact: ConversionArtifactKind.SolidWorksAssembly);

        object? assemblyModel = null;
        object? extension = null;
        object? mathUtility = null;
        var components = new List<object>();
        var componentsByName = new Dictionary<string, object>(StringComparer.Ordinal);
        var openedDocuments = new List<object>();
        string? temporaryPath = null;
        var closed = false;
        var maxOrigin = 0d;
        var maxRotation = 0d;

        try
        {
            assemblyModel = interop.NewAssembly(template)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.AssemblyTemplateMissing,
                    $"SolidWorks 无法用模板创建装配文档：{template}");
            var title = interop.GetTitle(assemblyModel);

            foreach (var child in node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var componentPath = ResolveComponentPath(child, successfulJobs, assemblyOutputs);
                if (componentPath is null)
                {
                    skippedChildren.Add($"{Path.GetFileName(node.OutputPath)} → {child.Name}");
                    continue;
                }

                var document = interop.OpenComponentDocument(componentPath, out var errors, out var warnings);
                if (document is null || errors != 0)
                {
                    // errors=65536 是 swFileWithSameTitleAlreadyOpen。裸错误码没法照做，
                    // 把占用标题的那个文档指出来——现场就是用户手工识别留下的未保存同名零件。
                    var conflict = (errors & SwFileWithSameTitleAlreadyOpen) != 0
                        ? interop.DescribeTitleConflict(componentPath)
                        : null;
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentInsertFailed,
                        conflict is null
                            ? $"组件文档打开失败：{componentPath}，errors={errors}, warnings={warnings}"
                            : $"组件文档打开失败：{componentPath}。{conflict}");
                }

                openedDocuments.Add(document);
            }

            _ = interop.ActivateDocument(title);
            mathUtility = interop.GetMathUtility();

            foreach (var child in node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var componentPath = ResolveComponentPath(child, successfulJobs, assemblyOutputs);
                if (componentPath is null)
                    continue;

                var component = interop.AddComponent(assemblyModel, componentPath)
                    ?? throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentInsertFailed,
                        $"AddComponent5 返回 null：{componentPath}");
                components.Add(component);
                componentsByName[child.Name] = component;

                // 局部矩阵直接用，不与任何父级矩阵复合——这一层的参考系就是这一层。
                var expected = SolidWorksAssemblyBuilder.ToSolidWorksTransform(child.LocalTransform);
                var transform = interop.CreateTransform(mathUtility, expected);
                try
                {
                    interop.SetComponentTransform(component, transform);
                }
                finally
                {
                    ComRelease.Final(transform);
                }

                var actual = interop.GetComponentTransform(component);
                if (actual.Length != 16 || actual.Any(value => !double.IsFinite(value)))
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentTransformFailed,
                        $"SolidWorks 返回无效组件变换：{node.OutputPath} → {child.Name}");
                maxOrigin = Math.Max(maxOrigin, MaxTranslationDeviation(expected, actual));
                maxRotation = Math.Max(maxRotation, MaxRotationDeviation(expected, actual));
                if (maxRotation >= 1e-9 || maxOrigin >= 1e-6)
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentTransformFailed,
                        $"组件变换校验超差：{node.OutputPath} → {child.Name}");
            }

            var fixedCount = 0;
            if (components.Count > 0)
            {
                interop.ClearSelection(assemblyModel);
                foreach (var component in components)
                {
                    if (!interop.SelectComponent(component, append: true))
                        throw new ClassifiedConversionException(
                            ConversionErrorClass.ComponentTransformFailed, "组件选择失败，无法固定。");
                }

                interop.FixSelectedComponents(assemblyModel);
                fixedCount = components.Count(interop.IsComponentFixed);
                if (fixedCount != components.Count)
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentTransformFailed,
                        $"组件固定不完整：{fixedCount}/{components.Count}（{Path.GetFileName(node.OutputPath)}）");
            }

            // V3.5：此刻组件已插入、变换已逐个回读校验，位置就是配合重建要用的基线。
            // 必须在保存之前做——保存后再改就得二次提交，中途失败会留下半成品。
            if (relations is { Count: > 0 })
            {
                mateOutcome = SolidWorksMateRebuilder.Rebuild(
                    interop, assemblyModel, mathUtility!, node, relations, componentsByName,
                    reporter, cancellationToken);
            }

            temporaryPath = TemporaryOutput.For(node.OutputPath);
            extension = interop.GetExtension(assemblyModel);
            if (!interop.SaveAs3(extension, temporaryPath, SaveAsCurrentVersion, SaveAsSilent, out var saveErrors, out var saveWarnings)
                || saveErrors != 0)
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.AssemblySaveFailed,
                    $"SLDASM 保存失败：{node.OutputPath}，errors={saveErrors}, warnings={saveWarnings}");
            }

            ComRelease.Final(extension);
            extension = null;
            interop.CloseDocument(interop.GetTitle(assemblyModel));
            closed = true;
            _ = FileProbe.WaitForStableNonEmptyFile(temporaryPath, cancellationToken);
            // V3.6.6：走到这里说明复用判定判了"重做"（产物过期、需要重建配合、或本来就没有）。
            // 既然已经决定重做，就必须允许覆盖——否则重做永远落不了盘。
            // 复用判定放行的节点根本不会进到 BuildNode，不存在"该复用却被覆盖"的情况。
            TemporaryOutput.Commit(temporaryPath, node.OutputPath, overwrite: true);
            temporaryPath = null;
            return new NodeResult(components.Count, fixedCount, maxOrigin, maxRotation, mateOutcome);
        }
        catch (Exception ex) when (ex is not ClassifiedConversionException and not OperationCanceledException)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.SubAssemblyBuildFailed,
                $"生成装配失败：{node.OutputPath}：{ex.Message}",
                ex);
        }
        finally
        {
            TemporaryOutput.DeleteIfExists(temporaryPath);
            if (!closed && assemblyModel is not null)
                TryRun(() => interop.CloseDocument(interop.GetTitle(assemblyModel)));
            // 打开的组件文档必须关掉。V3.0 的展平版本来就关（每批只有一个节点，问题不明显），
            // 嵌套版一个节点一批文档，不关就会在 SolidWorks 会话里越堆越多，
            // 后续节点 OpenDoc6 撞上 swFileWithSameTitleAlreadyOpen(65536)——
            // 组件插不进去，该组件连同它的配合一起消失。实测就是这么丢的。
            foreach (var document in openedDocuments)
            {
                TryRun(() => interop.CloseDocument(interop.GetTitle(document)));
                ComRelease.Final(document);
            }
            foreach (var component in components)
                ComRelease.Final(component);
            ComRelease.Final(mathUtility);
            ComRelease.Final(extension);
            ComRelease.Final(assemblyModel);
        }
    }

    /// <summary>
    /// 子项是零件就取它的 SLDPRT；是子装配就取**它那个节点的 OutputPath**，
    /// 拓扑序保证该文件此刻已经生成。绝不在这里重新拼路径——输出路径只能有一个来源。
    /// </summary>
    private static string? ResolveComponentPath(
        AssemblyChild child,
        IReadOnlyDictionary<string, ConversionJob> successfulJobs,
        IReadOnlyDictionary<string, string> assemblyOutputs)
    {
        var source = Path.GetFullPath(child.SourcePath);
        if (child.IsSubAssembly)
            return assemblyOutputs.GetValueOrDefault(source);
        return successfulJobs.TryGetValue(source, out var job) ? job.SolidWorksPath : null;
    }

    private static string ResolveTemplate(
        SolidWorksInteropBridge interop,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        string configured;
        try
        {
            configured = interop.GetAssemblyTemplate();
        }
        catch (Exception ex)
        {
            configured = string.Empty;
            reporter.Report(null, ConversionStage.AssemblyBuild,
                "读取 SolidWorks 默认装配模板失败，正在查找官方物理模板：" + ex.Message,
                errorClass: ConversionErrorClass.AssemblyTemplateMissing);
        }

        var candidates = SolidWorksAssemblyTemplateResolver.FindCandidates(configured, interop.InstallDirectory);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = interop.NewAssembly(candidate);
            if (probe is null)
                continue;
            TryRun(() => interop.CloseDocument(interop.GetTitle(probe)));
            ComRelease.Final(probe);
            return candidate;
        }

        throw new ClassifiedConversionException(
            ConversionErrorClass.AssemblyTemplateMissing,
            $"SolidWorks 无法创建装配文档。当前默认模板={(string.IsNullOrWhiteSpace(configured) ? "<未配置>" : configured)}；"
                + $"找到的物理模板={candidates.Count} 个。");
    }

    private static string BuildDiagnostic(
        IReadOnlyList<AssemblyNode> nodes,
        double maxRotationDeviation,
        IReadOnlyList<string> reused,
        IReadOnlyList<string> skippedChildren,
        IReadOnlyList<string>? failedPartPaths)
    {
        var diagnostic = $"嵌套装配：{nodes.Count} 个装配文件、最大 "
            + $"{(nodes.Count == 0 ? 1 : nodes.Max(node => node.Depth))} 层；"
            + $"最大旋转元素偏差 {maxRotationDeviation:G6}。";
        if (reused.Count > 0)
            diagnostic += " 复用产物：" + string.Join("；", reused);
        if (skippedChildren.Count > 0)
            diagnostic += " 跳过的组件：" + string.Join("；", skippedChildren);
        if (failedPartPaths is { Count: > 0 })
            diagnostic += " 缺失零件：" + string.Join("；", failedPartPaths);
        return diagnostic;
    }

    private static double MaxRotationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        var max = 0d;
        for (var index = 0; index < 9; index++)
            max = Math.Max(max, Math.Abs(expected[index] - actual[index]));
        return max;
    }

    private static double MaxTranslationDeviation(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        var max = 0d;
        for (var index = 9; index < 12; index++)
            max = Math.Max(max, Math.Abs(expected[index] - actual[index]));
        return max;
    }

    private static long TryGetHandle(SolidWorksInteropBridge interop)
    {
        try { return interop.GetWindowHandle(); } catch { return 0; }
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
