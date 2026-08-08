using System.Security.Cryptography;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class SolidEdgeAssemblyExplorer
{
    private const string ProgId = "SolidEdge.Application";
    private const string ProcessName = "Edge";

    public static AssemblyProbeResult Probe(string sourceAssemblyPath, CancellationToken cancellationToken)
    {
        var ownership = CadProcessOwnership.Capture(ProcessName);
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        bool? originalDisplayAlerts = null;
        var sourceHash = ComputeSha256(sourceAssemblyPath);
        try
        {
            var applicationType = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.ComNotRegistered, "未检测到 Solid Edge COM 注册。");
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new ClassifiedConversionException(ConversionErrorClass.AppLaunchFailed, "Solid Edge COM 返回空实例。");
            dynamic application = applicationObject;
            application.DoIdle();
            ownership.Resolve(Convert.ToInt64(application.hWnd));
            if (!ownership.OwnsInstance)
                throw new ClassifiedConversionException(
                    ConversionErrorClass.CadProcessOwnershipUnknown,
                    "无法证明 Solid Edge 实例由装配探查进程创建，已停止以保护用户会话。");

            originalDisplayAlerts = Convert.ToBoolean(application.DisplayAlerts);
            application.DisplayAlerts = false;
            application.Visible = false;
            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            documentObject = documents.Open(sourceAssemblyPath);
            dynamic document = documentObject;
            application.DoIdle();

            var occurrences = new List<AssemblyOccurrence>();
            var warnings = new List<string>();
            var modelingModes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rootChildren = new List<AssemblyChild>();
            dynamic top = document.Occurrences;
            try
            {
                for (var index = 1; index <= Convert.ToInt32(top.Count); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dynamic occurrence = top.Item(index);
                    try
                    {
                        // 顶层的参考系就是世界系，所以顶层的一级子项局部矩阵 == 世界矩阵，直接取。
                        rootChildren.Add(ReadChild(occurrence, Path.GetDirectoryName(sourceAssemblyPath)!));
                        ReadOccurrence(
                            occurrence,
                            null,
                            false,
                            Path.GetDirectoryName(sourceAssemblyPath)!,
                            occurrences,
                            warnings,
                            modelingModes,
                            cancellationToken);
                    }
                    finally
                    {
                        ComRelease.Final(occurrence);
                    }
                }
            }
            finally
            {
                ComRelease.Final(top);
            }

            // V3.5：顶层自己那一层的关系。必须在关闭文档之前读。
            var rootRelations = SolidEdgeRelationReader.Read(
                document, sourceAssemblyPath, warnings, cancellationToken);

            document.Close(false);
            application.DoIdle();
            ComRelease.Final(documentObject);
            documentObject = null;
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, ComputeSha256(sourceAssemblyPath)))
                throw new InvalidDataException("装配探查后源 .asm 文件内容发生变化。");

            var uniqueParts = occurrences
                .Where(item => !item.IsSubAssembly && !item.IsSuppressed && File.Exists(item.SourcePath))
                .Select(item => Path.GetFullPath(item.SourcePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            // V3.3 方案 A：把每个唯一子装配作为**独立顶层文档**打开，读到的矩阵天然就是
            // 该文档坐标系下的局部矩阵，不需要拿父级世界矩阵求逆换算。
            var documentReadings = new List<AssemblyDocumentReading>
            {
                new(Path.GetFullPath(sourceAssemblyPath), rootChildren, Array.Empty<string>(), rootRelations),
            };
            var subAssemblyPaths = occurrences
                .Where(item => item.IsSubAssembly && !item.IsSuppressed && File.Exists(item.SourcePath))
                .Select(item => Path.GetFullPath(item.SourcePath))
                .Where(path => ConversionPathLayout.HasExtension(path, ConversionPathLayout.SolidEdgeAssemblyExtension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            foreach (var path in subAssemblyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documentReadings.Add(ReadDocument(application, documents, path, warnings, cancellationToken));
            }

            return new AssemblyProbeResult(
                Path.GetFullPath(sourceAssemblyPath),
                occurrences,
                uniqueParts,
                occurrences.Count(item => item.IsSuppressed),
                occurrences.Count(item => item.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true),
                uniqueParts.Count(path => modelingModes.GetValueOrDefault(path) == 2),
                uniqueParts.Count(path => modelingModes.GetValueOrDefault(path) == 1),
                warnings,
                documentReadings);
        }
        catch (ClassifiedConversionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClassifiedConversionException(
                ComErrorClassifier.Classify(ex, ConversionErrorClass.AssemblyOpenFailed),
                ex.Message,
                ex);
        }
        finally
        {
            TryRun(() => ((dynamic?)documentObject)?.Close(false));
            ComRelease.Final(documentObject);
            ComRelease.Final(documentsObject);
            if (applicationObject is not null)
            {
                dynamic application = applicationObject;
                if (originalDisplayAlerts is bool alerts)
                    TryRun(() => application.DisplayAlerts = alerts);
                if (ownership.OwnsInstance)
                    TryRun(() => application.Quit());
            }
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// V3.3：把一个子装配 <c>.asm</c> 作为独立顶层文档打开，只读它的**一级** occurrence。
    ///
    /// 这里刻意不递归：每层只关心本层的直接子项，更深的层由它自己那一次读取负责。
    /// 需要祖先信息才能算出的结果，一定是把世界矩阵当成了局部矩阵。
    /// </summary>
    private static AssemblyDocumentReading ReadDocument(
        dynamic application,
        dynamic documents,
        string assemblyPath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var hash = ComputeSha256(assemblyPath);
        var directory = Path.GetDirectoryName(assemblyPath)!;
        var children = new List<AssemblyChild>();
        var local = new List<string>();
        var relations = new List<AssemblyRelation>();
        object? documentObject = null;
        try
        {
            documentObject = documents.Open(assemblyPath);
            dynamic document = documentObject;
            application.DoIdle();

            dynamic top = document.Occurrences;
            try
            {
                for (var index = 1; index <= Convert.ToInt32(top.Count); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dynamic occurrence = top.Item(index);
                    try
                    {
                        children.Add(ReadChild(occurrence, directory));
                    }
                    finally
                    {
                        ComRelease.Final(occurrence);
                    }
                }
            }
            finally
            {
                ComRelease.Final(top);
            }

            // V3.5：这一层自己的关系。该文档是作为独立顶层打开的，
            // 所以 GetGeometryN 的"世界系"就是它自己的坐标系，无需换算。
            relations.AddRange(SolidEdgeRelationReader.Read(document, assemblyPath, local, cancellationToken));

            document.Close(false);
            application.DoIdle();
            ComRelease.Final(documentObject);
            documentObject = null;
            if (!CryptographicOperations.FixedTimeEquals(hash, ComputeSha256(assemblyPath)))
                throw new InvalidDataException($"读取子装配后源文件内容发生变化：{assemblyPath}");
        }
        catch (ClassifiedConversionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClassifiedConversionException(
                ComErrorClassifier.Classify(ex, ConversionErrorClass.AssemblyOpenFailed),
                $"打开子装配失败：{assemblyPath}：{ex.Message}",
                ex);
        }
        finally
        {
            if (documentObject is not null)
            {
                TryRun(() => ((dynamic)documentObject).Close(false));
                ComRelease.Final(documentObject);
            }
        }

        if (children.Count == 0)
            warnings.Add($"子装配没有可读的一级实例：{assemblyPath}");
        return new AssemblyDocumentReading(assemblyPath, children, local, relations);
    }

    /// <summary>把一个一级 occurrence 读成 <see cref="AssemblyChild"/>。矩阵取值不做任何换算。</summary>
    private static AssemblyChild ReadChild(dynamic occurrence, string parentDirectory)
    {
        var name = Convert.ToString(occurrence.Name) ?? "Occurrence";
        var sourcePath = NormalizePath(Convert.ToString(occurrence.PartFileName) ?? string.Empty, parentDirectory);
        var isSubAssembly = TryGet(() => Convert.ToBoolean(occurrence.Subassembly), false);
        var hidden = !TryGet(() => Convert.ToBoolean(occurrence.Visible), true);
        var diagnostics = new List<string>();
        if (!File.Exists(sourcePath))
            diagnostics.Add("引用不存在");
        if (TryGet(() => Convert.ToBoolean(occurrence.IsPatternItem), false))
            diagnostics.Add("阵列成员");
        if (TryGet(() => Convert.ToBoolean(occurrence.IsAdjustablePart), false)
            || TryGet(() => Convert.ToBoolean(occurrence.Adjustable), false))
            diagnostics.Add("可调件");
        if (hidden)
            diagnostics.Add("隐藏件");

        return new AssemblyChild(
            name,
            sourcePath,
            isSubAssembly,
            hidden,
            ReadMatrix(occurrence),
            diagnostics.Count == 0 ? null : string.Join("；", diagnostics));
    }

    private static void ReadOccurrence(
        dynamic occurrence,
        string? parentId,
        bool isSubOccurrence,
        string sourceDirectory,
        List<AssemblyOccurrence> output,
        List<string> warnings,
        IDictionary<string, int> modelingModes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Convert.ToString(occurrence.Name) ?? "Occurrence";
        var id = parentId is null ? name : parentId + "/" + name;
        var isSubAssembly = TryGet(() => Convert.ToBoolean(occurrence.Subassembly), false);
        var rawPath = Convert.ToString(isSubOccurrence
            ? occurrence.SubOccurrenceFileName
            : occurrence.PartFileName) ?? string.Empty;
        var sourcePath = NormalizePath(rawPath, sourceDirectory);
        // Solid Edge 2020 的 Occurrence/SubOccurrence COM 合同没有可持久化的 Suppress
        // 属性。Activate 是会话加载状态，探针证明把它写为 false 后不会随 .asm 持久化，
        // 因此不能把它误当成抑制并静默丢件。活动配置未枚举出的 occurrence 会自然跳过；
        // 对显式清单/后续版本仍保留 IsSuppressed 合同和完整下游处理。
        var isSuppressed = false;
        var hidden = !TryGet(() => Convert.ToBoolean(occurrence.Visible), true);
        var diagnostics = new List<string>();
        if (!File.Exists(sourcePath))
            diagnostics.Add("引用不存在");
        if (TryGet(() => Convert.ToBoolean(occurrence.IsPatternItem), false))
            diagnostics.Add("阵列成员");
        if (TryGet(() => Convert.ToBoolean(occurrence.IsAdjustablePart), false)
            || TryGet(() => Convert.ToBoolean(occurrence.Adjustable), false))
            diagnostics.Add("可调件");
        if (hidden)
            diagnostics.Add("隐藏件");
        if (isSuppressed)
            diagnostics.Add("抑制/未激活");

        var matrix = ReadMatrix(occurrence);
        output.Add(new AssemblyOccurrence(
            id,
            parentId,
            sourcePath,
            isSubAssembly,
            isSuppressed,
            hidden,
            matrix,
            diagnostics.Count == 0 ? null : string.Join("；", diagnostics)));

        if (!File.Exists(sourcePath))
        {
            warnings.Add($"未解析引用：{sourcePath}");
            return;
        }
        if (isSuppressed)
        {
            warnings.Add($"跳过抑制/未激活实例：{id}");
            return;
        }
        if (!isSubAssembly)
        {
            if (!ConversionPathLayout.HasExtension(sourcePath, ConversionPathLayout.SolidEdgePartExtension))
            {
                warnings.Add($"跳过 V3.0 不支持的引用：{sourcePath}");
                return;
            }
            if (!modelingModes.ContainsKey(sourcePath))
            {
                object? partDocument = null;
                try
                {
                    partDocument = isSubOccurrence ? occurrence.SubOccurrenceDocument : occurrence.PartDocument;
                    modelingModes[sourcePath] = Convert.ToInt32(((dynamic)partDocument).ModelingMode);
                }
                catch
                {
                    modelingModes[sourcePath] = 0;
                    warnings.Add($"无法读取建模模式：{sourcePath}");
                }
                finally
                {
                    ComRelease.One(partDocument);
                }
            }
            return;
        }

        dynamic children = occurrence.SubOccurrences;
        try
        {
            for (var index = 1; index <= Convert.ToInt32(children.Count); index++)
            {
                dynamic child = children.Item(index);
                try
                {
                    // Solid Edge SubOccurrence.GetMatrix 已经返回顶层世界矩阵，禁止再次与父矩阵相乘。
                    ReadOccurrence(child, id, true, Path.GetDirectoryName(sourcePath)!, output, warnings, modelingModes, cancellationToken);
                }
                finally
                {
                    ComRelease.Final(child);
                }
            }
        }
        finally
        {
            ComRelease.Final(children);
        }
    }

    private static double[] ReadMatrix(dynamic occurrence)
    {
        Array values = new double[16];
        occurrence.GetMatrix(ref values);
        var matrix = values.Cast<object>().Select(Convert.ToDouble).ToArray();
        if (matrix.Length != 16 || matrix.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Solid Edge occurrence 返回无效矩阵。");
        return matrix;
    }

    private static string NormalizePath(string path, string parentDirectory)
        => Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(parentDirectory, path));

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
