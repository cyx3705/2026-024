
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V3.5 §3.5–§3.6：在一个刚生成好的 <c>.SLDASM</c> 上重建装配关系。
///
/// 调用时机是 V3.3 的 <c>BuildNode</c> 里"组件已插入、变换已校验、尚未保存"那一刻——
/// 此时的位置就是 §3.6 说的**基线**，它已经被逐元素验证与 Solid Edge 一致。
///
/// 三条不可动摇的规矩：
///   1. **基线只取一次**，不随配合推进而更新。否则误差会逐条累积，最后整体偏了也没人发现。
///   2. **一条一条加、一条一条验**，不批量提交。慢，但每条失败都能定位到具体是哪条关系。
///   3. **收尾时把没被约束住的组件重新固定**，保证位置精度绝不退化——
///      配合是净增益，建不起来就退回 V3.3 的状态，而不是交付一个会跑位的装配。
/// </summary>
internal static class SolidWorksMateRebuilder
{
    /// <summary>与 V3.0 起的位置判据同口径。</summary>
    private const double TranslationTolerance = 1e-6;
    private const double RotationTolerance = 1e-9;

    private const int SelectionMark = 1;

    /// <summary>
    /// swAddMateError_e.swAddMateError_NoError。**成功是 1 不是 0**（0 是 ErrorUknown，
    /// IncorrectSelections 是 4）。26 号报表的整个阻塞就是把 NoError 当失败、
    /// 删掉了每一条刚建成的配合。已从官方 swconst 程序集实测确认。
    /// </summary>
    private const int SwAddMateNoError = 1;

    public static MateOutcome Rebuild(
        SolidWorksInteropBridge interop,
        object assemblyModel,
        object mathUtility,
        AssemblyNode node,
        IReadOnlyList<AssemblyRelation> relations,
        IReadOnlyDictionary<string, object> componentsByName,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var rebuilt = 0;
        var groundApplied = 0;
        var skippedSuppressed = 0;
        var skippedUnsupported = 0;
        var failedUnmatched = 0;
        var failedAmbiguous = 0;
        var failedRejected = 0;
        var maxDrift = 0d;

        // §3.6 基线：此刻的位置已由 V3.3 验证过与 SE 一致，只取这一次。
        var baseline = componentsByName.ToDictionary(
            pair => pair.Key,
            pair => interop.GetComponentTransform(pair.Value),
            StringComparer.Ordinal);

        var groundNames = relations
            .Where(item => !item.IsSuppressed
                && string.Equals(item.InterfaceName, MateTypeMapperNames.Ground, StringComparison.Ordinal))
            .Select(item => item.Occurrence1)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        // §3.5：先浮动全部，再固定基准件。没有接地关系就固定第一个组件并记入报告。
        FloatAll(interop, assemblyModel, componentsByName.Values);
        if (groundNames.Count == 0 && componentsByName.Count > 0)
        {
            var fallback = componentsByName.First();
            groundNames.Add(fallback.Key);
            diagnostics.Add($"该层没有接地关系，已固定第一个组件作为基准：{fallback.Key}");
        }

        var constrained = new HashSet<string>(groundNames, StringComparer.Ordinal);
        FixNamed(interop, assemblyModel, componentsByName, groundNames);

        // 面候选只能在两次重建之间缓存。
        //
        // 实测事故（识别开启时 49/56 条配合失败）：每加一条配合都会 ForceRebuild，
        // **重建让缓存里的 Face2 引用全部失效**，下一条配合选实体时抛
        // 0x80010108 RPC_E_DISCONNECTED。哑实体没有特征树、重建近乎空操作，
        // 指针侥幸能用；识别版一重建就全废——所以这个缺陷只在"识别 + 配合"同时开时出现。
        var candidateCache = new Dictionary<string, IReadOnlyList<MateCandidate>>(StringComparer.Ordinal);

        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = MateTypeMapper.Map(relation);
            switch (plan.Kind)
            {
                case MatePlanKind.SkipSuppressed:
                    skippedSuppressed++;
                    continue;
                case MatePlanKind.Fix:
                    // 接地关系已经在上面落为固定，这里只记账——每条关系都要有确定去向。
                    groundApplied++;
                    continue;
                case MatePlanKind.Unsupported:
                    skippedUnsupported++;
                    diagnostics.Add($"#{relation.Index} {relation.InterfaceName}：{plan.Reason}");
                    continue;
            }

            if (relation.Geometry1 is null || relation.Geometry2 is null)
            {
                failedUnmatched++;
                diagnostics.Add($"#{relation.Index} {relation.InterfaceName}：关系缺少几何，无法定位实体");
                continue;
            }

            var side1 = Locate(interop, relation.Occurrence1, relation.Geometry1, componentsByName, candidateCache);
            var side2 = Locate(interop, relation.Occurrence2, relation.Geometry2, componentsByName, candidateCache);
            // 缓存可能是上一次重建之前建立的：任一侧指针已断开就整表重建后重试一次。
            if (IsStale(interop, side1) || IsStale(interop, side2))
            {
                candidateCache.Clear();
                side1 = Locate(interop, relation.Occurrence1, relation.Geometry1, componentsByName, candidateCache);
                side2 = Locate(interop, relation.Occurrence2, relation.Geometry2, componentsByName, candidateCache);
            }
            if (side1.Status != MateMatchStatus.Matched || side2.Status != MateMatchStatus.Matched)
            {
                var ambiguous = side1.Status == MateMatchStatus.Ambiguous || side2.Status == MateMatchStatus.Ambiguous;
                if (ambiguous)
                    failedAmbiguous++;
                else
                    failedUnmatched++;
                diagnostics.Add(
                    $"#{relation.Index} {relation.InterfaceName}：实体定位失败"
                    + $"（侧1 {side1.Status}/{side1.CandidateKeys.Count}，侧2 {side2.Status}/{side2.CandidateKeys.Count}）");
                continue;
            }

            var applied = TryApply(
                interop, assemblyModel, mathUtility, plan, side1, side2, baseline, componentsByName,
                out var drift, out var reason);
            if (applied)
            {
                // 这条配合触发了重建，缓存里的面指针已经作废，下一条必须重新收集。
                candidateCache.Clear();
                rebuilt++;
                maxDrift = Math.Max(maxDrift, drift);
                if (relation.Occurrence1 is not null)
                    constrained.Add(relation.Occurrence1);
                if (relation.Occurrence2 is not null)
                    constrained.Add(relation.Occurrence2);
            }
            else
            {
                failedRejected++;
                diagnostics.Add($"#{relation.Index} {relation.InterfaceName}：{reason}");
            }
        }

        // §3.5 第 4 步：没被任何成功配合约束到的组件重新固定。
        // 这是"产物永远可用"承诺的落点——配合没建起来也绝不让位置退化。
        var leftFixed = componentsByName.Keys.Where(name => !constrained.Contains(name)).ToArray();
        if (leftFixed.Length > 0)
        {
            FixNamed(interop, assemblyModel, componentsByName, leftFixed);
            diagnostics.Add($"{leftFixed.Length} 个组件未被任何配合约束，已保持固定：{string.Join("、", leftFixed)}");
        }

        // 收尾再验一次整体位置，确保回滚都干净。
        maxDrift = Math.Max(maxDrift, MeasureDrift(interop, componentsByName, baseline, out var rotationDrift));
        if (maxDrift >= TranslationTolerance || rotationDrift >= RotationTolerance)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.MateRejected,
                $"配合重建后位置退化：平移 {maxDrift:G6} m、旋转 {rotationDrift:G6}（{Path.GetFileName(node.OutputPath)}）。"
                    + $" 已建 {rebuilt}/{relations.Count} 条。"
                    + (diagnostics.Count == 0 ? string.Empty : " || " + string.Join(" || ", diagnostics.Take(6))));
        }

        var outcome = new MateOutcome(
            relations.Count,
            rebuilt,
            skippedSuppressed,
            skippedUnsupported,
            failedUnmatched,
            failedAmbiguous,
            failedRejected,
            leftFixed.Length,
            maxDrift,
            diagnostics,
            groundApplied);

        reporter.Report(
            null,
            ConversionStage.AssemblyBuild,
            $"{Path.GetFileName(node.OutputPath)} 配合重建：{rebuilt}/{relations.Count} 条，"
                + $"{leftFixed.Length} 个组件保持固定，最大漂移 {maxDrift:G3} m。"
                + (diagnostics.Count == 0 ? string.Empty : " || " + string.Join(" || ", diagnostics.Take(4))),
            artifact: ConversionArtifactKind.SolidWorksAssembly);
        return outcome;
    }

    /// <summary>加一条配合，立刻验位置，超差或报错就删掉。§3.6 的核心。</summary>
    private static bool TryApply(
        SolidWorksInteropBridge interop,
        object assemblyModel,
        object mathUtility,
        MatePlan plan,
        MateMatch side1,
        MateMatch side2,
        IReadOnlyDictionary<string, double[]> baseline,
        IReadOnlyDictionary<string, object> componentsByName,
        out double drift,
        out string reason)
    {
        drift = 0;
        reason = string.Empty;
        var selectionDetail = string.Empty;
        object? mate = null;
        try
        {
            interop.ClearSelection(assemblyModel);
            if (!interop.SelectEntityForMate(assemblyModel, side1.Candidate!.Entity!, false, SelectionMark)
                || !interop.SelectEntityForMate(assemblyModel, side2.Candidate!.Entity!, true, SelectionMark))
            {
                reason = "配合实体选择失败";
                return false;
            }

            // SelectionMgr 同上，不释放。
            var selectionManager = interop.GetSelectionManager(assemblyModel);
            {
                var selected = interop.GetSelectedObjectCount(selectionManager);
                if (selected != 2)
                {
                    reason = $"选中的实体数不是 2，而是 {selected}";
                    return false;
                }

                selectionDetail =
                    $"[1] 类型 {interop.GetSelectedObjectType(selectionManager, 1)}"
                    + $"/mark {interop.GetSelectedObjectMark(selectionManager, 1)}"
                    + $"/有组件 {interop.SelectedObjectHasComponent(selectionManager, 1)}"
                    + $"，[2] 类型 {interop.GetSelectedObjectType(selectionManager, 2)}"
                    + $"/mark {interop.GetSelectedObjectMark(selectionManager, 2)}"
                    + $"/有组件 {interop.SelectedObjectHasComponent(selectionManager, 2)}";
            }

            mate = interop.AddMate(
                assemblyModel,
                (int)plan.MateType,
                (int)plan.Align,
                flip: false,
                plan.Distance,
                angle: 0,
                out var errorStatus);
            if (mate is null || errorStatus != SwAddMateNoError)
            {
                reason = $"AddMate5 失败，errorStatus={errorStatus}；选择实况：{selectionDetail}";
                // 即便报错，Feature 也可能已经创建并且求解器已经动过组件——
                // 删配合不会把组件挪回去，必须显式还原基线。
                RemoveMate(interop, assemblyModel, mate);
                RestoreBaseline(interop, mathUtility, componentsByName, baseline);
                return false;
            }

            interop.ForceRebuild(assemblyModel);
            drift = MeasureDrift(interop, componentsByName, baseline, out var rotationDrift);
            if (drift >= TranslationTolerance || rotationDrift >= RotationTolerance)
            {
                reason = $"求解后位置漂移超差：平移 {drift:G6} m、旋转 {rotationDrift:G6}，已回滚并还原位置";
                RemoveMate(interop, assemblyModel, mate);
                // 关键：删配合只是删约束，组件停在求解器挪过去的地方。
                // 必须把全部组件写回基线变换，回滚才算真的回滚。
                RestoreBaseline(interop, mathUtility, componentsByName, baseline);
                interop.ForceRebuild(assemblyModel);
                drift = 0;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reason = "应用配合时异常：" + ex.Message;
            TryRun(() => RemoveMate(interop, assemblyModel, mate));
            TryRun(() => RestoreBaseline(interop, mathUtility, componentsByName, baseline));
            TryRun(() => interop.ForceRebuild(assemblyModel));
            return false;
        }
    }

    /// <summary>
    /// 删掉刚加的那条配合。
    ///
    /// <c>EditDelete</c> 删的是**当前选中的任何东西**——选错了就会删掉真实组件。
    /// 因此这里先确认拿到的 Feature 真的是配合（类型名含 Mate），再删；
    /// 拿不准就宁可留着一条多余配合，也绝不冒删组件的风险。
    /// </summary>
    /// <summary>
    /// 探一下候选实体是不是还活着。COM 对象被重建冲掉后任何调用都抛
    /// RPC_E_DISCONNECTED，这里用一次廉价的属性读取把它问出来。
    /// </summary>
    private static bool IsStale(SolidWorksInteropBridge interop, MateMatch match)
    {
        if (match.Status != MateMatchStatus.Matched || match.Candidate?.Entity is not { } entity)
            return false;
        try
        {
            return interop.GetFaceSurface(entity) is null;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>把全部组件写回基线变换。回滚的另一半——删配合只删约束，不还原位置。</summary>
    private static void RestoreBaseline(
        SolidWorksInteropBridge interop,
        object mathUtility,
        IReadOnlyDictionary<string, object> componentsByName,
        IReadOnlyDictionary<string, double[]> baseline)
    {
        foreach (var (name, component) in componentsByName)
        {
            if (!baseline.TryGetValue(name, out var expected) || expected.Length != 16)
                continue;
            var transform = interop.CreateTransform(mathUtility, expected);
            try
            {
                interop.SetComponentTransform(component, transform);
            }
            finally
            {
                ComRelease.Final(transform);
            }
        }
    }

    private static void RemoveMate(SolidWorksInteropBridge interop, object assemblyModel, object? mate)
    {
        if (mate is null)
            return;
        TryRun(() =>
        {
            var typeName = interop.GetFeatureTypeName(mate);
            if (typeName.IndexOf("Mate", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            interop.ClearSelection(assemblyModel);
            if (interop.SelectFeature(assemblyModel, mate))
                interop.DeleteSelection(assemblyModel);
            interop.ClearSelection(assemblyModel);
        });
    }

    /// <summary>相对基线的最大平移与旋转偏差。基线永远是 V3.3 验证过的那一份。</summary>
    private static double MeasureDrift(
        SolidWorksInteropBridge interop,
        IReadOnlyDictionary<string, object> componentsByName,
        IReadOnlyDictionary<string, double[]> baseline,
        out double rotationDrift)
    {
        var translation = 0d;
        rotationDrift = 0d;
        foreach (var (name, component) in componentsByName)
        {
            if (!baseline.TryGetValue(name, out var expected))
                continue;
            var actual = interop.GetComponentTransform(component);
            if (actual.Length != 16)
                continue;
            for (var index = 0; index < 9; index++)
                rotationDrift = Math.Max(rotationDrift, Math.Abs(expected[index] - actual[index]));
            for (var index = 9; index < 12; index++)
                translation = Math.Max(translation, Math.Abs(expected[index] - actual[index]));
        }

        return translation;
    }

    private static MateMatch Locate(
        SolidWorksInteropBridge interop,
        string? occurrenceName,
        RelationGeometry geometry,
        IReadOnlyDictionary<string, object> componentsByName,
        Dictionary<string, IReadOnlyList<MateCandidate>> cache)
    {
        if (occurrenceName is null || !componentsByName.TryGetValue(occurrenceName, out var component))
            return new MateMatch(MateMatchStatus.Unmatched, null, []);
        if (!cache.TryGetValue(occurrenceName, out var candidates))
        {
            candidates = MateCandidateCollector.Collect(interop, component);
            cache[occurrenceName] = candidates;
        }

        return MateGeometryMatcher.Match(geometry, candidates);
    }

    private static void FloatAll(SolidWorksInteropBridge interop, object assemblyModel, IEnumerable<object> components)
    {
        interop.ClearSelection(assemblyModel);
        var any = false;
        foreach (var component in components)
            any |= interop.SelectComponent(component, append: true);
        if (any)
            interop.UnfixSelectedComponents(assemblyModel);
        interop.ClearSelection(assemblyModel);
    }

    private static void FixNamed(
        SolidWorksInteropBridge interop,
        object assemblyModel,
        IReadOnlyDictionary<string, object> componentsByName,
        IEnumerable<string> names)
    {
        interop.ClearSelection(assemblyModel);
        var any = false;
        foreach (var name in names)
        {
            if (componentsByName.TryGetValue(name, out var component))
                any |= interop.SelectComponent(component, append: true);
        }

        if (any)
            interop.FixSelectedComponents(assemblyModel);
        interop.ClearSelection(assemblyModel);
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
