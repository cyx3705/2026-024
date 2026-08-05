using System.Diagnostics;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class SolidWorksAssemblyBuilder
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int SaveAsCurrentVersion = 0;
    private const int SaveAsSilent = 1;

    public static AssemblyOutcome Build(
        string outputPath,
        IReadOnlyList<AssemblyOccurrence> occurrences,
        IReadOnlyDictionary<string, ConversionJob> successfulJobs,
        bool overwrite,
        int partConverted,
        int partFailed,
        WorkerReporter reporter,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? failedPartPaths = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var leaves = occurrences.Where(item => !item.IsSubAssembly && !item.IsSuppressed).ToArray();
        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        object? assemblyModel = null;
        object? assemblyDocument = null;
        object? extension = null;
        object? mathUtility = null;
        var components = new List<object>();
        var openedParts = new List<object>();
        string? temporaryPath = null;
        var fixedCount = 0;
        var maxOriginDeviation = 0d;
        var maxRotationDeviation = 0d;
        var assemblyClosed = false;

        try
        {
            reporter.Report(
                null,
                ConversionStage.AssemblyBuild,
                "正在创建 SolidWorks 装配体。",
                artifact: ConversionArtifactKind.SolidWorksAssembly);
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.ComNotRegistered, "未检测到 SolidWorks COM 注册。");
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.AppLaunchFailed, "SolidWorks COM 返回空实例。");
            dynamic application = applicationObject;
            interop = SolidWorksInteropBridge.Create(applicationObject, applicationType);
            var handle = TryGetHandle(interop);
            ownership.Resolve(handle);
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

            string configuredTemplate;
            try
            {
                configuredTemplate = interop.GetAssemblyTemplate();
            }
            catch (Exception ex)
            {
                configuredTemplate = string.Empty;
                reporter.Report(
                    null,
                    ConversionStage.AssemblyBuild,
                    "读取 SolidWorks 默认装配模板失败，正在查找官方物理模板：" + ex.Message,
                    errorClass: ConversionErrorClass.AssemblyTemplateMissing);
            }

            var templateCandidates = SolidWorksAssemblyTemplateResolver.FindCandidates(
                configuredTemplate,
                interop.InstallDirectory);
            string? selectedTemplate = null;
            foreach (var candidate in templateCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                assemblyModel = interop.NewAssembly(candidate);
                if (assemblyModel is null)
                    continue;
                selectedTemplate = candidate;
                break;
            }
            if (assemblyModel is null || selectedTemplate is null)
            {
                var configured = string.IsNullOrWhiteSpace(configuredTemplate)
                    ? "<未配置>"
                    : configuredTemplate;
                throw new ClassifiedConversionException(
                    ConversionErrorClass.AssemblyTemplateMissing,
                    $"SolidWorks 无法创建装配文档。当前默认模板={configured}；找到的物理模板={templateCandidates.Count} 个。");
            }
            if (!string.Equals(
                    Path.GetFullPath(selectedTemplate),
                    TryGetPhysicalFullPath(configuredTemplate),
                    StringComparison.OrdinalIgnoreCase))
            {
                reporter.Report(
                    null,
                    ConversionStage.AssemblyBuild,
                    $"默认装配模板不可用，已回退到官方物理模板：{selectedTemplate}");
            }
            assemblyDocument = assemblyModel;
            var title = interop.GetTitle(assemblyModel);

            foreach (var job in successfulJobs.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = interop.OpenPart(job.SolidWorksPath, out var errors, out var warnings);
                if (part is null || errors != 0)
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentInsertFailed,
                        $"组件零件打开失败：{job.SolidWorksPath}，errors={errors}, warnings={warnings}");
                openedParts.Add(part);
            }
            _ = interop.ActivateDocument(title);
            mathUtility = interop.GetMathUtility();

            foreach (var occurrence in leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!successfulJobs.TryGetValue(Path.GetFullPath(occurrence.SourcePath), out var job))
                    continue;
                var component = interop.AddComponent(assemblyDocument, job.SolidWorksPath)
                    ?? throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentInsertFailed,
                        $"AddComponent5 返回 null：{job.SolidWorksPath}");
                components.Add(component);
                var expected = ToSolidWorksTransform(occurrence.WorldTransform);
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
                        $"SolidWorks 返回无效组件变换：{occurrence.OccurrenceId}");
                maxOriginDeviation = Math.Max(maxOriginDeviation, MaxTranslationDeviation(expected, actual));
                maxRotationDeviation = Math.Max(maxRotationDeviation, MaxRotationDeviation(expected, actual));
                if (maxRotationDeviation >= 1e-9 || maxOriginDeviation >= 1e-6)
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.ComponentTransformFailed,
                        $"组件变换校验超差：{occurrence.OccurrenceId}");
            }

            interop.ClearSelection(assemblyModel);
            foreach (var component in components)
            {
                if (!interop.SelectComponent(component, append: true))
                    throw new ClassifiedConversionException(ConversionErrorClass.ComponentTransformFailed, "组件选择失败，无法固定。");
            }
            interop.FixSelectedComponents(assemblyDocument);
            fixedCount = components.Count(interop.IsComponentFixed);
            if (fixedCount != components.Count)
                throw new ClassifiedConversionException(
                    ConversionErrorClass.ComponentTransformFailed,
                    $"组件固定不完整：{fixedCount}/{components.Count}");

            temporaryPath = TemporaryOutput.For(outputPath);
            extension = interop.GetExtension(assemblyModel);
            if (!interop.SaveAs3(extension, temporaryPath, SaveAsCurrentVersion, SaveAsSilent, out var saveErrors, out var saveWarnings)
                || saveErrors != 0)
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.AssemblySaveFailed,
                    $"SLDASM 保存失败：errors={saveErrors}, warnings={saveWarnings}");
            }
            // SolidWorks keeps the newly saved SLDASM locked while the document is open.
            // Close it before FileProbe so the stability check observes the real on-disk file
            // instead of waiting for the full timeout on an exclusive CAD lock.
            ComRelease.Final(extension);
            extension = null;
            var savedTitle = interop.GetTitle(assemblyModel);
            interop.CloseDocument(savedTitle);
            assemblyClosed = true;
            _ = FileProbe.WaitForStableNonEmptyFile(temporaryPath, cancellationToken);
            if (!overwrite && File.Exists(outputPath))
                throw new IOException($"输出已经存在：{outputPath}");
            TemporaryOutput.Commit(temporaryPath, outputPath);
            temporaryPath = null;
            return new AssemblyOutcome(
                leaves.Length,
                components.Count,
                fixedCount,
                partConverted,
                partFailed,
                occurrences.Count(item => item.IsSuppressed),
                maxOriginDeviation,
                stopwatch.ElapsedMilliseconds,
                BuildDiagnostic(maxRotationDeviation, failedPartPaths));
        }
        finally
        {
            TemporaryOutput.DeleteIfExists(temporaryPath);
            if (!assemblyClosed && interop is not null && assemblyModel is not null)
                TryRun(() => interop.CloseDocument(interop.GetTitle(assemblyModel)));
            foreach (var part in openedParts)
            {
                if (interop is not null && ownership.OwnsInstance)
                    TryRun(() => interop.CloseDocument(interop.GetTitle(part)));
                ComRelease.Final(part);
            }
            foreach (var component in components)
                ComRelease.Final(component);
            ComRelease.Final(mathUtility);
            ComRelease.Final(extension);
            ComRelease.Final(assemblyDocument);
            ComRelease.Final(assemblyModel);
            if (interop is not null && ownership.OwnsInstance)
                TryRun(interop.ExitApplication);
            interop?.Dispose();
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        }
    }

    internal static double[] ToSolidWorksTransform(IReadOnlyList<double> source)
    {
        if (source.Count != 16)
            throw new InvalidDataException("Solid Edge 世界矩阵必须包含 16 个元素。");
        return
        [
            source[0], source[1], source[2],
            source[4], source[5], source[6],
            source[8], source[9], source[10],
            source[12], source[13], source[14],
            1, 0, 0, 0,
        ];
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

    private static string TryGetPhysicalFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        return Path.GetFullPath(path);
    }

    private static string BuildDiagnostic(double maxRotationDeviation, IReadOnlyList<string>? failedPartPaths)
    {
        var diagnostic = $"V3.0 展平装配，所有组件固定，不含配合；最大旋转元素偏差 {maxRotationDeviation:G6}。";
        return failedPartPaths is { Count: > 0 }
            ? diagnostic + " 缺失零件：" + string.Join("；", failedPartPaths)
            : diagnostic;
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
