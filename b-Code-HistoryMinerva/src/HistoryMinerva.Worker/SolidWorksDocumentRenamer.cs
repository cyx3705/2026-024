using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// 按图号清单就地改名 SolidWorks 文件，并用 <c>ReplaceReferencedDocument</c>
/// 更新父装配引用。源文件改的是名字，不换目录。
///
/// V4.7 起同一次请求还负责写零件「自定义」属性：改名与引用更新全部收工后，
/// 才逐个打开零件写槽再保存。顺序不能反——<c>ReplaceReferencedDocument</c> 要求
/// 相关文档处于关闭状态，先开着零件写属性会让父装配的引用改不动。
/// </summary>
internal static class SolidWorksDocumentRenamer
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";

    /// <summary>
    /// 一个零件的属性写入作业：写去哪个条目、写哪些槽，以及要不要顺手换材质。
    ///
    /// 材质单独拎出来是因为它根本不是一个属性槽——「材料」那一槽写的是链接记号，
    /// 材质本身得走 <c>SetMaterialPropertyName2</c>。两者缺一，属性标签上都还是「未指定」。
    /// </summary>
    private readonly record struct PropertyWriteJob(
        RenameEntry Entry,
        IReadOnlyList<KeyValuePair<string, string>> Pairs,
        string MaterialDatabase,
        string MaterialName);

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
        var propertyTargets = ResolvePropertyTargets(request);
        if (pending.Length == 0 && propertyTargets.Count == 0)
        {
            reporter.Report(
                null,
                ConversionStage.Completed,
                "图号已与规则一致，也没有需要写入的零件属性。");
            return 0;
        }

        reporter.Report(
            null,
            ConversionStage.PropertyPrep,
            $"开始写入：{DescribeWorkload(pending.Length, propertyTargets.Count)}。");

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

            failed += WriteProperties(interop, propertyTargets, currentPaths, reporter, cancellationToken);

            reporter.Report(
                null,
                failed == 0 ? ConversionStage.Completed : ConversionStage.Failed,
                failed == 0
                    ? $"写入完成：{DescribeWorkload(pending.Length, propertyTargets.Count)}。"
                    : $"写入结束，{failed} 个文件失败。",
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
    /// 哪些条目要写属性。只认识别出的零件：装配体、未编号的内部件和子文件夹外购件一律不碰。
    ///
    /// 删图号不在这里分叉：它的载荷与一次普通写入完全一样，只是「图号」槽的值是空串
    /// （见 <see cref="PartPropertyWrite.Pairs"/>）。空串写进去是把槽清空，不是删掉整槽——
    /// 删槽会让属性标签上少一行，用户看到的是「属性没了」而不是「属性空了」。
    /// </summary>
    private static IReadOnlyList<PropertyWriteJob> ResolvePropertyTargets(AssemblyRenameRequest request)
    {
        if (!request.WriteProperties)
            return [];

        var targets = request.Entries
            .Where(entry => AssemblyRenamePlan.IsWritablePart(entry) && entry.Properties is { IsEmpty: false })
            .OrderBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 只带材质名不带材料库是上游拼请求时漏了字段。SetMaterialPropertyName2 对空库名
        // 不报错也不生效，放过去就又是一次「跑成功了但材料还是未指定」。整批停在这里。
        if (Array.Find(targets, entry => entry.Properties!.MaterialIsIncomplete) is { } broken)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.RenameFailed,
                $"材料「{broken.Properties!.Material}」没有随附材料库，无法应用到零件："
                + Path.GetFileName(broken.SourcePath));
        }

        return targets
            .Select(entry => new PropertyWriteJob(
                entry,
                entry.Properties!.Pairs().ToArray(),
                entry.Properties.HasMaterial ? entry.Properties.MaterialDatabase : string.Empty,
                entry.Properties.HasMaterial ? entry.Properties.Material : string.Empty))
            .ToArray();
    }

    /// <summary>门禁与离线 Smoke 用的只读视图：这次请求会去动哪些零件的哪些槽。</summary>
    internal static IReadOnlyList<(string SourcePath, IReadOnlyList<KeyValuePair<string, string>> Pairs)>
        DescribePropertyTargets(AssemblyRenameRequest request)
        => ResolvePropertyTargets(request)
            .Select(job => (job.Entry.SourcePath, job.Pairs))
            .ToArray();

    private static string DescribeWorkload(int renameCount, int propertyCount)
    {
        var parts = new List<string>(2);
        if (renameCount > 0)
            parts.Add($"{renameCount} 个文件改名");
        if (propertyCount > 0)
            parts.Add($"{propertyCount} 个零件写属性");
        return parts.Count == 0 ? "没有需要处理的文件" : string.Join("、", parts);
    }

    /// <summary>
    /// 逐个打开零件写「自定义」属性再保存。
    ///
    /// 走的是改名后的**当前路径**：改名成功的零件此刻已经是新文件名，拿原路径去开
    /// 只会得到「零件不存在」。改名失败的零件仍在原名上，属性照写——名字没改成不是
    /// 不写属性的理由，那两件事在用户眼里是分开的。
    /// </summary>
    private static int WriteProperties(
        SolidWorksInteropBridge interop,
        IReadOnlyList<PropertyWriteJob> targets,
        IReadOnlyDictionary<string, string> currentPaths,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var (entry, pairs, materialDatabase, materialName) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(entry.SourcePath);
            var path = currentPaths.TryGetValue(source, out var mapped) ? mapped : source;
            try
            {
                var written = WritePropertiesOne(
                    interop, path, pairs, materialDatabase, materialName, out var created);
                reporter.Report(
                    entry.Id,
                    ConversionStage.Completed,
                    created.Count == 0
                        ? $"已写入 {written} 项属性：{Path.GetFileName(path)}"
                        : $"已写入 {written} 项属性：{Path.GetFileName(path)}；"
                          + $"但模板里原本没有 {string.Join("、", created)}，本次是新建的槽——"
                          + "请核对属性标签模板里的属性名是否与此一致。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                reporter.Report(
                    entry.Id,
                    ConversionStage.Failed,
                    $"写属性失败：{ex.Message}",
                    true,
                    ex.HResult,
                    errorClass: ex is ClassifiedConversionException classified
                        ? classified.ErrorClass
                        : ConversionErrorClass.RenameFailed);
            }
        }

        return failed;
    }

    /// <param name="createdSlots">
    /// 模板里原本没有、本次由 <c>Add3</c> 新建的槽名。空表示七个名字全都命中了模板。
    /// 非空几乎总是意味着 <see cref="PartPropertyNames"/> 里的名字与属性标签模板对不上。
    /// </param>
    private static int WritePropertiesOne(
        SolidWorksInteropBridge interop,
        string path,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        string materialDatabase,
        string materialName,
        out IReadOnlyList<string> createdSlots)
    {
        if (!File.Exists(path))
            throw new ClassifiedConversionException(ConversionErrorClass.InputMissing, $"零件不存在：{path}");

        object? model = null;
        try
        {
            model = interop.OpenPart(path, out var errors, out _)
                ?? throw new ClassifiedConversionException(
                    ConversionErrorClass.InputLocked,
                    $"SolidWorks 未能打开零件（错误码 {errors}）：{Path.GetFileName(path)}");
            var extension = interop.GetExtension(model);
            // 属性标签模板的控件全是 ApplyTo="Config"，所以写的是**活动配置**那一批槽。
            // 写成文档级不会报任何错，只是用户打开属性标签页看到的还是空的。
            var configuration = interop.GetActiveConfigurationName(model);
            var manager = interop.GetCustomPropertyManager(extension, configuration);
            var existing = interop.GetCustomPropertyNames(manager);
            var created = new List<string>();
            var written = 0;

            // 材质先应用，再写属性槽。反过来也能跑，但换材质会触发一次重建，
            // 让刚写好的槽多经历一次模型变更——没有必要冒这个险。
            if (materialName.Length != 0)
            {
                interop.SetMaterialPropertyName(model, configuration, materialDatabase, materialName);
                var appliedMaterial = interop.GetMaterialPropertyName(model, configuration);
                if (!string.Equals(appliedMaterial, materialName, StringComparison.Ordinal))
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.RenameFailed,
                        $"材质应用后读回是“{appliedMaterial}”，与期望的“{materialName}”不符"
                        + $"（材料库：{materialDatabase}）。");
                }
            }

            foreach (var pair in pairs)
            {
                if (!existing.Contains(pair.Key))
                    created.Add(pair.Key);
                var status = interop.AddCustomProperty(manager, pair.Key, pair.Value);
                if (status != 0)
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.RenameFailed,
                        $"SolidWorks 拒绝写入属性「{pair.Key}」（返回 {status}）。");
                }

                // Add3 返回 0 只说明调用被接受。链接到方程或被模板设成只读的槽会静默不动，
                // 那种「成功了但值没变」在图框上与压根没跑过一模一样，必须当场读回来对一次。
                var readBack = interop.GetCustomProperty(manager, pair.Key);
                if (!string.Equals(readBack, pair.Value, StringComparison.Ordinal))
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.RenameFailed,
                        $"属性「{pair.Key}」写入后读回是“{readBack}”，与期望的“{pair.Value}”不符。");
                }

                written++;
            }

            createdSlots = created;
            if (!interop.Save3(model, out var saveErrors, out _))
            {
                throw new ClassifiedConversionException(
                    ConversionErrorClass.OutputExists,
                    $"属性已写入内存但保存失败（错误码 {saveErrors}）：{Path.GetFileName(path)}");
            }

            return written;
        }
        finally
        {
            if (model is not null)
            {
                var closing = model;
                TryRun(() => interop.CloseDocument(interop.GetTitle(closing)));
                ComRelease.One(model);
            }
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
