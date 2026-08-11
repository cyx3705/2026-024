using System.Security.Cryptography;
using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// V4.3：把一个 SolidWorks 源 <c>.SLDASM</c> 读成与 Solid Edge 探查**完全同形**的
/// <see cref="AssemblyProbeResult"/>。同形是这一版的全部设计意图——只要读数形状一致，
/// 计划器、拓扑排序、嵌套装配生成、配合重建就一行都不用改。
///
/// 三条实测事实（2026-08-11，tools/SolidWorksAssemblyProbe 对 Module 样件）：
///
///   1. <c>GetComponents(true)</c> 的 <c>Transform2</c> 是**本文档坐标系**下的局部矩阵；
///      <c>GetComponents(false)</c> 的则一律相对当前顶层文档，即世界矩阵。
///      两者独立读出，正好喂给 <c>AssemblyTransformVerifier</c> 互相印证。
///   2. 后代组件的 <c>Name2</c> 天然是 "父-1/子-1"，与既有 OccurrenceId 约定逐字相同。
///   3. 配合挂在特征树顶层的 <c>MateGroup</c> 下，逐个子特征
///      <c>GetSpecificFeature2()</c> 即 <c>IMate2</c>；样件里 0 条，全部组件为固定件。
///
/// 只读：优先复用会话里已打开的文档（绝不替用户关掉），自己打开的一律
/// Silent + ReadOnly，并在关闭后比对 SHA256——"没有改动源文件"必须是可核验的事实。
/// </summary>
internal static class SolidWorksAssemblyExplorer
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";

    /// <summary>swComponentVisibilityState_e.swComponentVisible。</summary>
    private const int ComponentVisible = 1;

    /// <summary>swMateType_e 里本版翻译的三种。其余照实带类型号进报告，不猜。</summary>
    private const int MateTypeCoincident = 0;
    private const int MateTypeConcentric = 1;
    private const int MateTypeDistance = 5;

    /// <summary>MathTransform.ArrayData 的第 13 个元素是缩放。装配组件必须是 1。</summary>
    private const double ScaleTolerance = 1e-9;

    public static AssemblyProbeResult Probe(string sourceAssemblyPath, CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(sourceAssemblyPath);
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

            // SolidWorks 是单实例：用户开着它时一定附着过去。此时绝不改可见性、绝不退出，
            // 用户的未保存工作不受影响——与 SolidWorksImporter 同一条规矩。
            if (ownership.OwnsInstance)
            {
                TryRun(() => application.Visible = false);
                TryRun(() => application.UserControl = false);
            }

            var occurrences = new List<AssemblyOccurrence>();
            var warnings = new List<string>();
            var documents = new List<AssemblyDocumentReading>
            {
                ReadDocument(interop, rootPath, occurrences, warnings, cancellationToken),
            };

            var uniqueParts = occurrences
                .Where(item => !item.IsSubAssembly && !item.IsSuppressed && File.Exists(item.SourcePath))
                .Select(item => Path.GetFullPath(item.SourcePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            // 每个唯一子装配都作为**独立顶层文档**再打开一次，读到的矩阵天然就是它自己
            // 坐标系下的局部矩阵，不需要拿父级世界矩阵求逆换算（与 V3.3 方案 A 同源）。
            var subAssemblyPaths = occurrences
                .Where(item => item.IsSubAssembly && !item.IsSuppressed && File.Exists(item.SourcePath))
                .Select(item => Path.GetFullPath(item.SourcePath))
                .Where(path => ConversionPathLayout.HasExtension(
                    path, ConversionPathLayout.SolidWorksAssemblyExtension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            foreach (var path in subAssemblyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documents.Add(ReadDocument(interop, path, output: null, warnings, cancellationToken));
            }

            return new AssemblyProbeResult(
                rootPath,
                occurrences,
                uniqueParts,
                occurrences.Count(item => item.IsSuppressed),
                occurrences.Count(item => item.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true),
                // 顺序/同步建模是 Solid Edge 的概念，SolidWorks 源没有对应读数。
                OrderedPartCount: 0,
                SynchronousPartCount: 0,
                warnings,
                documents);
        }
        catch (ClassifiedConversionException)
        {
            throw;
        }
        catch (OperationCanceledException)
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
            if (interop is not null && ownership.OwnsInstance)
                TryRun(interop.ExitApplication);
            interop?.Dispose();
            ComRelease.Final(applicationObject);
            if (ownership.OwnsInstance)
                _ = ownership.EnsureOwnedExit(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// 读一个装配文档：本层的直接子项、本层的配合，以及（仅顶层）全部后代的世界矩阵。
    ///
    /// <paramref name="output"/> 为 null 表示这是子装配文档——它的后代已经在顶层那一次
    /// 展平里出现过了，再收一遍只会产生重复的 OccurrenceId。
    /// </summary>
    private static AssemblyDocumentReading ReadDocument(
        SolidWorksInteropBridge interop,
        string assemblyPath,
        List<AssemblyOccurrence>? output,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var hash = ComputeSha256(assemblyPath);
        var children = new List<AssemblyChild>();
        var local = new List<string>();
        var relations = new List<AssemblyRelation>();
        object? model = null;
        var openedHere = false;

        try
        {
            // 用户已经开着这个文档时直接复用，绝不替他关掉；自己打开的才由自己关。
            model = interop.FindOpenDocument(assemblyPath);
            if (model is null)
            {
                model = interop.OpenAssemblyReadOnly(assemblyPath, out var errors, out var warningCode);
                openedHere = true;
                if (model is null)
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.AssemblyOpenFailed,
                        $"SolidWorks 打开装配失败：{assemblyPath}，errors={errors}, warnings={warningCode}");
                }
            }
            else
            {
                local.Add($"复用会话里已打开的文档：{assemblyPath}");
            }

            // 轻化组件读不到面几何，配合采集会整批落空。返回值只作诊断，不据此中止。
            var resolved = TryGet(() => interop.ResolveLightweightComponents(model), -1);
            if (resolved != 0)
                local.Add($"轻化组件解析返回 {resolved}：{Path.GetFileName(assemblyPath)}");

            var directChildren = interop.GetAssemblyComponents(model, topLevelOnly: true);
            foreach (var component in directChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                children.Add(ReadChild(interop, component));
            }

            if (output is not null)
            {
                foreach (var component in interop.GetAssemblyComponents(model, topLevelOnly: false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Add(ReadOccurrence(interop, component, warnings));
                }
            }

            relations.AddRange(ReadRelations(interop, model, assemblyPath, directChildren, local, cancellationToken));

            if (openedHere)
            {
                interop.CloseDocument(interop.GetTitle(model));
                ComRelease.Final(model);
                model = null;
                openedHere = false;
                if (!CryptographicOperations.FixedTimeEquals(hash, ComputeSha256(assemblyPath)))
                    throw new InvalidDataException($"装配探查后源文件内容发生变化：{assemblyPath}");
            }
        }
        catch (ClassifiedConversionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClassifiedConversionException(
                ComErrorClassifier.Classify(ex, ConversionErrorClass.AssemblyOpenFailed),
                $"读取装配失败：{assemblyPath}：{ex.Message}",
                ex);
        }
        finally
        {
            if (model is not null)
            {
                if (openedHere)
                    TryRun(() => interop.CloseDocument(interop.GetTitle(model)));
                ComRelease.Final(model);
            }
        }

        if (children.Count == 0)
            warnings.Add($"装配没有可读的一级实例：{assemblyPath}");
        return new AssemblyDocumentReading(assemblyPath, children, local, relations);
    }

    /// <summary>一级组件 → <see cref="AssemblyChild"/>。矩阵是本文档坐标系下的局部矩阵。</summary>
    private static AssemblyChild ReadChild(SolidWorksInteropBridge interop, object component)
    {
        var name = interop.GetComponentName(component);
        var sourcePath = NormalizePath(interop.GetComponentPath(component));
        var isSubAssembly = ConversionPathLayout.HasExtension(
            sourcePath, ConversionPathLayout.SolidWorksAssemblyExtension);
        var hidden = TryGet(() => interop.GetComponentVisibility(component), ComponentVisible) != ComponentVisible;
        var diagnostics = new List<string>();
        if (!File.Exists(sourcePath))
            diagnostics.Add("引用不存在");
        if (hidden)
            diagnostics.Add("隐藏件");

        return new AssemblyChild(
            name,
            sourcePath,
            isSubAssembly,
            hidden,
            ReadMatrix(interop, component, name),
            diagnostics.Count == 0 ? null : string.Join("；", diagnostics));
    }

    /// <summary>后代组件 → <see cref="AssemblyOccurrence"/>。矩阵相对顶层文档，即世界矩阵。</summary>
    private static AssemblyOccurrence ReadOccurrence(
        SolidWorksInteropBridge interop,
        object component,
        List<string> warnings)
    {
        // Name2 已经是 "父-1/子-1" 形式，与 SE 的 OccurrenceId 拼法逐字相同，不再自己拼。
        var id = interop.GetComponentName(component);
        var separator = id.LastIndexOf('/');
        var parentId = separator < 0 ? null : id[..separator];
        var sourcePath = NormalizePath(interop.GetComponentPath(component));
        var isSubAssembly = ConversionPathLayout.HasExtension(
            sourcePath, ConversionPathLayout.SolidWorksAssemblyExtension);
        var isSuppressed = TryGet(() => interop.IsComponentSuppressed(component), false);
        var hidden = TryGet(() => interop.GetComponentVisibility(component), ComponentVisible) != ComponentVisible;
        var diagnostics = new List<string>();
        if (!File.Exists(sourcePath))
        {
            diagnostics.Add("引用不存在");
            warnings.Add($"未解析引用：{sourcePath}");
        }
        if (hidden)
            diagnostics.Add("隐藏件");
        if (isSuppressed)
        {
            diagnostics.Add("抑制/未激活");
            warnings.Add($"跳过抑制实例：{id}");
        }
        if (!isSubAssembly
            && File.Exists(sourcePath)
            && !ConversionPathLayout.HasExtension(sourcePath, ConversionPathLayout.SolidWorksPartExtension))
        {
            warnings.Add($"跳过不支持的引用：{sourcePath}");
        }

        return new AssemblyOccurrence(
            id,
            parentId,
            sourcePath,
            isSubAssembly,
            isSuppressed,
            hidden,
            ReadMatrix(interop, component, id),
            diagnostics.Count == 0 ? null : string.Join("；", diagnostics));
    }

    /// <summary>
    /// 本层的装配关系。两个来源，都要有确定去向：
    ///
    ///   · 固定组件 → 一条 <c>SwFixedComponent</c>，与 SE 的接地关系同处理；
    ///   · <c>MateGroup</c> 下的每条 <c>IMate2</c> → 一条配合关系，几何取自被引用的面。
    ///
    /// 单条关系读失败只丢这一条并记诊断，绝不让整层落空——读不到的关系最坏结果是
    /// 该组件保持固定，位置仍与源装配逐元素一致。
    /// </summary>
    private static IReadOnlyList<AssemblyRelation> ReadRelations(
        SolidWorksInteropBridge interop,
        object model,
        string assemblyPath,
        IReadOnlyList<object> directChildren,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var relations = new List<AssemblyRelation>();
        var index = 0;

        foreach (var component in directChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGet(() => interop.IsComponentFixed(component), false))
                continue;
            relations.Add(new AssemblyRelation(
                assemblyPath,
                index++,
                MateTypeMapper.SolidWorksFixed,
                interop.GetComponentName(component),
                null,
                null,
                null));
        }

        object? feature = null;
        try
        {
            feature = interop.FirstFeature(model);
            while (feature is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? next = null;
                try
                {
                    next = interop.NextFeature(feature);
                    if (string.Equals(
                            TryGet(() => interop.FeatureTypeName(feature), string.Empty),
                            "MateGroup",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ReadMateGroup(
                            interop, feature, assemblyPath, relations, ref index, diagnostics, cancellationToken);
                    }
                }
                finally
                {
                    ComRelease.Final(feature);
                    feature = next;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add("遍历特征树读取配合失败：" + ex.Message);
        }
        finally
        {
            ComRelease.Final(feature);
        }

        return relations;
    }

    private static void ReadMateGroup(
        SolidWorksInteropBridge interop,
        object mateGroup,
        string assemblyPath,
        List<AssemblyRelation> relations,
        ref int index,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        object? sub = interop.GetFirstSubFeature(mateGroup);
        while (sub is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? next = null;
            try
            {
                next = interop.GetNextSubFeature(sub);
                var relation = ReadMate(interop, sub, assemblyPath, index, diagnostics);
                if (relation is not null)
                {
                    relations.Add(relation);
                    index++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"配合 #{index} 读取失败：{ex.Message}");
            }
            finally
            {
                ComRelease.Final(sub);
                sub = next;
            }
        }
    }

    private static AssemblyRelation? ReadMate(
        SolidWorksInteropBridge interop,
        object mateFeature,
        string assemblyPath,
        int index,
        List<string> diagnostics)
    {
        var featureName = TryGet(() => interop.FeatureName(mateFeature), $"Mate{index}");
        object? mate = null;
        try
        {
            mate = interop.SpecificFeature(mateFeature);
            if (mate is null)
            {
                diagnostics.Add($"配合 {featureName}：GetSpecificFeature2 返回空。");
                return null;
            }

            var mateType = interop.GetMateType(mate);
            var interfaceName = MapMateTypeName(mateType);
            var suppressed = TryGet(() => interop.IsFeatureSuppressed(mateFeature), false);
            var entityCount = TryGet(() => interop.GetMateEntityCount(mate), 0);
            var notes = new List<string>();

            RelationGeometry? geometry1 = null;
            RelationGeometry? geometry2 = null;
            string? occurrence1 = null;
            string? occurrence2 = null;
            if (entityCount >= 2)
            {
                (occurrence1, geometry1) = ReadMateSide(interop, mate, 0, notes);
                (occurrence2, geometry2) = ReadMateSide(interop, mate, 1, notes);
            }
            else
            {
                notes.Add($"配合实体数为 {entityCount}，本版只翻译两侧配合");
            }

            // 距离值只有距离配合才有意义。取不到就留 0 并记诊断，绝不拿 0 冒充"确实是 0"。
            var offset = 0d;
            if (mateType == MateTypeDistance)
            {
                var value = interop.GetMateDimensionValue(mate);
                if (value is { } distance)
                    offset = distance;
                else
                    notes.Add("距离配合读不到尺寸值");
            }

            return new AssemblyRelation(
                assemblyPath,
                index,
                interfaceName,
                occurrence1,
                occurrence2,
                geometry1,
                geometry2,
                offset,
                NormalsAligned: TryGet(() => interop.GetMateAlignment(mate), 0) == 0,
                ParallelOffset: false,
                IsSuppressed: suppressed,
                Diagnostic: notes.Count == 0 ? null : $"{featureName}：" + string.Join("；", notes));
        }
        finally
        {
            ComRelease.Final(mate);
        }
    }

    /// <summary>
    /// 配合的一侧：所属组件名 + 该侧几何。
    ///
    /// 几何一律用**被引用面的曲面参数乘组件变换**得到，与 <see cref="MateCandidateCollector"/>
    /// 在目标装配里收集候选时走的是同一套换算——两边同源，匹配才有意义。
    /// 不用 <c>EntityParams</c>：它的布局随引用类型变化且未实测，猜错不会报错，只会静默错配。
    /// </summary>
    private static (string? Occurrence, RelationGeometry? Geometry) ReadMateSide(
        SolidWorksInteropBridge interop,
        object mate,
        int side,
        List<string> notes)
    {
        object? entity = null;
        object? reference = null;
        try
        {
            entity = interop.GetMateEntity(mate, side);
            if (entity is null)
            {
                notes.Add($"侧{side + 1} 取不到配合实体");
                return (null, null);
            }

            // ReferenceComponent 与组件表里的是同一个 RCW，绝不 final 释放。
            var component = interop.GetMateEntityComponent(entity);
            var occurrence = component is null ? null : TryGet(() => interop.GetComponentName(component), null);
            if (occurrence is null)
                notes.Add($"侧{side + 1} 取不到所属组件");

            reference = interop.GetMateEntityReference(entity);
            if (reference is null)
            {
                notes.Add($"侧{side + 1} 取不到被引用实体");
                return (occurrence, null);
            }

            var transform = component is null ? null : TryGet(() => interop.GetComponentTransform(component), null);
            if (transform is not { Length: 16 })
            {
                notes.Add($"侧{side + 1} 取不到组件变换");
                return (occurrence, null);
            }

            var geometry = ToGeometry(interop, reference, transform);
            if (geometry is null)
            {
                notes.Add($"侧{side + 1} 的引用不是可匹配的平面/圆柱/圆锥面（引用类型 "
                    + TryGet(() => interop.GetMateEntityReferenceType(entity), -1) + "）");
            }

            return (occurrence, geometry);
        }
        finally
        {
            ComRelease.Final(reference);
            ComRelease.Final(entity);
        }
    }

    /// <summary>面 → 关系几何。参数在零件系，必须乘组件变换换到本装配系。</summary>
    private static RelationGeometry? ToGeometry(
        SolidWorksInteropBridge interop,
        object face,
        double[] transform)
    {
        object? surface = null;
        try
        {
            surface = TryGet(() => interop.GetFaceSurface(face), null);
            if (surface is null)
                return null;

            if (TryGet(() => interop.SurfaceIsPlane(surface), false)
                && interop.GetSurfaceParameters(surface, "PlaneParams") is { Length: >= 6 } plane)
            {
                // PlaneParams：前三个是法向，后三个是根点。
                return new RelationGeometry(
                    MateGeometryMatcher.GeometryPlane,
                    TransformPoint([plane[3], plane[4], plane[5]], transform),
                    TransformDirection([plane[0], plane[1], plane[2]], transform));
            }

            if (TryGet(() => interop.SurfaceIsCylinder(surface), false)
                && interop.GetSurfaceParameters(surface, "CylinderParams") is { Length: >= 6 } cylinder)
            {
                return new RelationGeometry(
                    MateGeometryMatcher.GeometryAxis,
                    TransformPoint([cylinder[0], cylinder[1], cylinder[2]], transform),
                    TransformDirection([cylinder[3], cylinder[4], cylinder[5]], transform));
            }

            if (TryGet(() => interop.SurfaceIsCone(surface), false)
                && interop.GetSurfaceParameters(surface, "ConeParams") is { Length: >= 6 } cone)
            {
                return new RelationGeometry(
                    MateGeometryMatcher.GeometryAxis,
                    TransformPoint([cone[0], cone[1], cone[2]], transform),
                    TransformDirection([cone[3], cone[4], cone[5]], transform));
            }

            return null;
        }
        catch
        {
            // 引用不是面（基准面、边、点…）时反射调用会直接抛。这不是故障，是"本版不翻译"。
            return null;
        }
        finally
        {
            ComRelease.Final(surface);
        }
    }

    private static string MapMateTypeName(int mateType) => mateType switch
    {
        MateTypeCoincident => MateTypeMapper.SolidWorksCoincident,
        MateTypeConcentric => MateTypeMapper.SolidWorksConcentric,
        MateTypeDistance => MateTypeMapper.SolidWorksDistance,
        _ => $"SwMateType{mateType}",
    };

    /// <summary>
    /// 组件变换 → 既有契约的 16 元素矩阵。
    ///
    /// SolidWorks 的 ArrayData 是"旋转 0..8、平移 9..11、缩放 12"，而契约沿用 Solid Edge 的
    /// "旋转 0,1,2/4,5,6/8,9,10、平移 12..14"。这里做的换算与
    /// <see cref="SolidWorksAssemblyBuilder.ToSolidWorksTransform"/> 严格互逆，
    /// 保证读进来的矩阵原样写回去。
    /// </summary>
    internal static double[] FromSolidWorksTransform(IReadOnlyList<double> source)
    {
        if (source.Count != 16)
            throw new InvalidDataException("SolidWorks 组件变换必须包含 16 个元素。");
        if (Math.Abs(source[12] - 1) > ScaleTolerance)
            throw new InvalidDataException($"不支持带缩放的组件变换：缩放 {source[12]:G6}。");
        return
        [
            source[0], source[1], source[2], 0,
            source[3], source[4], source[5], 0,
            source[6], source[7], source[8], 0,
            source[9], source[10], source[11], 1,
        ];
    }

    private static double[] ReadMatrix(SolidWorksInteropBridge interop, object component, string name)
    {
        var raw = interop.GetComponentTransform(component);
        if (raw.Length != 16 || raw.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException($"SolidWorks 组件返回无效矩阵：{name}");
        return FromSolidWorksTransform(raw);
    }

    /// <summary>SolidWorks 变换布局：旋转 0..8（行主序），平移 9..11，缩放 12。</summary>
    private static double[] TransformPoint(IReadOnlyList<double> point, IReadOnlyList<double> transform) =>
    [
        (point[0] * transform[0]) + (point[1] * transform[3]) + (point[2] * transform[6]) + transform[9],
        (point[0] * transform[1]) + (point[1] * transform[4]) + (point[2] * transform[7]) + transform[10],
        (point[0] * transform[2]) + (point[1] * transform[5]) + (point[2] * transform[8]) + transform[11],
    ];

    private static double[] TransformDirection(IReadOnlyList<double> direction, IReadOnlyList<double> transform) =>
    [
        (direction[0] * transform[0]) + (direction[1] * transform[3]) + (direction[2] * transform[6]),
        (direction[0] * transform[1]) + (direction[1] * transform[4]) + (direction[2] * transform[7]),
        (direction[0] * transform[2]) + (direction[1] * transform[5]) + (direction[2] * transform[8]),
    ];

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    private static long TryGetHandle(SolidWorksInteropBridge interop)
    {
        try { return interop.GetWindowHandle(); } catch { return 0; }
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
