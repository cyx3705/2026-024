using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// 按图号清单就地改名 SolidWorks 文件，并用 <c>ReplaceReferencedDocument</c>
/// 更新父装配引用。源文件改的是名字，不换目录。
/// </summary>
internal static class SolidWorksDocumentRenamer
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";

    public static int Rename(
        AssemblyRenameRequest request,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var pending = request.Entries
            .Where(entry => !AssemblyRenamePlan.SamePath(entry.SourcePath, entry.TargetPath))
            .OrderByDescending(entry => entry.Depth)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var action = request.StripBySpace ? "按空格洗图号" : "按图号改名";
        if (pending.Length == 0)
        {
            reporter.Report(
                null,
                ConversionStage.Completed,
                request.StripBySpace ? "没有可按空格洗掉的图号。" : "图号已与规则一致，没有需要改名的文件。");
            return 0;
        }

        reporter.Report(
            null,
            ConversionStage.PropertyPrep,
            $"正在{action} {pending.Length} 个 SolidWorks 文件。");

        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        SolidWorksInteropBridge? interop = null;
        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.ComNotRegistered, "未检测到 SolidWorks COM 注册。");
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.AppLaunchFailed, "SolidWorks COM 返回空实例。");
            dynamic application = applicationObject;
            interop = SolidWorksInteropBridge.Create(applicationObject, applicationType);
            ownership.Resolve(TryGetHandle(interop));
            if (ownership.OwnsInstance)
            {
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
            }

            CloseRelatedDocuments(interop, request, cancellationToken);

            var currentPaths = request.Entries.ToDictionary(
                entry => Path.GetFullPath(entry.SourcePath),
                entry => Path.GetFullPath(entry.SourcePath),
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in request.Entries)
            {
                foreach (var parent in entry.ParentSourcePaths)
                    currentPaths.TryAdd(Path.GetFullPath(parent), Path.GetFullPath(parent));
            }

            var failed = 0;
            foreach (var entry in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    RenameOne(interop, entry, currentPaths);
                    reporter.Report(
                        entry.Id,
                        ConversionStage.Completed,
                        $"已改名为 {Path.GetFileName(entry.TargetPath)}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    reporter.Report(
                        entry.Id,
                        ConversionStage.Failed,
                        ex.Message,
                        true,
                        ex.HResult,
                        errorClass: ex is ClassifiedConversionException classified
                            ? classified.ErrorClass
                            : ConversionErrorClass.RenameFailed);
                }
            }

            reporter.Report(
                null,
                failed == 0 ? ConversionStage.Completed : ConversionStage.Failed,
                failed == 0
                    ? $"属性整备{action}完成：{pending.Length} 个文件。"
                    : $"属性整备{action}结束，{failed} 个文件失败。",
                isError: failed != 0,
                errorClass: failed == 0 ? ConversionErrorClass.None : ConversionErrorClass.RenameFailed);
            return failed == 0 ? 0 : 1;
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

    private static void RenameOne(
        SolidWorksInteropBridge interop,
        RenameEntry entry,
        Dictionary<string, string> currentPaths)
    {
        var source = Path.GetFullPath(entry.SourcePath);
        var target = Path.GetFullPath(entry.TargetPath);
        if (!File.Exists(source))
            throw new ClassifiedConversionException(ConversionErrorClass.InputMissing, $"源文件不存在：{source}");
        if (File.Exists(target))
            throw new ClassifiedConversionException(ConversionErrorClass.OutputExists, $"目标文件已存在：{target}");

        File.Move(source, target);
        currentPaths[source] = target;
        try
        {
            foreach (var parentSource in entry.ParentSourcePaths)
            {
                var parentCurrent = currentPaths.TryGetValue(Path.GetFullPath(parentSource), out var mapped)
                    ? mapped
                    : Path.GetFullPath(parentSource);
                if (!File.Exists(parentCurrent))
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.RenameFailed,
                        $"父装配不存在，无法更新引用：{parentCurrent}");
                }

                if (!interop.ReplaceReferencedDocument(parentCurrent, source, target))
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.RenameFailed,
                        $"SolidWorks 未能把 {Path.GetFileName(parentCurrent)} 中的引用改为 {Path.GetFileName(target)}。");
                }
            }
        }
        catch
        {
            if (File.Exists(target) && !File.Exists(source))
                File.Move(target, source);
            currentPaths[source] = source;
            throw;
        }
    }

    private static void CloseRelatedDocuments(
        SolidWorksInteropBridge interop,
        AssemblyRenameRequest request,
        CancellationToken cancellationToken)
    {
        var related = request.Entries
            .SelectMany(entry => entry.ParentSourcePaths.Append(entry.SourcePath).Append(entry.TargetPath))
            .Append(request.SourceAssemblyPath)
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in related)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = interop.FindOpenDocument(path);
            if (document is null)
                continue;
            if (interop.IsDocumentDirty(document))
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.InputLocked,
                    $"SolidWorks 里开着未保存的 {Path.GetFileName(path)}，请先保存或关闭后再改名。");
            }

            interop.CloseDocument(interop.GetTitle(document));
            ComRelease.One(document);
        }
    }

    private static long TryGetHandle(SolidWorksInteropBridge interop)
    {
        try
        {
            return interop.GetWindowHandle();
        }
        catch
        {
            return 0;
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }
}
