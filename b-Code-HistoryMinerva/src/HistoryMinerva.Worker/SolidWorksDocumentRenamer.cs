using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// 按图号清单就地改名 SolidWorks 文件，并用 <c>ReplaceReferencedDocument</c>
/// 更新父装配引用。源文件改的是名字，不换目录——唯一的例外是 V4.15 的
/// 「同级外购件挪进 <c>外购件\</c>」，引用照样由 <c>ReplaceReferencedDocument</c> 改到新路径上。
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
        // 「外购件」目录这一轮可能才第一次用到。校验已经确认过它只会是总装目录下的那一个。
        foreach (var entry in pending)
        {
            if (ConversionPathLayout.IsPurchasedRelocation(entry.SourcePath, entry.TargetPath, request.SourceAssemblyPath))
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(entry.TargetPath))!);
        }
        if (pending.Length == 0)
        {
            reporter.Report(
                null,
                ConversionStage.Completed,
                "图号已与规则一致，无需改名。");
            return 0;
        }

        reporter.Report(
            null,
            ConversionStage.PropertyPrep,
            $"开始改名：{pending.Length} 个文件。");

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
            var stuck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staged = StageSwappedSources(interop, pending, currentPaths, stuck, reporter, cancellationToken);
            failed += stuck.Count;
            foreach (var entry in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stuck.Contains(entry.Id))
                    continue;
                try
                {
                    try
                    {
                        RenameOne(interop, entry, currentPaths);
                    }
                    catch when (staged.Contains(entry.Id))
                    {
                        RestoreStaged(interop, entry, currentPaths);
                        throw;
                    }
                    reporter.Report(
                        entry.Id,
                        ConversionStage.Completed,
                        ConversionPathLayout.IsPurchasedRelocation(
                            entry.SourcePath, entry.TargetPath, request.SourceAssemblyPath)
                            ? $"已移入 {ConversionPathLayout.PurchasedPartsDirectoryName}\\{Path.GetFileName(entry.TargetPath)}"
                            : $"已改名为 {Path.GetFileName(entry.TargetPath)}");
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
                    ? $"改名完成：{pending.Length} 个文件。"
                    : $"改名结束，{failed} 个文件失败。",
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

    /// <summary>
    /// V4.15.1：重排号时号码会在同名件之间互换（-19 安装板 → -17 安装板，原来的 -17 安装板 → -16），
    /// 甚至成环。逐个直接改，第一个就撞上「目标文件已存在」。所以先把「源文件名正是别人目标」的那些
    /// 挪到同目录的临时名（引用一并改过去），腾出名字，第二遍再统一落到目标名。
    ///
    /// 挪临时名失败的条目记进 <paramref name="stuck"/>，第二遍跳过它；占着它名字的那条会照常报
    /// 「目标文件已存在」，不会悄悄盖掉。返回成功挪到临时名的条目 id。
    /// </summary>
    private static HashSet<string> StageSwappedSources(
        SolidWorksInteropBridge interop,
        IReadOnlyList<RenameEntry> pending,
        Dictionary<string, string> currentPaths,
        HashSet<string> stuck,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = pending
            .Select(entry => Path.GetFullPath(entry.TargetPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var swapping = pending
            .Where(entry => wanted.Contains(Path.GetFullPath(entry.SourcePath)))
            .ToArray();
        if (swapping.Length == 0)
            return staged;

        reporter.Report(
            null,
            ConversionStage.PropertyPrep,
            $"{swapping.Length} 个文件的号码与别的文件互换，先挪到临时名腾出名字。");
        foreach (var entry in swapping)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MoveWithReferences(interop, entry, currentPaths, TemporaryPath(entry.SourcePath));
                staged.Add(entry.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stuck.Add(entry.Id);
                reporter.Report(
                    entry.Id,
                    ConversionStage.Failed,
                    $"号码互换前挪临时名失败：{ex.Message}",
                    true,
                    ex.HResult,
                    errorClass: ex is ClassifiedConversionException classified
                        ? classified.ErrorClass
                        : ConversionErrorClass.RenameFailed);
            }
        }

        return staged;
    }

    /// <summary>同目录、同扩展名的临时名：SolidWorks 认扩展名，换目录又会动到引用的搜索路径。</summary>
    private static string TemporaryPath(string source)
    {
        var full = Path.GetFullPath(source);
        var directory = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        while (true)
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var candidate = Path.Combine(directory, $"{stem}~minerva-{tag}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// 落位失败的互换件还停在临时名上：原名空着就挪回原名，免得文件夹里留一个临时名。
    /// 原名已被别人占了（对方落位成功）就只能留在临时名，并在报错里写明它现在叫什么。
    /// </summary>
    private static void RestoreStaged(
        SolidWorksInteropBridge interop,
        RenameEntry entry,
        Dictionary<string, string> currentPaths)
    {
        var source = Path.GetFullPath(entry.SourcePath);
        var current = currentPaths[source];
        if (AssemblyRenamePlan.SamePath(current, source))
            return;
        try
        {
            MoveWithReferences(interop, entry, currentPaths, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.RenameFailed,
                $"落位失败、原名也回不去，文件现在叫 {Path.GetFileName(current)}：{ex.Message}");
        }
    }

    private static void RenameOne(
        SolidWorksInteropBridge interop,
        RenameEntry entry,
        Dictionary<string, string> currentPaths)
        => MoveWithReferences(interop, entry, currentPaths, entry.TargetPath);

    /// <summary>
    /// 把条目的文件从它「现在」的位置（可能是临时名）挪到 <paramref name="destination"/>，
    /// 并把每个父装配里的引用从现在的位置改过去；任何一步失败就把文件挪回原处。
    /// </summary>
    private static void MoveWithReferences(
        SolidWorksInteropBridge interop,
        RenameEntry entry,
        Dictionary<string, string> currentPaths,
        string destination)
    {
        var key = Path.GetFullPath(entry.SourcePath);
        var source = currentPaths.TryGetValue(key, out var current) ? current : key;
        var target = Path.GetFullPath(destination);
        if (!File.Exists(source))
            throw new ClassifiedConversionException(ConversionErrorClass.InputMissing, $"源文件不存在：{source}");
        if (File.Exists(target))
            throw new ClassifiedConversionException(ConversionErrorClass.OutputExists, $"目标文件已存在：{target}");

        File.Move(source, target);
        currentPaths[key] = target;
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
            currentPaths[key] = source;
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
