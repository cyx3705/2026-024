using SE2SW;
using SE2SW.Contracts;
using SE2SW.Worker;
using AppShell.Core.Docking;
using AppShell.Core.Modules;
using System.Text.Json;
using System.Windows.Threading;

var root = Path.Combine(Path.GetTempPath(), "se2sw-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    TestSharedContractsAndVersion();
    TestPartImportIsolationContracts(root);
    TestCadProcessOwnershipResolution();
    TestOhsLayoutAndScan(root);
    TestViewModelDirectorySelection(root);
    TestMissingUnusedPreflight(root);
    TestExternalMapping(root);
    TestExternalLegacyAndDirectoryCreation(root);
    TestDuplicateOutputRejection(root);
    TestAssemblyPlanningAndJson(root);
    TestAssemblyLegacyReuse(root);
    TestAssemblyDuplicateNames(root);
    TestAssemblyRetryReuse(root);
    TestAssemblyRetryRejectsUnsafeOutputs(root);
    TestAssemblyTemplateFallback(root);
    TestComponentDocumentReuseGuard();
    TestMateCandidateStalenessContract();
    TestAssemblyMatrixMapping();
    TestAssemblyTree();
    TestAssemblyGraphTopology();
    TestAssemblyGraphSharedAndRepeated();
    TestAssemblyGraphCycleAndMissing();
    TestAssemblyGraphLocalTransformComposition();
    TestAssemblyTransformVerifierWithRotation();
    TestAssemblyTransformVerifierCatchesWrongFrame();
    TestAssemblyPlannerNesting(root);
    TestAssemblyPlannerRejectsBrokenGraph(root);
    TestAssemblyNodeReuse(root);
    TestMateGeometryMatching();
    TestMateTypeMapping();
    TestMateOutcomeSelfConsistency();
    TestMateCandidateEquivalence();
    TestNestedAssemblyMetrics();
    TestAssemblyModeValidationText();
    TestAssemblyViewModelState(root);
    TestAssemblyMateSwitchAndReport(root);
    TestAssemblyActiveRunDisposal(root);
    TestUnifiedSourceWorkspace();
    TestUnifiedPartDirectoryFlow(root);
    TestUiModuleRegistration();
    TestTemporaryOutput(root);
    TestParasolidTextProbe(root);
    TestFeatureRecognitionRetries();
    TestFeatureRecognitionSessionGuards();
    TestRecognitionGeometryGuard();
    TestRecognitionSemanticGuard();
    TestImportIdentityAndSessionFaultGuards();
    TestActiveRunDisposal(root);
    Console.WriteLine("SE2SW.Smoke: PASS");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void TestCadProcessOwnershipResolution()
{
    Equal(
        0,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int>(), Array.Empty<int>(), 0),
        "COM 返回早于 CAD 进程出现时不得凭空取得所有权");
    Equal(
        200,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int>(), [200], 0),
        "启动前无 CAD 且只出现一个新 PID 时必须取得所有权");
    Equal(
        200,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int> { 100 }, [100, 200], 0),
        "已有用户进程时只能认领唯一新增 PID");
    Equal(
        0,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int> { 100 }, [100], 100),
        "窗口句柄指向启动前已有 PID 时不得取得所有权");
    Equal(
        0,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int>(), [200, 300], 0),
        "多个新增 PID 且没有窗口证据时必须保持未知");
    Equal(
        300,
        CadProcessOwnership.ResolveOwnedProcessId(new HashSet<int>(), [200, 300], 300),
        "窗口句柄必须能消解多个新增 PID 的歧义");
}

static void TestSharedContractsAndVersion()
{
    // 期望值从版本真源现读，不写死字面量——写死等于每次升版都要改这个测试，
    // 而这道守卫要证明的恰恰是"版本只有一个来源"，它自己就不该成为第二个来源。
    var expected = ReadVersionFromSingleSource();
    Equal(expected, typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3), "UI 程序集版本必须来自唯一版本源");
    Equal(expected, typeof(ConversionJob).Assembly.GetName().Version?.ToString(3), "Contracts 程序集版本必须来自唯一版本源");
    Equal(expected, typeof(WorkerRequestValidator).Assembly.GetName().Version?.ToString(3), "Worker 程序集版本必须来自唯一版本源");
    Equal(expected, new ModuleInfo().Version, "模块运行时版本不得另存字符串副本");
    Equal(expected, ReadVersionFromManifest(), "OHS 注册清单版本必须与版本真源一致");
    Equal(180, FeatureRecognitionPolicy.DefaultTimeoutSeconds, "特征识别默认无进度预算必须是三分钟");
    Equal(180, FeatureRecognitionPolicy.NormalizeTimeoutSeconds(0), "缺省超时必须回到三分钟");
    Equal(180, FeatureRecognitionPolicy.NormalizeTimeoutSeconds(180), "显式三分钟超时不得被改写");
    Equal(3600, FeatureRecognitionPolicy.NormalizeTimeoutSeconds(99999), "超时配置必须有上限");
    Equal(
        FeatureRecognitionPolicy.DefaultTimeoutSeconds,
        new AssemblyBatchRequest("b", ConversionMode.External, "a.asm", "a.SLDASM", [], []).FeatureRecognitionTimeoutSeconds,
        "装配请求不得保留独立的旧超时默认值");

    True(WorkerProtocol.IsKnownVerb(WorkerProtocol.PartsRequestVerb), "零件 Worker 动词必须由共享合同认可");
    True(WorkerProtocol.IsKnownVerb(WorkerProtocol.PartImportVerb), "单零件隔离导入动词必须由共享合同认可");
    True(WorkerProtocol.IsKnownVerb(WorkerProtocol.AssemblyProbeVerb), "装配探查动词必须由共享合同认可");
    True(WorkerProtocol.IsKnownVerb(WorkerProtocol.AssemblyBuildVerb), "装配构建动词必须由共享合同认可");
    True(!WorkerProtocol.IsKnownVerb("--unknown"), "未知 Worker 动词必须被拒绝");

    var directories = ConversionPathLayout.ResolveExternalDirectories(@"C:\fixture");
    Equal(@"C:\fixture\XT", directories.XtDirectory, "外界 XT 目录必须由共享路径合同解析");
    Equal(@"C:\fixture\SW", directories.SolidWorksDirectory, "外界 SW 目录必须由共享路径合同解析");
    var paths = ConversionPathLayout.ResolvePartPaths(@"C:\fixture\Part.par", directories.XtDirectory, directories.SolidWorksDirectory, directories.RootDirectory);
    Equal(@"C:\fixture\XT\Part.x_t", paths.XtPath, "XT 路径必须由共享路径合同解析");
    Equal(@"C:\fixture\SW\Part.SLDPRT", paths.SolidWorksPath, "SLDPRT 路径必须由共享路径合同解析");
    Equal(@"C:\fixture\Part.x_t", paths.LegacyXtPath, "旧平铺 XT 候选必须由共享路径合同解析");
    Equal(@"C:\fixture\Part.SLDPRT", paths.LegacySolidWorksPath, "旧平铺 SLDPRT 候选必须由共享路径合同解析");
    Equal(@"C:\fixture\SW\Top.SLDASM", ConversionPathLayout.ResolveAssemblyOutputPath(@"C:\fixture\Top.asm", directories.SolidWorksDirectory),
        "SLDASM 路径必须由共享路径合同解析");

    var row = new ConversionFileRow(new ScanCandidate(@"C:\fixture\Part.par", paths.XtPath, paths.SolidWorksPath, false));
    var reuseEvent = new WorkerEvent("smoke", row.Id, ConversionStage.Skipped, "消息文本不应参与复用类别判断：XT", ReuseKind: ReuseKind.ExistingSolidWorksPart);
    Equal("复用 SW", ConversionProgressPresenter.GetRowStatus(reuseEvent, "排队"), "UI 必须使用结构化复用类别，不得解析消息文本");
    ConversionProgressPresenter.ApplyFeatureOutcome(row, new FeatureOutcome(2, true, 1, 1, [], false, 1));
    Equal("2", row.FeatureText, "共享进度呈现必须更新特征结果");
    Equal("1/1", row.SketchText, "共享进度呈现必须更新草图结果");

    var json = JsonSerializer.Serialize(reuseEvent, WorkerProtocol.CreateJsonOptions());
    var roundTrip = JsonSerializer.Deserialize<WorkerEvent>(json, WorkerProtocol.CreateJsonOptions());
    Equal(ReuseKind.ExistingSolidWorksPart, roundTrip?.ReuseKind, "结构化复用类别必须可经 Worker JSON 往返");
}

static void TestPartImportIsolationContracts(string root)
{
    var directory = Path.Combine(root, "part-import-isolation");
    var sourceDirectory = Path.Combine(directory, "source");
    var xtDirectory = Path.Combine(directory, "XT");
    var swDirectory = Path.Combine(directory, "SW");
    Directory.CreateDirectory(sourceDirectory);
    Directory.CreateDirectory(xtDirectory);
    Directory.CreateDirectory(swDirectory);
    var sourcePath = Path.Combine(sourceDirectory, "part.par");
    var xtPath = Path.Combine(xtDirectory, "part.x_t");
    var swPath = Path.Combine(swDirectory, "part.SLDPRT");
    File.WriteAllText(sourcePath, "part");
    File.WriteAllText(xtPath, "xt");

    var job = new ConversionJob("part-1", sourcePath, xtPath, swPath);
    var request = new PartImportRequest(
        "batch-1",
        ConversionMode.External,
        job,
        RecognizeFeatures: true,
        FullyDefineSketches: true,
        FeatureRecognitionTimeoutSeconds: 90,
        ContinueWhenRecognitionFails: false);
    WorkerRequestValidator.Validate(request);

    var json = JsonSerializer.Serialize(request, WorkerProtocol.CreateJsonOptions());
    var roundTrip = JsonSerializer.Deserialize<PartImportRequest>(json, WorkerProtocol.CreateJsonOptions());
    Equal(request, roundTrip, "单零件隔离请求必须可经 Worker JSON 往返");
    Equal(job, roundTrip?.Job, "单零件隔离请求不得丢失任务路径");

    var batch = new BatchRequest("batch-1", ConversionMode.External, [job], RecognizeFeatures: true);
    var recognitionStartedAt = DateTimeOffset.Parse("2026-08-04T12:00:00+00:00");
    True(
        !SolidWorksPartImportIsolation.HasRecognitionStalled(
            0,
            recognitionStartedAt.AddMinutes(10),
            TimeSpan.FromMinutes(3)),
        "未收到单件开始识别事件时不得启动无进度超时");
    True(
        !SolidWorksPartImportIsolation.HasRecognitionStalled(
            recognitionStartedAt.Ticks,
            recognitionStartedAt.AddSeconds(179),
            TimeSpan.FromMinutes(3)),
        "三分钟预算内不得终止识别子 Worker");
    True(
        SolidWorksPartImportIsolation.HasRecognitionStalled(
            recognitionStartedAt.Ticks,
            recognitionStartedAt.AddMinutes(3),
            TimeSpan.FromMinutes(3)),
        "三分钟无进度必须触发哑实体回退");
    True(SolidWorksPartImportIsolation.ShouldIsolate(batch), "启用 FeatureWorks 时必须逐零件隔离");
    // FeatureWorks 崩在某个复杂零件上以后，继续附着同一个进程只会拿到同一具尸体——
    // 现场实测跑到第 14 件崩溃、剩下 39 件全退哑实体。首次尝试仍借用现有会话
    // （多半是人工激活过的那个，识别质量最好），重试才升级为专属进程。
    True(
        !SolidWorksPartImportIsolation.ShouldUseDedicatedSession(attempt: 1),
        "首次尝试必须借用现有会话，人工激活过的会话识别质量最好");
    True(
        SolidWorksPartImportIsolation.ShouldUseDedicatedSession(attempt: 2),
        "会话故障后的重试必须换专属进程，否则会拿到同一个已损坏的 FeatureWorks");
    True(
        !SolidWorksPartImportIsolation.ShouldIsolate(batch with { RecognizeFeatures = false }),
        "未启用 FeatureWorks 时必须保留原批量导入路径");
    // V3.6.4：推翻 V3.6.2 的"附着已有会话时重载 FeatureWorks"。
    // 实测（2026-08-05）自动识别依赖一份由人工识别建立的**进程内激活状态**，
    // UnloadAddIn 会把它一起清掉，让用户手工激活过的会话整批退化成哑实体。
    True(
        !SolidWorksImporter.ShouldResetFeatureWorksSession(resetRequested: true, ownsFreshInstance: false),
        "附着已有 SolidWorks 会话时不得重载 FeatureWorks（会清掉人工激活状态）");
    True(
        !SolidWorksImporter.ShouldResetFeatureWorksSession(resetRequested: true, ownsFreshInstance: true),
        "全新 SolidWorks 会话不得在首个文档前卸载并重载 FeatureWorks");
    True(
        !SolidWorksImporter.ShouldResetFeatureWorksSession(resetRequested: false, ownsFreshInstance: false),
        "未请求隔离重置时不得改变 FeatureWorks 加载状态");
    True(
        SolidWorksPartImportIsolation.UnactivatedSessionThreshold >= 2,
        "未激活判定阈值不得低于 2，单个零件确实可能本就没有可识别特征");
    True(
        SolidWorksPartImportIsolation.DescribeUnactivatedSession(2).Contains("手工", StringComparison.Ordinal),
        "未激活诊断必须告诉用户去做什么，而不是只说失败");
    True(
        SolidWorksPartImportIsolation
            .DescribeUnactivatedSession(0, reportedByAddIn: true)
            .Contains("SetAdvancedOptions", StringComparison.Ordinal),
        "由加载项直接判定时，诊断必须写明依据是 SetAdvancedOptions 而非零识别启发式");
    // 语义守卫丢弃结果后识别数同样是 0，但那说明识别是好的，绝不能算作"未激活"证据。
    var semanticReject = new FeatureOutcome(
        0, false, 0, 0, [], true, 0, "结果仍包含未识别导入体，已降级。", SemanticMismatch: true);
    True(
        !semanticReject.SessionNotActivated,
        "语义守卫驳回不得置 SessionNotActivated——它恰恰证明识别在工作");
    // SetAdvancedOptions 返回 false 时，识别必然为 0（实测重试 12 次也翻不过来），
    // 因此这条结果必须同时是"未激活"和"已降级为哑实体"。
    var notActivated = FeatureRecognizer.NotActivated(new System.Diagnostics.Stopwatch(), []);
    True(notActivated.SessionNotActivated, "未激活结果必须置 SessionNotActivated");
    True(notActivated.DegradedToDumbSolid, "未激活时必须按哑实体降级");
    Equal(0, notActivated.RecognizedFeatureCount, "未激活时不得报告任何识别数");
    True(!notActivated.GeometryChanged, "未激活时识别没有跑，几何不得被标记为已改变");
    // 归因猜测不得废掉 V2.3 那条已被真机验证的首件重试：猜错一次就连补救一起没了。
    True(
        SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, true, notActivated),
        "疑似未激活也必须保留首件重试——归因只是猜测，不能据此取消补救");
    True(
        SolidWorksImporter.ShouldRetryFirstRecognition(
            0, 0, true, new FeatureOutcome(0, false, 0, 0, [], true, 0)),
        "普通的首件零识别仍必须重试一次");

    File.WriteAllText(swPath, "existing");
    Throws<IOException>(() => WorkerRequestValidator.Validate(request));
    File.Delete(swPath);
    File.Delete(xtPath);
    Throws<FileNotFoundException>(() => WorkerRequestValidator.Validate(request));
    File.WriteAllText(xtPath, "xt");

    var feature = new FeatureOutcome(3, true, 2, 2, ["fully-defined"], false, 12);
    var mate = new MateOutcome(
        RelationTotal: 1,
        MateRebuilt: 1,
        SkippedSuppressed: 0,
        SkippedUnsupported: 0,
        FailedUnmatched: 0,
        FailedAmbiguous: 0,
        FailedRejected: 0,
        ComponentsLeftFixed: 0,
        MaxDriftMeters: 1e-10,
        Diagnostics: []);
    var forwarded = new WorkerEvent(
        "child-batch",
        job.Id,
        ConversionStage.Completed,
        "child event",
        HResult: 123,
        NativeError: 4,
        NativeWarning: 5,
        ErrorClass: ConversionErrorClass.None,
        Feature: feature,
        Artifact: ConversionArtifactKind.SolidWorksPart,
        ReuseKind: ReuseKind.ExistingXt,
        Mate: mate);
    var originalOut = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        new WorkerReporter("parent-batch", WorkerProtocol.CreateJsonOptions()).Forward(forwarded);
    }
    finally
    {
        Console.SetOut(originalOut);
    }

    var forwardedRoundTrip = JsonSerializer.Deserialize<WorkerEvent>(
        output.ToString().Trim(),
        WorkerProtocol.CreateJsonOptions());
    Equal("parent-batch", forwardedRoundTrip?.BatchId, "转发事件必须归入父批次");
    Equal(forwarded.JobId, forwardedRoundTrip?.JobId, "转发事件不得丢失任务编号");
    Equal(feature.RecognizedFeatureCount, forwardedRoundTrip?.Feature?.RecognizedFeatureCount, "转发事件不得丢失特征计数");
    True(
        feature.SketchStatuses.SequenceEqual(forwardedRoundTrip?.Feature?.SketchStatuses ?? []),
        "转发事件不得丢失草图状态");
    Equal(mate.RelationTotal, forwardedRoundTrip?.Mate?.RelationTotal, "转发事件不得丢失配合总数");
    Equal(mate.MateRebuilt, forwardedRoundTrip?.Mate?.MateRebuilt, "转发事件不得丢失配合成功数");
    Equal(mate.MaxDriftMeters, forwardedRoundTrip?.Mate?.MaxDriftMeters, "转发事件不得丢失配合漂移量");
    Equal(forwarded.Artifact, forwardedRoundTrip?.Artifact, "转发事件不得丢失产物类别");
    Equal(forwarded.ReuseKind, forwardedRoundTrip?.ReuseKind, "转发事件不得丢失复用类别");
    Equal(forwarded.NativeError, forwardedRoundTrip?.NativeError, "转发事件不得丢失原生错误码");
}

/// <summary>从 build/SE2SW.Version.props 读出唯一版本源。</summary>
static string ReadVersionFromSingleSource()
{
    var path = LocateRepoFile(Path.Combine("build", "SE2SW.Version.props"));
    var match = System.Text.RegularExpressions.Regex.Match(
        File.ReadAllText(path), @"<SE2SWVersion>([^<]+)</SE2SWVersion>");
    True(match.Success, $"版本真源里找不到 SE2SWVersion：{path}");
    return match.Groups[1].Value.Trim();
}

/// <summary>OHS 注册清单在仓库外的 z-SE2SW 目录，它是第二个必须跟上的地方。</summary>
static string ReadVersionFromManifest()
{
    var path = LocateRepoFile(Path.Combine("..", "z-SE2SW", "module.manifest.json"));
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.GetProperty("version").GetString() ?? string.Empty;
}

static string LocateRepoFile(string relative)
{
    var directory = AppContext.BaseDirectory;
    for (var depth = 0; depth < 10 && directory is not null; depth++)
    {
        var candidate = Path.GetFullPath(Path.Combine(directory, relative));
        if (File.Exists(candidate))
            return candidate;
        directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
    }

    throw new FileNotFoundException($"未能从 {AppContext.BaseDirectory} 向上定位 {relative}");
}

static void TestOhsLayoutAndScan(string root)
{
    var project = Path.Combine(root, "2026-900-Smoke");
    var source = Path.Combine(project, "b-Module-SE");
    var unusedGe = Path.Combine(project, "Unused", "b-Module-GE");
    var unusedSw = Path.Combine(project, "Unused", "b-Module-SW");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(unusedGe);
    Directory.CreateDirectory(unusedSw);
    File.WriteAllText(Path.Combine(source, "A.par"), "sample-a");
    File.WriteAllText(Path.Combine(source, "B.PAR"), "sample-b");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    File.WriteAllText(Path.Combine(source, "nested", "ignored.par"), "nested");

    var layout = OhsProjectResolver.Resolve(project);
    Equal(2, OhsProjectResolver.GetRequiredMoves(layout).Count, "OHS 应同时计划 GE/SW 两次移动");
    Equal(2, FileScanner.Scan(ConversionMode.Ohs, project, layout).Count, "扫描必须只包含顶层 par");

    OhsProjectResolver.PrepareOutputDirectories(layout);
    True(Directory.Exists(layout.XtDirectory), "GE 目录应移动到项目根");
    True(Directory.Exists(layout.SolidWorksDirectory), "SW 目录应移动到项目根");
    True(!Directory.Exists(unusedGe) && !Directory.Exists(unusedSw), "Unused 候选目录应被剪切");

    File.WriteAllText(Path.Combine(layout.XtDirectory, "A.x_t"), "existing");
    var rescanned = FileScanner.Scan(ConversionMode.Ohs, project, layout);
    True(rescanned.Single(item => Path.GetFileName(item.SourcePath) == "A.par").HasExistingOutput,
        "存在 XT 时应标记输出冲突");
}

static void TestMissingUnusedPreflight(string root)
{
    var project = Path.Combine(root, "2026-901-Missing");
    var source = Path.Combine(project, "b-Module-SE");
    var unusedGe = Path.Combine(project, "Unused", "b-Module-GE");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(unusedGe);
    var layout = OhsProjectResolver.Resolve(project);

    Throws<DirectoryNotFoundException>(() => OhsProjectResolver.GetRequiredMoves(layout));
    True(Directory.Exists(unusedGe), "双目录预检失败时不得提前移动 GE");
    True(!Directory.Exists(layout.XtDirectory), "双目录预检失败时项目根不得出现 GE");
}

static void TestViewModelDirectorySelection(string root)
{
    var project = Path.Combine(root, "2026-902-ViewModel");
    var source = Path.Combine(project, "b-Module-SE");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "OhsPart.par"), "ohs");

    using var projectSelection = CreateViewModel();
    projectSelection.SetDirectory(project);
    True(projectSelection.IsOhsMode, "选择 OHS 项目根时必须保持 OHS 模式");
    Equal(1, projectSelection.Files.Count, "选择 OHS 项目根后应立即显示零件");

    using var sourceSelection = CreateViewModel();
    sourceSelection.SetDirectory(source);
    True(sourceSelection.IsOhsMode, "直接选择 b-Module-SE 时必须保持 OHS 模式");
    Equal(project, sourceSelection.SelectedDirectory, "直接选择 b-Module-SE 时应回推项目根");
    Equal(1, sourceSelection.Files.Count, "直接选择 b-Module-SE 后应立即显示零件");

    var external = Path.Combine(root, "external-view-model");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "ExternalPart.PAR"), "external");
    using var externalSelection = CreateViewModel();
    externalSelection.SetDirectory(external);
    True(externalSelection.IsExternalMode, "默认 OHS 模式选择普通零件目录时应自动切换外界模式");
    Equal(1, externalSelection.Files.Count, "自动切换外界模式后应立即显示零件");
    True(externalSelection.StatusText.Contains("已切换到零件转换模式", StringComparison.Ordinal),
        "自动切换后状态栏应明确说明当前模式");

    using var manualScan = CreateViewModel();
    manualScan.SelectedDirectory = external;
    manualScan.Scan();
    True(manualScan.IsExternalMode, "手动填写普通零件目录后重新扫描也应自动切换外界模式");
    Equal(1, manualScan.Files.Count, "重新扫描应恢复普通目录中的零件列表");

    var invalidOhs = Path.Combine(root, "invalid-ohs");
    Directory.CreateDirectory(invalidOhs);
    using var invalidSelection = CreateViewModel();
    invalidSelection.SetDirectory(invalidOhs);
    True(invalidSelection.IsOhsMode, "空的非 OHS 目录不能被误判为外界零件目录");
    True(invalidSelection.StatusText.Contains("普通零件目录请切换到零件转换", StringComparison.Ordinal),
        "OHS 扫描失败时应给出模式修正提示");
}

static SE2SWViewModel CreateViewModel()
    => new(
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);

static void TestExternalMapping(string root)
{
    var external = Path.Combine(root, "external");
    Directory.CreateDirectory(external);
    var source = Path.Combine(external, "Outside.par");
    File.WriteAllText(source, "external");
    var item = FileScanner.Scan(ConversionMode.External, external).Single();
    Equal(Path.Combine(external, "XT", "Outside.x_t"), item.XtPath, "外界模式 XT 应进入 XT 子目录");
    Equal(Path.Combine(external, "SW", "Outside.SLDPRT"), item.SolidWorksPath, "外界模式 SW 应进入 SW 子目录");
    True(!Directory.Exists(Path.Combine(external, "XT")) && !Directory.Exists(Path.Combine(external, "SW")),
        "扫描阶段不得创建 XT/SW 目录");
}

static void TestExternalLegacyAndDirectoryCreation(string root)
{
    var external = Path.Combine(root, "external-legacy");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "Legacy.par"), "source");
    File.WriteAllText(Path.Combine(external, "Legacy.SLDPRT"), "legacy");
    True(FileScanner.Scan(ConversionMode.External, external).Single().HasExistingOutput,
        "旧平铺产物必须继续被识别，避免重复转换");

    var clean = Path.Combine(root, "external-create-on-convert");
    Directory.CreateDirectory(clean);
    ExternalOutputLayout.EnsureDirectories(clean);
    True(Directory.Exists(Path.Combine(clean, "XT")) && Directory.Exists(Path.Combine(clean, "SW")),
        "执行转换前应创建 XT/SW 目录");

    var conflict = Path.Combine(root, "external-directory-conflict");
    Directory.CreateDirectory(conflict);
    File.WriteAllText(Path.Combine(conflict, "SW"), "occupied");
    Throws<IOException>(() => ExternalOutputLayout.EnsureDirectories(conflict));
    True(!Directory.Exists(Path.Combine(conflict, "XT")), "任一目录冲突时不得提前创建另一输出目录");
}

/// <summary>
/// 嵌套装配的组件文档复用判据。
///
/// 真实事故：嵌套生成器打开组件文档后只释放不关闭（V3.0 的展平版是关的），
/// 文档在 SolidWorks 会话里越堆越多，后续节点 OpenDoc6 撞上
/// swFileWithSameTitleAlreadyOpen(65536) → 组件插不进去 → **该组件连同它的配合一起消失**。
///
/// 修复是两条：关闭打开的文档；真撞上时同一文件可复用。
/// 这里锁住第二条的边界——同名不同路径绝不能复用，否则会把别人的几何插进装配。
/// </summary>
static void TestComponentDocumentReuseGuard()
{
    True(SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\out\SW\零件1.SLDPRT", @"C:\out\SW\零件1.SLDPRT"),
        "同一个文件必须允许复用——嵌套装配里上一层已经打开它是常态");
    True(SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\out\SW\零件1.SLDPRT", @"C:\out\sw\零件1.SLDPRT"),
        "路径大小写不同仍是同一个文件");
    True(SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\out\SW\..\SW\零件1.SLDPRT", @"C:\out\SW\零件1.SLDPRT"),
        "规范化后相同的路径仍是同一个文件");

    True(!SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\A\SW\零件1.SLDPRT", @"C:\B\SW\零件1.SLDPRT"),
        "同名不同路径绝不能复用——那会把别人的几何插进装配");
    True(!SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\out\SW\零件1.SLDPRT", null),
        "拿不到已打开文档的路径就无法证明是同一个，必须拒绝");
    True(!SolidWorksInteropBridge.CanReuseOpenDocument(@"C:\out\SW\零件1.SLDPRT", "   "),
        "空路径同样不足以证明身份");
}

/// <summary>
/// 面候选跨重建失效的契约。
///
/// 真实事故（识别 + 配合同时开时 49/56 条失败）：每加一条配合都会 ForceRebuild，
/// 重建让缓存里的 Face2 引用全部作废，下一条配合选实体时抛
/// 0x80010108 RPC_E_DISCONNECTED。哑实体没有特征树、重建近乎空操作，指针侥幸能用；
/// 识别版一重建就全废——所以只在两个开关同时打开时才炸。
///
/// 这里锁住判定侧：RPC_E_DISCONNECTED 必须被识别为会话级故障，
/// 从而触发缓存重建而不是被当成普通失败吞掉。
/// </summary>
static void TestMateCandidateStalenessContract()
{
    True(FeatureRecognizer.IsServerFault(new InvalidOperationException("断开") { HResult = unchecked((int)0x80010108) }),
        "RPC_E_DISCONNECTED 必须被识别——面指针跨重建失效就是以它出现的");

    // 候选携带的实体只是给 Worker 选中用，匹配逻辑不解释它；
    // 因此重新收集一次候选不应改变匹配结果。
    var geometry = Plane(0, 0.13, 0, 0, -1, 0);
    var before = MateGeometryMatcher.Match(geometry, [Cand("b1f1", 1, 0, 0.13, 0, 0, 1, 0)]);
    var after = MateGeometryMatcher.Match(geometry, [Cand("b1f1", 1, 0, 0.13, 0, 0, 1, 0)]);
    Equal(before.Status, after.Status, "重建后重新收集候选，匹配结论必须一致");
    Equal(before.Candidate!.Key, after.Candidate!.Key, "同样的几何必须选到同一个面——重建不得改变选择");
}

static void TestAssemblyPlanningAndJson(string root)
{
    var directory = Path.Combine(root, "assembly-plan");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var part = Path.Combine(directory, "Part.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(part, "part");
    var world = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0.1, 0.2, 0.3, 1,
    };
    var occurrence = new AssemblyOccurrence("Part:1", null, part, false, false, true, world, "隐藏件");
    var probe = new AssemblyProbeResult(assembly, [occurrence], [part], 0, 0, 1, 0, []);
    var json = JsonSerializer.Serialize(probe);
    var roundTrip = JsonSerializer.Deserialize<AssemblyProbeResult>(json)
        ?? throw new InvalidOperationException("装配 JSON 往返失败");
    Equal(16, roundTrip.Occurrences.Single().WorldTransform.Length, "JSON 往返必须保留 16 元素矩阵");

    var plan = AssemblyPlanner.Create(roundTrip);
    True(plan.CanConvert, "合法单件装配应通过规划门禁");
    Equal(Path.Combine(directory, "XT", "Part.x_t"), plan.Parts.Single().XtPath,
        "装配零件 XT 必须进入分层目录");
    Equal(Path.Combine(directory, "SW", "Part.SLDPRT"), plan.Parts.Single().SolidWorksPath,
        "装配零件 SLDPRT 必须进入分层目录");
    Equal(Path.Combine(directory, "SW", "Top.SLDASM"), plan.AssemblyOutputPath,
        "SLDASM 必须与零件放在 SW 目录");
    True(plan.Warnings.Any(item => item.Contains("隐藏实例", StringComparison.Ordinal)),
        "隐藏件必须保留为警告");
    True(!Directory.Exists(plan.XtDirectory) && !Directory.Exists(plan.SolidWorksDirectory),
        "装配规划阶段不得创建输出目录");

    var duplicateOccurrence = occurrence with { OccurrenceId = "Part:2", WorldTransform = (double[])world.Clone() };
    var repeated = AssemblyPlanner.Create(probe with { Occurrences = [occurrence, duplicateOccurrence] });
    Equal(1, repeated.Parts.Count, "重复实例只能产生一个唯一零件转换任务");
    Equal(2, repeated.Occurrences.Count(item => !item.IsSubAssembly), "重复实例必须全部保留用于插入");
}

static void TestAssemblyLegacyReuse(string root)
{
    var directory = Path.Combine(root, "assembly-legacy-reuse");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var part = Path.Combine(directory, "Legacy.par");
    var legacyXt = Path.Combine(directory, "Legacy.x_t");
    var legacySw = Path.Combine(directory, "Legacy.SLDPRT");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(part, "part");
    File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddMinutes(-5));
    WriteValidXt(legacyXt);
    File.WriteAllText(legacySw, "legacy-solidworks-part");
    File.SetLastWriteTimeUtc(legacyXt, DateTime.UtcNow.AddMinutes(-2));
    File.SetLastWriteTimeUtc(legacySw, DateTime.UtcNow.AddMinutes(-2));
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [new AssemblyOccurrence("Legacy:1", null, part, false, false, false, identity, null)],
        [part],
        0, 0, 1, 0, []);

    var plan = AssemblyPlanner.Create(probe);
    True(plan.CanConvert, "旧平铺产物不应阻止装配安全重试");
    Equal(legacyXt, plan.Parts.Single().XtPath, "旧平铺 XT 必须作为复用输入");
    Equal(legacySw, plan.Parts.Single().SolidWorksPath, "旧平铺 SLDPRT 必须直接参与组装");
    var job = new ConversionJob("legacy", part, plan.Parts.Single().XtPath, plan.Parts.Single().SolidWorksPath);
    Equal(1, AssemblyPartReusePlanner.Create([job], CancellationToken.None).ReusableSolidWorksParts.Count,
        "Worker 必须把有效旧平铺 SLDPRT 分类为直接复用");
}

static void TestAssemblyDuplicateNames(string root)
{
    var directory = Path.Combine(root, "assembly-duplicate");
    var a = Path.Combine(directory, "A");
    var b = Path.Combine(directory, "B");
    Directory.CreateDirectory(a);
    Directory.CreateDirectory(b);
    var assembly = Path.Combine(directory, "Top.asm");
    var partA = Path.Combine(a, "Same.par");
    var partB = Path.Combine(b, "Same.PAR");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(partA, "a");
    File.WriteAllText(partB, "b");
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [
            new AssemblyOccurrence("A", null, partA, false, false, false, identity, null),
            new AssemblyOccurrence("B", null, partB, false, false, false, identity, null),
        ],
        [partA, partB],
        0, 0, 2, 0, []);
    var plan = AssemblyPlanner.Create(probe);
    True(!plan.CanConvert, "同名不同路径零件必须阻止转换");
    True(plan.BlockingIssues.Any(issue => issue.ErrorClass == ConversionErrorClass.DuplicateOutputName),
        "同名冲突必须归类为 DuplicateOutputName");
}

static void TestAssemblyRetryReuse(string root)
{
    var directory = Path.Combine(root, "assembly-retry");
    var xtDirectory = Path.Combine(directory, "XT");
    var swDirectory = Path.Combine(directory, "SW");
    Directory.CreateDirectory(xtDirectory);
    Directory.CreateDirectory(swDirectory);
    var assembly = Path.Combine(directory, "Top.asm");
    File.WriteAllText(assembly, "asm");

    var sourceTime = DateTime.UtcNow.AddMinutes(-5);
    var reuseSw = CreatePartJob("reuse-sw", "ReuseSw");
    var reuseXt = CreatePartJob("reuse-xt", "ReuseXt");
    var fresh = CreatePartJob("fresh", "Fresh");
    File.WriteAllText(reuseSw.SolidWorksPath, "solidworks-part");
    File.SetLastWriteTimeUtc(reuseSw.SolidWorksPath, sourceTime.AddMinutes(2));
    WriteValidXt(reuseXt.XtPath);
    File.SetLastWriteTimeUtc(reuseXt.XtPath, sourceTime.AddMinutes(2));

    var plan = AssemblyPartReusePlanner.Create([reuseSw, reuseXt, fresh], CancellationToken.None);
    Equal(1, plan.ReusableSolidWorksParts.Count, "已有有效 SLDPRT 必须直接复用");
    Equal("reuse-sw", plan.ReusableSolidWorksParts.Single().Id, "SLDPRT 复用任务分类错误");
    Equal(1, plan.ImportFromExistingXt.Count, "只有有效 XT 时必须跳过 SE 导出并进入 SW 导入");
    Equal("reuse-xt", plan.ImportFromExistingXt.Single().Id, "XT 复用任务分类错误");
    Equal(1, plan.NeedsExport.Count, "没有产物的零件必须走完整转换");
    Equal(0, plan.RegeneratedForRecognition.Count, "未开启识别时不得把已有 SLDPRT 判为需重做");

    // 现场事故（307 实例 / 54 零件）：`SW\` 目录里是上一轮的哑实体 SLDPRT，
    // 复用判据不看识别开关，54 个零件全被跳过，用户开了识别却一个特征都没有。
    // 已有 SLDPRT 是否含特征，不打开文档无从判断，因此开启识别时一律重做。
    var recognizePlan = AssemblyPartReusePlanner.Create(
        [reuseSw, reuseXt, fresh], CancellationToken.None, recognizeFeatures: true);
    Equal(0, recognizePlan.ReusableSolidWorksParts.Count, "开启识别时不得复用已有 SLDPRT，否则识别永远不会发生");
    Equal(1, recognizePlan.RegeneratedForRecognition.Count, "开启识别时已有 SLDPRT 必须记为需重做");
    Equal("reuse-sw", recognizePlan.RegeneratedForRecognition.Single().Id, "需重做任务分类错误");
    Equal(1, recognizePlan.ImportFromExistingXt.Count, "已有 XT 的零件仍只复用 XT，不重跑 Solid Edge 导出");
    // reuse-sw 只有 SLDPRT、没有 XT：重做时无源可导入，必须回到完整导出。
    Equal(2, recognizePlan.NeedsExport.Count, "重做的零件没有 XT 时必须回到完整导出");

    // 同一零件既有 SLDPRT 又有 XT 时，重做只需重跑导入与识别，不必再过 Solid Edge。
    var bothArtifacts = CreatePartJob("both", "Both");
    File.WriteAllText(bothArtifacts.SolidWorksPath, "solidworks-part");
    File.SetLastWriteTimeUtc(bothArtifacts.SolidWorksPath, sourceTime.AddMinutes(2));
    WriteValidXt(bothArtifacts.XtPath);
    File.SetLastWriteTimeUtc(bothArtifacts.XtPath, sourceTime.AddMinutes(2));
    var bothPlan = AssemblyPartReusePlanner.Create(
        [bothArtifacts], CancellationToken.None, recognizeFeatures: true);
    Equal(1, bothPlan.RegeneratedForRecognition.Count, "已有 SLDPRT 必须记为需重做");
    Equal(1, bothPlan.ImportFromExistingXt.Count, "重做时有 XT 就复用 XT，只重跑导入与识别");
    Equal(0, bothPlan.NeedsExport.Count, "有可用 XT 时不得重跑 Solid Edge 导出");

    // 落盘闸门：默认绝不覆盖用户产物；只有调用方明确要求重做时才放行。
    // 现场事故：请求里 Overwrite=true，但 Commit 写死 false，53 个零件全部止于
    // 「当文件已存在时，无法创建该文件」。
    var commitTarget = Path.Combine(directory, "commit-target.SLDPRT");
    File.WriteAllText(commitTarget, "old");
    var commitTemp = TemporaryOutput.For(commitTarget);
    File.WriteAllText(commitTemp, "new");
    Throws<IOException>(() => TemporaryOutput.Commit(commitTemp, commitTarget));
    TemporaryOutput.Commit(commitTemp, commitTarget, overwrite: true);
    Equal("new", File.ReadAllText(commitTarget), "放行覆盖后必须真正落到最终路径");

    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var request = new AssemblyBatchRequest(
        "retry-smoke",
        ConversionMode.External,
        assembly,
        Path.Combine(swDirectory, "Top.SLDASM"),
        [reuseSw, reuseXt, fresh],
        [
            new AssemblyOccurrence("A", null, reuseSw.SourcePath, false, false, false, identity, null),
            new AssemblyOccurrence("B", null, reuseXt.SourcePath, false, false, false, identity, null),
            new AssemblyOccurrence("C", null, fresh.SourcePath, false, false, false, identity, null),
        ]);
    PreflightValidator.ValidateAssemblyRequest(request);
    WorkerRequestValidator.Validate(request);
    File.WriteAllText(request.AssemblyOutputPath, "existing-assembly");
    Throws<IOException>(() => PreflightValidator.ValidateAssemblyRequest(request));
    Throws<IOException>(() => WorkerRequestValidator.Validate(request));

    ConversionJob CreatePartJob(string id, string name)
    {
        var source = Path.Combine(directory, name + ".par");
        File.WriteAllText(source, "part");
        File.SetLastWriteTimeUtc(source, sourceTime);
        return new ConversionJob(
            id,
            source,
            Path.Combine(xtDirectory, name + ".x_t"),
            Path.Combine(swDirectory, name + ".SLDPRT"));
    }
}

static void TestAssemblyRetryRejectsUnsafeOutputs(string root)
{
    var directory = Path.Combine(root, "assembly-retry-invalid");
    Directory.CreateDirectory(directory);
    var sourceTime = DateTime.UtcNow.AddMinutes(-2);

    var emptySw = CreateJob("empty-sw");
    File.WriteAllBytes(emptySw.SolidWorksPath, []);
    File.SetLastWriteTimeUtc(emptySw.SolidWorksPath, sourceTime.AddMinutes(1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([emptySw], CancellationToken.None));

    var staleSw = CreateJob("stale-sw");
    File.WriteAllText(staleSw.SolidWorksPath, "old-part");
    File.SetLastWriteTimeUtc(staleSw.SolidWorksPath, sourceTime.AddMinutes(-1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([staleSw], CancellationToken.None));

    var invalidXt = CreateJob("invalid-xt");
    File.WriteAllText(invalidXt.XtPath, "not-parasolid");
    File.SetLastWriteTimeUtc(invalidXt.XtPath, sourceTime.AddMinutes(1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([invalidXt], CancellationToken.None));

    var staleXt = CreateJob("stale-xt");
    WriteValidXt(staleXt.XtPath);
    File.SetLastWriteTimeUtc(staleXt.XtPath, sourceTime.AddMinutes(-1));
    Throws<ClassifiedConversionException>(() =>
        AssemblyPartReusePlanner.Create([staleXt], CancellationToken.None));

    ConversionJob CreateJob(string name)
    {
        var source = Path.Combine(directory, name + ".par");
        File.WriteAllText(source, "part");
        File.SetLastWriteTimeUtc(source, sourceTime);
        return new ConversionJob(
            name,
            source,
            Path.Combine(directory, name + ".x_t"),
            Path.Combine(directory, name + ".SLDPRT"));
    }
}

static void TestAssemblyTemplateFallback(string root)
{
    var programData = Path.Combine(root, "template-fallback");
    var templates = Path.Combine(programData, "SOLIDWORKS", "SOLIDWORKS 2025", "templates");
    Directory.CreateDirectory(templates);
    var standard = Path.Combine(templates, "gb_assembly.asmdot");
    var secondary = Path.Combine(templates, "z_assembly.asmdot");
    File.WriteAllText(standard, "standard");
    File.WriteAllText(secondary, "secondary");

    var fallback = SolidWorksAssemblyTemplateResolver.FindCandidates(
        "~BLANK_ASSY_TEMPLATE.asmdot",
        installDirectory: null,
        commonApplicationData: programData);
    Equal(standard, fallback.First(), "虚拟默认模板必须回退到标准物理 gb_assembly.asmdot");

    var configured = Path.Combine(programData, "configured.asmdot");
    File.WriteAllText(configured, "configured");
    var configuredFirst = SolidWorksAssemblyTemplateResolver.FindCandidates(
        configured,
        installDirectory: null,
        commonApplicationData: programData);
    Equal(configured, configuredFirst.First(), "有效的当前默认模板必须保持最高优先级");
}

static void WriteValidXt(string path)
    => File.WriteAllText(
        path,
        "**ABCDEFGHIJKLMNOPQRSTUVWXYZ**;\r\nFORMAT=text;\r\nmodeller version SCH_3101255_31100_1300;\r\n");

static void TestAssemblyMatrixMapping()
{
    var world = new[]
    {
        0d, -1, 0, 0,
        1, 0, 0, 0,
        0, 0, 1, 0,
        0.4, -0.5, 0.6, 1,
    };
    var mapped = SolidWorksAssemblyBuilder.ToSolidWorksTransform(world);
    Equal(16, mapped.Length, "SolidWorks MathTransform 必须是 16 元素");
    Equal(-1d, mapped[1], "旋转矩阵不得转置");
    Equal(1d, mapped[3], "旋转矩阵不得转置");
    Equal(0.4d, mapped[9], "平移 X 必须从 SE[12] 映射到 SW[9]");
    Equal(-0.5d, mapped[10], "平移 Y 必须从 SE[13] 映射到 SW[10]");
    Equal(0.6d, mapped[11], "平移 Z 必须从 SE[14] 映射到 SW[11]");
    Equal(1d, mapped[12], "SolidWorks 变换缩放必须为 1");
}

static void TestAssemblyTree()
{
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        @"C:\fixture\Top.asm",
        [
            new AssemblyOccurrence("Sub:1", null, @"C:\fixture\Sub.asm", true, false, false, identity, null),
            new AssemblyOccurrence("Sub:1/Part:1", "Sub:1", @"C:\fixture\Part.par", false, false, false, identity, null),
        ],
        [@"C:\fixture\Part.par"],
        0, 0, 1, 0, []);
    var rootNode = AssemblyTreeNode.Build(probe);
    Equal(1, rootNode.Children.Count, "装配树应有一个顶层子装配");
    Equal(1, rootNode.Children[0].Children.Count, "ParentId 必须还原真实子层级");
    Equal("Part:1", rootNode.Children[0].Children[0].DisplayName, "树节点应显示 occurrence 名称");
}

// ---- V3.3 装配嵌套：装配图构建 ----------------------------------------------

/// <summary>16 元素行主序矩阵，旋转为单位阵，平移放在 12..14，与 V3.0 的口径一致。</summary>
static double[] Translation(double x, double y, double z) =>
[
    1, 0, 0, 0,
    0, 1, 0, 0,
    0, 0, 1, 0,
    x, y, z, 1,
];

static AssemblyChild Part(string name, string path, double x, double y, double z)
    => new(name, path, false, false, Translation(x, y, z));

static AssemblyChild Sub(string name, string path, double x, double y, double z)
    => new(name, path, true, false, Translation(x, y, z));

static string SwOutput(string assemblyPath)
    => Path.Combine(@"C:\fixture\SW", Path.GetFileNameWithoutExtension(assemblyPath) + ".SLDASM");

/// <summary>三层嵌套：拓扑序必须是子级在前、顶层在最后，且深度逐层递增。</summary>
static void TestAssemblyGraphTopology()
{
    var graph = AssemblyGraphBuilder.Build(
        @"C:\fixture\Top.asm",
        [
            new AssemblyDocumentReading(@"C:\fixture\Top.asm",
                [Part("P1:1", @"C:\fixture\P1.par", 0, 0, 0), Sub("Mid:1", @"C:\fixture\Mid.asm", 0, 0.238, 0)], []),
            new AssemblyDocumentReading(@"C:\fixture\Mid.asm",
                [Part("P2:1", @"C:\fixture\P2.par", 0, 0, 0), Sub("Leaf:1", @"C:\fixture\Leaf.asm", 0, 0.244, 0)], []),
            new AssemblyDocumentReading(@"C:\fixture\Leaf.asm",
                [Part("P3:1", @"C:\fixture\P3.par", 0, -0.03, 0)], []),
        ],
        SwOutput);

    True(graph.IsValid, "三层装配图应当有效");
    Equal(3, graph.Nodes.Count, "三层嵌套应产出三个装配节点");
    Equal(3, graph.MaxDepth, "最大层数应为 3");
    Equal("Leaf.asm", Path.GetFileName(graph.Nodes[0].SourceAssemblyPath), "拓扑序必须把最深的子装配排在最前");
    Equal("Mid.asm", Path.GetFileName(graph.Nodes[1].SourceAssemblyPath), "中间层排在叶层之后");
    Equal("Top.asm", Path.GetFileName(graph.Nodes[2].SourceAssemblyPath), "顶层必须最后生成——父级要插入子级的 .SLDASM 文件");
    True(graph.Nodes[2].IsRoot, "顶层节点必须标为 IsRoot");
    True(!graph.Nodes[0].IsRoot, "子装配不得标为 IsRoot");
    Equal(1, graph.Nodes[2].Depth, "顶层深度为 1");
    Equal(3, graph.Nodes[0].Depth, "最深子装配深度为 3");
    Equal(@"C:\fixture\SW\Mid.SLDASM", graph.Nodes[1].OutputPath, "输出路径由调用方的解析器决定");

    // 依赖闭包用于 §3.7 的复用过期判定：任一后代更新，产物即过期。
    var rootDependencies = graph.Nodes[2].Dependencies;
    Equal(5, rootDependencies.Count, "顶层依赖闭包应含全部后代文件");
    True(rootDependencies.Any(path => path.EndsWith("P3.par", StringComparison.OrdinalIgnoreCase)),
        "闭包必须穿透到第三层的叶零件");
}

/// <summary>同一子装配被多处引用只生成一次；被多次引用的实例由各自的父级插入矩阵承担姿态。</summary>
static void TestAssemblyGraphSharedAndRepeated()
{
    var graph = AssemblyGraphBuilder.Build(
        @"C:\fixture\Top.asm",
        [
            new AssemblyDocumentReading(@"C:\fixture\Top.asm",
                [
                    Sub("Shared:1", @"C:\fixture\Shared.asm", 0, 0, 0),
                    Sub("Shared:2", @"C:\fixture\Shared.asm", 0.1, 0, 0),
                    Sub("Mid:1", @"C:\fixture\Mid.asm", 0, 0.5, 0),
                ], []),
            new AssemblyDocumentReading(@"C:\fixture\Mid.asm",
                [Sub("Shared:1", @"C:\fixture\Shared.asm", 0, 0.05, 0)], []),
            new AssemblyDocumentReading(@"C:\fixture\Shared.asm",
                [Part("P:1", @"C:\fixture\P.par", 0, 0, 0)], []),
        ],
        SwOutput);

    True(graph.IsValid, "跨层复用的装配图应当有效");
    Equal(3, graph.Nodes.Count, "同一子装配被三处引用仍只生成一个节点");
    Equal(1, graph.Nodes.Count(node => Path.GetFileName(node.SourceAssemblyPath) == "Shared.asm"),
        "跨层复用不产生副本节点");

    var names = graph.Nodes.Select(node => Path.GetFileName(node.SourceAssemblyPath)).ToList();
    var shared = graph.Nodes.Single(node => Path.GetFileName(node.SourceAssemblyPath) == "Shared.asm");
    Equal(3, shared.Depth, "被多处引用时深度取最深的一条路径");
    True(names.IndexOf("Shared.asm") < names.IndexOf("Mid.asm"), "共享子装配必须排在它的每个父级之前");
    True(names.IndexOf("Mid.asm") < names.IndexOf("Top.asm"), "中间层必须排在顶层之前");

    var top = graph.Nodes.Single(node => node.IsRoot);
    Equal(3, top.Children.Count, "顶层的两个 Shared 实例各占一个组件位");
    Equal(0.1, top.Children[1].LocalTransform[12], "重复实例的姿态由父级的插入矩阵承担，互不干扰");
}

/// <summary>成环必须被截断并报错，引用缺失必须单列。</summary>
static void TestAssemblyGraphCycleAndMissing()
{
    var cyclic = AssemblyGraphBuilder.Build(
        @"C:\fixture\A.asm",
        [
            new AssemblyDocumentReading(@"C:\fixture\A.asm", [Sub("B:1", @"C:\fixture\B.asm", 0, 0, 0)], []),
            new AssemblyDocumentReading(@"C:\fixture\B.asm", [Sub("A:1", @"C:\fixture\A.asm", 0, 0, 0)], []),
        ],
        SwOutput);
    True(!cyclic.IsValid, "成环的装配图不可转换");
    Equal(1, cyclic.Cycles.Count, "应报出一条环路");
    True(cyclic.Cycles[0].Contains("A.asm") && cyclic.Cycles[0].Contains("B.asm"), "环路文案要列出参与的文档");
    Equal(0, cyclic.Nodes.Count, "成环时不得产出任何节点");

    var missing = AssemblyGraphBuilder.Build(
        @"C:\fixture\Top.asm",
        [
            new AssemblyDocumentReading(@"C:\fixture\Top.asm", [Sub("Gone:1", @"C:\fixture\Gone.asm", 0, 0, 0)], []),
        ],
        SwOutput);
    True(!missing.IsValid, "缺读数的装配图不可转换");
    Equal(1, missing.MissingDocuments.Count, "缺失的子装配文档必须单列");

    var noRoot = AssemblyGraphBuilder.Build(@"C:\fixture\Top.asm", [], SwOutput);
    True(!noRoot.IsValid, "连顶层读数都没有时不可转换");
    Equal(1, noRoot.MissingDocuments.Count, "缺顶层读数应报为缺失文档");
}

/// <summary>
/// 逐层复合验算：子装配内组件的世界位置 = 各级局部矩阵依次复合。
/// 这条锁死 §6.1 第 1 条的算法，用纯数值验，不依赖 CAD。数据取自真实三层样件。
/// </summary>
static void TestAssemblyGraphLocalTransformComposition()
{
    var graph = AssemblyGraphBuilder.Build(
        @"C:\fixture\风滚子.asm",
        [
            new AssemblyDocumentReading(@"C:\fixture\风滚子.asm",
                [Sub("测试装配1:1", @"C:\fixture\测试装配1.asm", 0, 0.238, 0)], []),
            new AssemblyDocumentReading(@"C:\fixture\测试装配1.asm",
                [Sub("测试装配3:1", @"C:\fixture\测试装配3.asm", 0.0007, 0.244, -0.025)], []),
            new AssemblyDocumentReading(@"C:\fixture\测试装配3.asm",
                [Part("测试零件6:1", @"C:\fixture\测试零件6.par", 0, -0.03, 0)], []),
        ],
        SwOutput);

    True(graph.IsValid, "真实三层样件的装配图应当有效");

    // 纯平移链，逐级相加即可；旋转参与时用矩阵乘，此处只验参考系口径。
    var world = new double[3];
    foreach (var node in graph.Nodes.OrderBy(item => item.Depth))
    {
        var child = node.Children[0];
        world[0] += child.LocalTransform[12];
        world[1] += child.LocalTransform[13];
        world[2] += child.LocalTransform[14];
    }

    // 探针实测：测试零件6 在顶层世界系下的 Y = 0.452。
    True(Math.Abs(world[1] - 0.452) < 1e-9, $"三层复合后的世界 Y 应为 0.452，实得 {world[1]}");
    True(Math.Abs(world[0] - 0.0007) < 1e-9, "三层复合后的世界 X 应为 0.0007");
}

/// <summary>
/// 带旋转的两层复合。子装配绕 Z 轴转 90°，叶零件在子装配里沿 +X 偏 0.1。
/// 正确的复合结果是世界 (0, 0.338, 0)——那 0.1 被旋进了 +Y。
/// 若把"复合"错写成平移相加，会得到 (0.1, 0.238, 0)，本用例当场失败。
/// </summary>
static void TestAssemblyTransformVerifierWithRotation()
{
    double[] subLocal =
    [
        0, 1, 0, 0,
        -1, 0, 0, 0,
        0, 0, 1, 0,
        0, 0.238, 0, 1,
    ];
    double[] leafLocal = Translation(0.1, 0, 0);
    double[] leafWorld =
    [
        0, 1, 0, 0,
        -1, 0, 0, 0,
        0, 0, 1, 0,
        0, 0.338, 0, 1,
    ];

    var probe = MakeProbe(subLocal, leafLocal, subWorld: subLocal, leafWorld: leafWorld);
    var result = AssemblyTransformVerifier.Verify(probe);
    Equal(2, result.CheckedCount, "两层各一个实例，应核验两条");
    True(result.IsConsistent, $"带旋转的复合应当一致，最大偏差 {result.MaxDeviation}");
    True(result.MaxDeviation <= AssemblyTransformVerifier.Tolerance, "偏差必须在 1e-9 以内");
}

/// <summary>参考系用错时必须当场超差——这正是本版唯一的静默错误源。</summary>
static void TestAssemblyTransformVerifierCatchesWrongFrame()
{
    double[] subLocal = Translation(0, 0.238, 0);
    double[] leafLocal = Translation(0, 0.244, 0);
    // 叶零件的世界矩阵被写成了它的局部矩阵——典型的"拿局部当世界"错误。
    var probe = MakeProbe(subLocal, leafLocal, subWorld: subLocal, leafWorld: leafLocal);

    var result = AssemblyTransformVerifier.Verify(probe);
    True(!result.IsConsistent, "参考系用错必须被检出");
    Equal(1, result.Mismatches.Count, "应当只有叶零件那一条超差");
    True(Math.Abs(result.MaxDeviation - 0.238) < 1e-9, $"偏差应等于漏掉的父级平移，实得 {result.MaxDeviation}");
}

static AssemblyProbeResult MakeProbe(double[] subLocal, double[] leafLocal, double[] subWorld, double[] leafWorld)
    => new(
        @"C:\fixture\Top.asm",
        [
            new AssemblyOccurrence("Sub:1", null, @"C:\fixture\Sub.asm", true, false, false, subWorld, null),
            new AssemblyOccurrence("Sub:1/Leaf:1", "Sub:1", @"C:\fixture\Leaf.par", false, false, false, leafWorld, null),
        ],
        [@"C:\fixture\Leaf.par"],
        0, 0, 1, 0, [],
        [
            new AssemblyDocumentReading(@"C:\fixture\Top.asm",
                [new AssemblyChild("Sub:1", @"C:\fixture\Sub.asm", true, false, subLocal)], []),
            new AssemblyDocumentReading(@"C:\fixture\Sub.asm",
                [new AssemblyChild("Leaf:1", @"C:\fixture\Leaf.par", false, false, leafLocal)], []),
        ]);

/// <summary>三层真实结构走完整规划：节点、输出落位、装配树标注、请求校验。</summary>
static void TestAssemblyPlannerNesting(string root)
{
    var directory = Path.Combine(root, "assembly-nested");
    Directory.CreateDirectory(directory);
    string F(string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    var top = F("Top.asm");
    var mid = F("Mid.asm");
    var leaf = F("Leaf.asm");
    var partA = F("A.par");
    var partB = F("B.par");

    var probe = new AssemblyProbeResult(
        top,
        [
            new AssemblyOccurrence("A:1", null, partA, false, false, false, Translation(0, 0, 0), null),
            new AssemblyOccurrence("Mid:1", null, mid, true, false, false, Translation(0, 0.238, 0), null),
            new AssemblyOccurrence("Mid:1/Leaf:1", "Mid:1", leaf, true, false, false, Translation(0, 0.482, 0), null),
            new AssemblyOccurrence("Mid:1/Leaf:1/B:1", "Mid:1/Leaf:1", partB, false, false, false, Translation(0, 0.452, 0), null),
        ],
        [partA, partB],
        0, 0, 2, 0, [],
        [
            new AssemblyDocumentReading(top,
                [Part("A:1", partA, 0, 0, 0), Sub("Mid:1", mid, 0, 0.238, 0)], []),
            new AssemblyDocumentReading(mid, [Sub("Leaf:1", leaf, 0, 0.244, 0)], []),
            new AssemblyDocumentReading(leaf, [Part("B:1", partB, 0, -0.03, 0)], []),
        ]);

    var plan = AssemblyPlanner.Create(probe);
    True(plan.CanConvert, "三层装配应通过规划门禁：" + string.Join("；", plan.BlockingIssues.Select(i => i.Message)));
    True(plan.IsNested, "有逐文档读数时必须走嵌套");
    Equal(3, plan.Nodes!.Count, "三层应产出三个装配节点");
    Equal(2, plan.SubAssemblyCount, "顶层之外还有两个子装配");
    Equal(3, plan.MaxDepth, "最大层数为 3");
    Equal(Path.Combine(directory, "SW", "Leaf.SLDASM"), plan.Nodes[0].OutputPath, "子装配产物必须落在 SW 目录");
    True(plan.Nodes[^1].IsRoot, "拓扑序最后一个必须是顶层");
    True(plan.Warnings.Any(item => item.Contains("嵌套装配体", StringComparison.Ordinal)),
        "必须告知用户本版按层级生成，而不是展平");
    True(!plan.Warnings.Any(item => item.Contains("展平", StringComparison.Ordinal)),
        "嵌套模式下不得再出现 V3.0 的展平文案");

    var tree = AssemblyTreeNode.Build(probe, plan.Nodes);
    var midNode = tree.Children.Single(node => node.DisplayName == "Mid:1");
    True(midNode.StateText.Contains("生成 Mid.SLDASM", StringComparison.Ordinal),
        $"子装配节点要标注它生成哪个文件，实得：{midNode.StateText}");
    Equal(1, midNode.Children.Count, "装配树必须保留真实层级");

    // 请求校验：节点输出逐个查，顶层必须恰好一个。
    Directory.CreateDirectory(Path.Combine(directory, "XT"));
    Directory.CreateDirectory(Path.Combine(directory, "SW"));
    var request = new AssemblyBatchRequest(
        "batch", ConversionMode.External, top, plan.AssemblyOutputPath,
        plan.Parts.Select(p => new ConversionJob(p.SourcePath, p.SourcePath, p.XtPath, p.SolidWorksPath)).ToArray(),
        plan.Occurrences, Nodes: plan.Nodes);
    PreflightValidator.ValidateAssemblyRequest(request);
    Throws<InvalidDataException>(() => PreflightValidator.ValidateAssemblyRequest(
        request with { Nodes = plan.Nodes.Select(n => n with { IsRoot = true }).ToArray() }));
}

/// <summary>成环与参考系错乱都必须在碰 CAD 之前被拦下。</summary>
static void TestAssemblyPlannerRejectsBrokenGraph(string root)
{
    var directory = Path.Combine(root, "assembly-broken");
    Directory.CreateDirectory(directory);
    string F(string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    var top = F("Top.asm");
    var sub = F("Sub.asm");
    var part = F("P.par");

    var cyclic = new AssemblyProbeResult(
        top,
        [
            new AssemblyOccurrence("Sub:1", null, sub, true, false, false, Translation(0, 0, 0), null),
            new AssemblyOccurrence("Sub:1/P:1", "Sub:1", part, false, false, false, Translation(0, 0, 0), null),
        ],
        [part], 0, 0, 1, 0, [],
        [
            new AssemblyDocumentReading(top, [Sub("Sub:1", sub, 0, 0, 0)], []),
            new AssemblyDocumentReading(sub, [Sub("Top:1", top, 0, 0, 0), Part("P:1", part, 0, 0, 0)], []),
        ]);
    var cyclicPlan = AssemblyPlanner.Create(cyclic);
    True(!cyclicPlan.CanConvert, "成环必须阻断转换");
    True(cyclicPlan.BlockingIssues.Any(issue => issue.ErrorClass == ConversionErrorClass.SubAssemblyCycleDetected),
        "成环必须报 SubAssemblyCycleDetected");

    // 叶零件的世界矩阵故意漏掉父级平移——典型的"拿局部当世界"。
    var wrongFrame = new AssemblyProbeResult(
        top,
        [
            new AssemblyOccurrence("Sub:1", null, sub, true, false, false, Translation(0, 0.238, 0), null),
            new AssemblyOccurrence("Sub:1/P:1", "Sub:1", part, false, false, false, Translation(0, 0.244, 0), null),
        ],
        [part], 0, 0, 1, 0, [],
        [
            new AssemblyDocumentReading(top, [Sub("Sub:1", sub, 0, 0.238, 0)], []),
            new AssemblyDocumentReading(sub, [Part("P:1", part, 0, 0.244, 0)], []),
        ]);
    var wrongPlan = AssemblyPlanner.Create(wrongFrame);
    True(!wrongPlan.CanConvert, "参考系不一致必须阻断转换");
    True(wrongPlan.BlockingIssues.Any(issue => issue.ErrorClass == ConversionErrorClass.ComponentTransformFailed),
        "参考系不一致必须报 ComponentTransformFailed");
}

/// <summary>§3.7：装配产物必须比它递归依赖的每一个文件都新，否则拒绝复用。</summary>
static void TestAssemblyNodeReuse(string root)
{
    var directory = Path.Combine(root, "assembly-reuse");
    Directory.CreateDirectory(directory);
    var source = Path.Combine(directory, "Sub.asm");
    var dependency = Path.Combine(directory, "Dep.par");
    var output = Path.Combine(directory, "Sub.SLDASM");
    File.WriteAllText(source, "asm");
    File.WriteAllText(dependency, "par");

    var node = new AssemblyNode(source, output, false, 2,
        [Part("Dep:1", dependency, 0, 0, 0)], [dependency]);

    True(!AssemblyNodeReusePlanner.CanReuse(node), "产物不存在时必须重新生成");

    File.WriteAllText(output, "sldasm");
    var future = DateTime.UtcNow.AddMinutes(5);
    File.SetLastWriteTimeUtc(output, future);
    True(AssemblyNodeReusePlanner.CanReuse(node), "产物比全部依赖都新时可以复用");

    // V3.6.6：判不出"可安全复用"时一律**重做**，不再抛异常要求用户移走旧产物。
    // 原策略防的是"输出目录混着来历不明的同名 SLDASM"，那是冒烟环境的假设；
    // 真实用法是转换到空目录，代价却是 UI 默认开着重建配合时连重跑一次都做不到。
    True(
        !AssemblyNodeReusePlanner.CanReuse(node, requireMateRebuild: true),
        "需要重建配合时必须重做，不能复用证明不了配合来历的旧产物");

    // .asm 没动，但里面的零件改了——V3.0 的"比源文件新"规则会漏掉这种情况。
    File.SetLastWriteTimeUtc(dependency, future.AddMinutes(1));
    True(!AssemblyNodeReusePlanner.CanReuse(node), "产物早于任一依赖时必须重做");

    File.SetLastWriteTimeUtc(dependency, future.AddMinutes(-1));
    File.WriteAllText(output, string.Empty);
    File.SetLastWriteTimeUtc(output, future);
    True(!AssemblyNodeReusePlanner.CanReuse(node), "空产物必须重做");
}

static void TestNestedAssemblyMetrics()
{
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var reusedNode = new AssemblyNode(
        @"C:\fixture\Reused.asm",
        @"C:\fixture\Reused.SLDASM",
        false,
        2,
        [
            new AssemblyChild("A:1", @"C:\fixture\A.par", false, false, identity),
            new AssemblyChild("B:1", @"C:\fixture\B.par", false, false, identity),
        ],
        []);
    var builtNode = new AssemblyNode(
        @"C:\fixture\Top.asm",
        @"C:\fixture\Top.SLDASM",
        true,
        1,
        [new AssemblyChild("Reused:1", reusedNode.SourceAssemblyPath, true, false, identity)],
        [reusedNode.SourceAssemblyPath]);

    var metrics = new NestedAssemblyBuildMetrics(skippedSuppressed: 3);
    metrics.AddPlannedNode(reusedNode);
    metrics.AddReusedNode(reusedNode);
    metrics.AddPlannedNode(builtNode);
    metrics.AddBuiltNode(inserted: 1, fixedCount: 1);

    Equal(3, metrics.ComponentTotal, "嵌套组件总数必须覆盖构建与复用节点的计划直接子项");
    Equal(1, metrics.ComponentInserted, "复用节点不得伪造为本次已插入组件");
    Equal(1, metrics.ComponentFixed, "复用节点不得伪造为本次已固定组件");
    Equal(1, metrics.ReusedAssemblyCount, "复用装配数必须单列");
    Equal(2, metrics.ReusedAssemblyPlannedComponentCount, "复用装配内的计划直接子项必须单列为非实测数据");
    Equal(3, metrics.SkippedSuppressed, "嵌套路径的抑制件数不得静默归零");

    var outcome = new AssemblyOutcome(
        metrics.ComponentTotal,
        metrics.ComponentInserted,
        metrics.ComponentFixed,
        0,
        0,
        metrics.SkippedSuppressed,
        0,
        0,
        null,
        ReusedAssemblyCount: metrics.ReusedAssemblyCount,
        ReusedAssemblyPlannedComponentCount: metrics.ReusedAssemblyPlannedComponentCount);
    var json = JsonSerializer.Serialize(outcome, WorkerProtocol.CreateJsonOptions());
    var roundTrip = JsonSerializer.Deserialize<AssemblyOutcome>(json, WorkerProtocol.CreateJsonOptions());
    Equal(1, roundTrip?.ComponentInserted, "实测插入计数必须可经 Worker JSON 往返");
    Equal(1, roundTrip?.ReusedAssemblyCount, "复用装配计数必须可经 Worker JSON 往返");
    Equal(2, roundTrip?.ReusedAssemblyPlannedComponentCount, "复用装配计划组件数必须可经 Worker JSON 往返");
    Equal(3, roundTrip?.SkippedSuppressed, "抑制件数必须可经 Worker JSON 往返");
}

static void TestAssemblyModeValidationText()
{
    var request = new AssemblyBatchRequest(
        "mode-smoke",
        ConversionMode.Ohs,
        @"C:\fixture\Top.asm",
        @"C:\fixture\SW\Top.SLDASM",
        [],
        []);
    var preflight = Capture<InvalidDataException>(() => PreflightValidator.ValidateAssemblyRequest(request));
    var worker = Capture<InvalidDataException>(() => WorkerRequestValidator.Validate(request));
    True(preflight.Message.Contains("当前装配转换", StringComparison.Ordinal), "UI 预检不得继续暴露 V3.0 过时文案");
    True(worker.Message.Contains("当前装配转换", StringComparison.Ordinal), "Worker 预检不得继续暴露 V3.0 过时文案");
}

// ---- V3.5 装配关系重建 -------------------------------------------------------

static RelationGeometry Plane(double px, double py, double pz, double nx, double ny, double nz)
    => new(MateGeometryMatcher.GeometryPlane, [px, py, pz], [nx, ny, nz]);

static RelationGeometry Axis(double px, double py, double pz, double dx, double dy, double dz)
    => new(MateGeometryMatcher.GeometryAxis, [px, py, pz], [dx, dy, dz]);

static MateCandidate Cand(string key, int kind, double px, double py, double pz, double dx, double dy, double dz)
    => new(key, kind, [px, py, pz], [dx, dy, dz]);

/// <summary>§3.3：唯一命中才接受；0 个与多个都必须当场判定失败，绝不猜。</summary>
static void TestMateGeometryMatching()
{
    // 同一个平面可以由面上任意一点表达；法向反向仍是同一平面。
    var target = Plane(0, 0.130, 0, 0, -1, 0);
    var candidates = new[]
    {
        Cand("faceA", 1, 0.5, 0.130, -0.2, 0, 1, 0),      // 同一平面，法向反了
        Cand("faceB", 1, 0, 0.131, 0, 0, 1, 0),           // 平行但差 1mm
        Cand("faceC", 1, 0, 0.130, 0, 1, 0, 0),           // 过同一点但法向垂直
        Cand("cylD", 2, 0, 0.130, 0, 0, 1, 0),            // 类型不同
    };
    var matched = MateGeometryMatcher.Match(target, candidates);
    Equal(MateMatchStatus.Matched, matched.Status, "法向反向的同一平面必须命中");
    Equal("faceA", matched.Candidate!.Key, "命中的应当是共面的那个");

    var unmatched = MateGeometryMatcher.Match(target, [candidates[1], candidates[2]]);
    Equal(MateMatchStatus.Unmatched, unmatched.Status, "没有共面候选时必须判为未匹配");
    Equal(ConversionErrorClass.MateEntityUnmatched, unmatched.ErrorClass!.Value, "未匹配要映射到专用错误类");

    // 轴：实测数据（19 号报告 §4 第 4 条关系），两点仅 Y 不同、方向 ±Y，共线。
    var axis = Axis(-0.04, 0.1327, -0.04, 0, 1, 0);
    var axisMatch = MateGeometryMatcher.Match(axis,
    [
        Cand("cyl1", 2, -0.04, 0.1255, -0.04, 0, -1, 0),   // 同一条轴，方向反
        Cand("cyl2", 2, -0.04, 0.1255, 0.04, 0, 1, 0),     // 平行但不共线
    ]);
    Equal(MateMatchStatus.Matched, axisMatch.Status, "共线的轴必须命中");
    Equal("cyl1", axisMatch.Candidate!.Key, "命中的应当是共线的那条轴");

    // 容差边界：1e-6 内算在面上，超出即不算。
    True(MateGeometryMatcher.IsPointOnPlane([0, 9e-7, 0], [0, 0, 0], [0, 1, 0]), "9e-7 应在容差内");
    True(!MateGeometryMatcher.IsPointOnPlane([0, 1.1e-6, 0], [0, 0, 0], [0, 1, 0]), "1.1e-6 应超出容差");
    True(!MateGeometryMatcher.IsParallel([0, 1, 0], [0, 0, 0]), "零向量不得判为平行");
}

/// <summary>§3.4 映射表，重点是条件可读那条陷阱。</summary>
/// <summary>
/// 实测教训（25 号文档 §5.2）：SolidWorks 会把一个几何面切成多块拓扑面——
/// 一个通孔 2~3 个圆柱面、一个大平面多块共面面。首轮实测 106 侧里 43 侧因此被误判为歧义，
/// 命中率被压到 37.7%；改为等价性判定后是 100%。
///
/// 这几条用例锁住"多候选不等于歧义"，同时保住"真不等价才判歧义"的安全网。
/// </summary>
static void TestMateCandidateEquivalence()
{
    var target = Plane(0, 0.130, 0, 0, -1, 0);

    // 同一平面被切成三块：法向有同有反，都必须归为同一实体。
    var split = MateGeometryMatcher.Match(target,
    [
        Cand("f2", 1, 0.5, 0.130, -0.2, 0, 1, 0),
        Cand("f1", 1, -0.3, 0.130, 0.4, 0, -1, 0),
        Cand("f3", 1, 0.1, 0.130, 0.1, 0, -1, 0),
    ]);
    Equal(MateMatchStatus.Matched, split.Status, "同一平面被切成多块不是歧义");
    Equal(3, split.CandidateKeys.Count, "候选仍要全部报出，便于排查");
    Equal("f1", split.Candidate!.Key, "优先法向同向，再按 Key 取序——同样输入必须永远同样输出");

    // 同轴的多个圆柱（通孔被切开、沉孔多段），半径不同也是同一条轴。
    var axis = Axis(0, 0, 0, 0, 0, 1);
    var coaxial = MateGeometryMatcher.Match(axis,
        [Cand("cyl2", 2, 0, 0, 0.05, 0, 0, 1), Cand("cyl1", 2, 0, 0, -0.02, 0, 0, -1)]);
    Equal(MateMatchStatus.Matched, coaxial.Status, "同轴的多个圆柱面不是歧义");

    // 安全网：真不等价的候选仍必须判歧义。两个平行但相距 10mm 的平面不可能同时通过筛选，
    // 所以直接验等价性判定本身。
    True(!MateGeometryMatcher.AreEquivalent(
            Cand("a", 1, 0, 0, 0, 0, 1, 0),
            Cand("b", 1, 0, 0.010, 0, 0, 1, 0)),
        "平行但不共面的两个面不得判为等价");
    True(!MateGeometryMatcher.AreEquivalent(
            Cand("a", 2, 0, 0, 0, 0, 0, 1),
            Cand("b", 2, 0.010, 0, 0, 0, 0, 1)),
        "平行但不共线的两条轴不得判为等价");
    True(!MateGeometryMatcher.AreEquivalent(
            Cand("a", 1, 0, 0, 0, 0, 1, 0),
            Cand("b", 2, 0, 0, 0, 0, 1, 0)),
        "类型不同不得判为等价");
    True(!MateGeometryMatcher.AreAllEquivalent(
        [Cand("a", 1, 0, 0, 0, 0, 1, 0), Cand("b", 1, 0, 0.010, 0, 0, 1, 0)]),
        "只要有一对不等价，整组就不等价");
}

static void TestMateTypeMapping()
{
    AssemblyRelation R(string kind) => new(@"C:\T.asm", 1, kind, "A:1", "B:1", null, null);

    Equal(MatePlanKind.Fix, MateTypeMapper.Map(R(MateTypeMapper.Ground)).Kind, "接地关系映射为固定");

    // 对齐一律 CLOSEST：组件已精确位于源位置，最近解就是"不动"。
    // 显式传 Aligned/AntiAligned 的真机教训是求解器按指定方向翻转组件（180°，旋转偏差恒为 2）。
    var coincident = MateTypeMapper.Map(R(MateTypeMapper.Planar) with { NormalsAligned = true });
    Equal(SolidWorksMateType.Coincident, coincident.MateType, "零偏移平面关系映射为重合");
    Equal(SolidWorksMateAlign.Closest, coincident.Align, "对齐必须是 CLOSEST，不得按 NormalsAligned 强指方向");

    var distance = MateTypeMapper.Map(R(MateTypeMapper.Planar) with { Offset = 0.003, NormalsAligned = false });
    Equal(SolidWorksMateType.Distance, distance.MateType, "带偏移的平面关系映射为距离");
    Equal(SolidWorksMateAlign.Closest, distance.Align, "距离配合同样 CLOSEST");
    True(Math.Abs(distance.Distance - 0.003) < 1e-12, "距离取 Offset 绝对值");

    // 这条是本组的重点：ParallelOffset=false 时 Offset 根本读不出来、留的是默认 0。
    // 若实现改用"Offset 是否为 0"推断类型，同轴关系会被误判成零距离配合。
    var concentric = MateTypeMapper.Map(R(MateTypeMapper.Axial) with { ParallelOffset = false, Offset = 0 });
    Equal(SolidWorksMateType.Concentric, concentric.MateType, "ParallelOffset=false 必须映射为同轴，不能看 Offset");
    var axialDistance = MateTypeMapper.Map(R(MateTypeMapper.Axial) with { ParallelOffset = true, Offset = 0.012 });
    Equal(SolidWorksMateType.Distance, axialDistance.MateType, "ParallelOffset=true 才是距离");

    True(!MateTypeMapper.CanReadAxialOffset(false), "ParallelOffset=false 时不得去读 Offset");
    True(!MateTypeMapper.CanReadRange(false), "RangedOffset=false 时不得去读 RangeLow/High");

    var unsupported = MateTypeMapper.Map(R("TangentRelation3d"));
    Equal(MatePlanKind.Unsupported, unsupported.Kind, "未实测的类型一律不翻译");
    True(unsupported.Reason!.Contains("TangentRelation3d", StringComparison.Ordinal),
        "不支持的原因里要带上接口名，据此决定下一版补哪个");

    Equal(MatePlanKind.SkipSuppressed,
        MateTypeMapper.Map(R(MateTypeMapper.Planar) with { IsSuppressed = true }).Kind,
        "被抑制的关系在 SE 里没生效，不翻译");
}

/// <summary>§6.1 判据 3：每条关系都要有确定去向，不允许凭空消失。</summary>
static void TestMateOutcomeSelfConsistency()
{
    var consistent = new MateOutcome(56, 37, 2, 4, 6, 3, 1, 5, 4.2e-7, [], GroundApplied: 3);
    True(consistent.IsSelfConsistent, "37+3+2+4+6+3+1 应等于 56——接地关系也要有去向");
    True(!(consistent with { MateRebuilt = 36 }).IsSelfConsistent, "少算一条必须被检出");

    var json = JsonSerializer.Serialize(consistent);
    var roundTrip = JsonSerializer.Deserialize<MateOutcome>(json)!;
    True(roundTrip.IsSelfConsistent, "JSON 往返后计数仍须自洽");

    var relation = new AssemblyRelation(@"C:\T.asm", 3, MateTypeMapper.Axial, "A:1", "B:1",
        Axis(0, 0.1, 0, 0, 1, 0), Axis(0, 0.2, 0, 0, -1, 0), ParallelOffset: false);
    var relationRoundTrip = JsonSerializer.Deserialize<AssemblyRelation>(JsonSerializer.Serialize(relation))!;
    Equal(3, relationRoundTrip.Geometry1!.Point.Length, "几何点必须 3 元素往返");
    Equal(MateGeometryMatcher.GeometryAxis, relationRoundTrip.Geometry2!.GeometryType, "几何类型必须往返");
}

/// <summary>
/// 特征识别的几何守卫。真实事故：FeatureWorks 只认出基体拉伸时，CreateFeatures 照样返回 true，
/// 零件被重建成一个方块存盘——管线此前从不校验几何，用户看到的是"形状全错但一切正常"。
/// </summary>
static void TestRecognitionGeometryGuard()
{
    // 一致：体积逐位相同，面数变化不影响判定（识别可能合并共面面）。
    Equal(null, FeatureRecognizer.DescribeGeometryMismatch((1.234e-4, 32), (1.234e-4, 30)),
        "体积一致时不得判为几何改变——面数合并是识别的正常行为");

    // 方块事故：体积掉了一大截。
    var boxed = FeatureRecognizer.DescribeGeometryMismatch((1.0e-4, 55), (2.5e-4, 6));
    True(boxed is not null, "体积变化必须被检出");
    True(boxed!.Contains("体积", StringComparison.Ordinal) && boxed.Contains("面数", StringComparison.Ordinal),
        $"诊断要给出前后数值便于排查，实得：{boxed}");

    // 已验证的 FeatureWorks 金标准会产生 1.418e-5 的重建偏差，守卫必须放行但保持有界。
    Equal(null, FeatureRecognizer.DescribeGeometryMismatch((1.0, 10), (1.0 + 1.5e-5, 10)),
        "已验证的 FeatureWorks 重建偏差不得误判为几何破坏");
    True(FeatureRecognizer.DescribeGeometryMismatch((1.0, 10), (1.0 + 2.1e-5, 10)) is not null,
        "超过金标准上限的体积偏差必须判为几何改变");
    True(FeatureRecognizer.DescribeGeometryMismatch((1.0, 10), (1.0001, 10)) is not null,
        "万分之一的体积偏差就必须判为几何改变——这不是噪声");

    // 量不到就是不安全，绝不默认放行。
    True(FeatureRecognizer.DescribeGeometryMismatch((1.0e-4, 20), null) is not null,
        "识别后读不出几何必须判为不安全");
    Equal(null, FeatureRecognizer.DescribeGeometryMismatch(null, (1.0e-4, 20)),
        "识别前就没有基准时，本判据不兜底，交给其他环节");

    // 契约：GeometryChanged 与 DegradedToDumbSolid 语义不同，不能混用。
    var outcome = new FeatureOutcome(3, true, 0, 0, [], true, 10, "几何被改变", GeometryChanged: true);
    var roundTrip = JsonSerializer.Deserialize<FeatureOutcome>(JsonSerializer.Serialize(outcome))!;
    True(roundTrip.GeometryChanged, "GeometryChanged 必须能 JSON 往返——Worker 靠它决定要不要重新导入");
    True(!new FeatureOutcome(0, false, 0, 0, [], true, 10, "没识别出来").GeometryChanged,
        "普通降级默认不得标记几何被改变");
}

/// <summary>
/// 两道守卫，都来自真实事故：
///   · 导入身份——FeatureWorks 服务器故障后 LoadFile4 交回上一件的文档，
///     5 个零件被存成同一个方块（体积与面数逐位相同）；
///   · 会话故障——死掉的 COM 对象不会自愈，不识别出来就会对着它重试到批次结束。
/// </summary>
static void TestImportIdentityAndSessionFaultGuards()
{
    // 身份正确：SolidWorks 导入 XT 后的标题是"基名.sldprt"。
    Equal(null, SolidWorksImporter.DescribeImportIdentityFailure(@"C:\x\XT\测试零件5.x_t", "测试零件5.sldprt"),
        "标题与 XT 基名一致时不得报错");
    Equal(null, SolidWorksImporter.DescribeImportIdentityFailure(@"C:\x\XT\零件1.x_t", "零件1"),
        "无扩展名的标题同样算一致");

    // 事故现场：导入 测试零件5，SolidWorks 交回 测试零件4。
    var wrong = SolidWorksImporter.DescribeImportIdentityFailure(@"C:\x\XT\测试零件5.x_t", "测试零件4.sldprt");
    True(wrong is not null, "交回别的文档必须被检出——否则会把错误几何存成本零件");
    True(wrong!.Contains("测试零件5", StringComparison.Ordinal) && wrong.Contains("测试零件4", StringComparison.Ordinal),
        $"诊断要同时给出期望与实得，实得：{wrong}");

    True(SolidWorksImporter.DescribeImportIdentityFailure(@"C:\x\XT\A.x_t", null) is not null,
        "拿不到标题就无法证明身份，必须判失败");

    // 会话故障的 HRESULT 识别。
    True(FeatureRecognizer.IsServerFault(new InvalidOperationException("x") { HResult = unchecked((int)0x80010105) }),
        "RPC_E_SERVERFAULT 必须识别为会话故障");
    True(FeatureRecognizer.IsServerFault(new InvalidOperationException("x") { HResult = unchecked((int)0x80010108) }),
        "RPC_E_DISCONNECTED 必须识别为会话故障");
    True(!FeatureRecognizer.IsServerFault(new InvalidOperationException("普通失败")),
        "普通异常不得误判为会话故障——否则会白白重建会话");

    // 契约：标志语义互不相同，不能相互替代。
    var faulted = new FeatureOutcome(0, false, 0, 0, [], true, 5, "服务器出现意外情况", SessionFaulted: true);
    var roundTrip = JsonSerializer.Deserialize<FeatureOutcome>(JsonSerializer.Serialize(faulted))!;
    True(roundTrip.SessionFaulted && !roundTrip.GeometryChanged,
        "SessionFaulted 要能往返，且不得牵连 GeometryChanged");
    True(new FeatureOutcome(0, false, 0, 0, [], true, 5, "没识别出来") is { SessionFaulted: false, GeometryChanged: false },
        "普通降级默认两个标志都不置位");
}

static void TestUiModuleRegistration()
{
    var registrar = new RecordingShellUiRegistrar();
    var module = new SE2SWUiModule { ShellUi = registrar };
    module.CreateUi();
    Equal(1, registrar.Descriptors.Count, "模块应只注册一个单页工具窗口");
    var descriptor = registrar.Descriptors.Single();
    Equal("se2sw", descriptor.Id, "必须保留稳定窗口 ID se2sw");
    Equal("SE2SW 转换", descriptor.Title, "窗口标题应覆盖零件与装配两种转换");
    True(descriptor.ContentFactory != null, "单页工具窗口必须提供内容工厂");
    Equal(DockSide.Right, descriptor.DefaultSide, "窗口应保持 AppShell 普通右侧工具窗口语义");
    module.DestroyUi();
    Equal(1, registrar.DisposeCount, "热卸载必须释放双页窗口句柄");
}

static void TestUnifiedSourceWorkspace()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var workspace = new SE2SWWorkspaceView();
            Equal(ConversionSourceKind.None, workspace.UnifiedPage.ViewModel.SourceKind,
                "单页工作区默认必须等待用户选择来源");
            True(workspace.UnifiedPage.ViewModel.SourcePath.Length == 0,
                "未选择来源时不能残留旧路径");
            Equal("转换装配体", workspace.UnifiedPage.ViewModel.PrimaryActionText,
                "未选择来源时主按钮使用装配体占位文案");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
        throw new InvalidOperationException("单页来源工作区 Smoke 失败。", failure);
}

static void TestRecognitionSemanticGuard()
{
    Equal(0x3F, FeatureRecognizer.StandardPartRecognitionOptions,
        "普通零件识别参数必须包含六类机械特征");
    Equal(FeatureRecognizer.VolumeRecognitionOption,
        FeatureRecognizer.StandardPartRecognitionOptions & FeatureRecognizer.VolumeRecognitionOption,
        "普通零件自动识别必须选中体积特征");
    Equal(0, FeatureRecognizer.StandardPartRecognitionOptions & FeatureRecognizer.SheetMetalRecognitionOptions,
        "普通零件识别参数严禁混入钣金四位");
    Equal(2e-5, FeatureRecognizer.MaximumFeatureWorksVolumeRelativeDeviation,
        "几何守卫必须覆盖已验证的 FeatureWorks 重建误差且保持有界");

    var mixedImported = FeatureRecognizer.DescribeSemanticMismatch(
        2, true,
        [new FeatureTreeEntry("Imported1", "BaseBody"), new FeatureTreeEntry("Boss-Extrude1", "Extrusion")]);
    True(mixedImported is not null && mixedImported.Contains("不等价于手工识别", StringComparison.Ordinal),
        "混合树仍含未识别导入体，不得把部分识别冒充完整成功");

    var sheetMetal = FeatureRecognizer.DescribeSemanticMismatch(
        2, true,
        [new FeatureTreeEntry("Sheet-Metal1", "SheetMetal"), new FeatureTreeEntry("Imported1", "BaseBody")]);
    True(sheetMetal is not null && sheetMetal.Contains("钣金", StringComparison.Ordinal),
        "普通 .par 出现钣金特征必须判为语义错误");

    var zeroCountSheetSideEffect = FeatureRecognizer.DescribeSemanticMismatch(
        0, false,
        [new FeatureTreeEntry("Sheet<10>", "CutListFolder"), new FeatureTreeEntry("Imported10", "BaseBody")]);
    True(zeroCountSheetSideEffect is not null && zeroCountSheetSideEffect.Contains("钣金", StringComparison.Ordinal),
        "RecognizeFeatureAutomatic 返回 0 时产生的钣金树副作用也必须触发干净重导入");
    Equal(null, FeatureRecognizer.DescribeSemanticMismatch(
        0, false,
        [new FeatureTreeEntry("Imported1", "BaseBody")]),
        "返回 0 且仍是原始导入体时沿用普通未识别降级");

    var importedOnly = FeatureRecognizer.DescribeSemanticMismatch(
        1, true,
        [new FeatureTreeEntry("Origin", "OriginProfileFeature"), new FeatureTreeEntry("Imported1", "BaseBody")]);
    True(importedOnly is not null && importedOnly.Contains("导入体", StringComparison.Ordinal),
        "返回成功但仍只有导入体时不得报告识别成功");

    True(FeatureRecognizer.DescribeSemanticMismatch(1, true, []) is not null,
        "成功后无法枚举特征树必须安全降级");
    Equal(null, FeatureRecognizer.DescribeSemanticMismatch(0, false, []),
        "未识别/未创建由既有失败分支处理，不重复标记语义错误");

    var outcome = new FeatureOutcome(
        2, true, 0, 0, [], true, 10, "钣金误识别", SemanticMismatch: true);
    var roundTrip = JsonSerializer.Deserialize<FeatureOutcome>(JsonSerializer.Serialize(outcome))!;
    True(roundTrip.SemanticMismatch && !roundTrip.GeometryChanged && !roundTrip.SessionFaulted,
        "SemanticMismatch 必须独立 JSON 往返");

    True(SolidWorksImporter.ShouldReimportRejectedRecognition(roundTrip),
        "语义错误必须触发关闭错误文档并从 XT 重新导入");
    Equal(ConversionErrorClass.FeatureRecognitionSemanticMismatch,
        SolidWorksImporter.ClassifyRejectedRecognition(roundTrip),
        "语义错误必须保留独立分类，不得退化为普通创建失败");
    Equal(ConversionErrorClass.FeatureRecognitionSemanticMismatch,
        SolidWorksImporter.ClassifyCompletedFeatureOutcome(roundTrip),
        "哑实体成功保存后仍须如实报告此前的语义错误");

    var geometryChanged = outcome with { SemanticMismatch = false, GeometryChanged = true };
    True(SolidWorksImporter.ShouldReimportRejectedRecognition(geometryChanged),
        "几何错误仍须沿用既有的干净重导入保护");
    Equal(ConversionErrorClass.FeatureCreationFailed,
        SolidWorksImporter.ClassifyRejectedRecognition(geometryChanged),
        "几何错误与语义错误必须保持不同分类");
    True(!SolidWorksImporter.ShouldReimportRejectedRecognition(null),
        "未执行识别时不得额外重导入");
}

static void TestUnifiedPartDirectoryFlow(string root)
{
    var directory = Path.Combine(root, "unified-parts");
    var childDirectory = Path.Combine(directory, "child");
    Directory.CreateDirectory(childDirectory);
    var partA = Path.Combine(directory, "A.par");
    var partB = Path.Combine(directory, "B.PAR");
    var partC = Path.Combine(directory, "C.par");
    var nestedPart = Path.Combine(childDirectory, "Nested.par");
    File.WriteAllText(partA, "a");
    File.WriteAllText(partB, "b");
    File.WriteAllText(partC, "c");
    File.WriteAllText(nestedPart, "nested");

    var assemblyDirectory = Path.Combine(root, "unified-assembly");
    Directory.CreateDirectory(assemblyDirectory);
    var assembly = Path.Combine(assemblyDirectory, "Top.asm");
    var assemblyPart = Path.Combine(assemblyDirectory, "AssemblyPart.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(assemblyPart, "part");
    var probeCount = 0;
    var partRunCount = 0;
    List<BatchRequest> capturedRequests = [];
    var probe = new AssemblyProbeResult(
        assembly,
        [new AssemblyOccurrence("AssemblyPart:1", null, assemblyPart, false, false, false, Translation(0, 0, 0), null)],
        [assemblyPart],
        0, 0, 1, 0, []);

    using var viewModel = new AssemblyViewModel(
        (_, _, _) =>
        {
            probeCount++;
            return Task.FromResult(probe);
        },
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher,
        (request, progress, _) =>
        {
            partRunCount++;
            capturedRequests.Add(request);
            foreach (var job in request.Jobs)
            {
                if (partRunCount == 1 && string.Equals(
                        Path.GetFileName(job.SourcePath), "C.par", StringComparison.OrdinalIgnoreCase))
                {
                    progress(new WorkerEvent(request.BatchId, job.Id, ConversionStage.Failed, "模拟失败", IsError: true));
                    continue;
                }
                File.WriteAllText(job.SolidWorksPath, "converted");
                progress(new WorkerEvent(request.BatchId, job.Id, ConversionStage.Completed, "完成"));
            }
            return Task.FromResult(partRunCount == 1 ? 1 : 0);
        });

    viewModel.SetAssemblySource(assembly);
    viewModel.ProbeAsync().GetAwaiter().GetResult();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    Equal(1, probeCount, "一次装配来源选择只能执行一次显式探查");
    Equal(1, viewModel.AssemblyTree.Count, "装配探查应建立树");

    viewModel.ContinueWhenPartFails = true;
    viewModel.SetPartDirectory(directory);
    Equal(ConversionSourceKind.PartDirectory, viewModel.SourceKind, "选择文件夹后必须切换到零件来源");
    Equal(3, viewModel.Parts.Count, "文件夹模式只扫描顶层 .par");
    True(viewModel.Parts.All(row => !string.Equals(row.SourcePath, nestedPart, StringComparison.OrdinalIgnoreCase)),
        "文件夹模式不得递归扫描子目录");
    Equal(0, viewModel.AssemblyTree.Count, "切到文件夹必须清除旧装配树");
    True(!viewModel.ContinueWhenPartFails && !viewModel.RebuildMates,
        "文件夹模式必须关闭装配专属失败策略与关系重建");
    True(!viewModel.CanContinueWhenPartFails && !viewModel.CanRebuildMates,
        "文件夹模式必须禁用装配专属选项");
    Equal("转换全部零件", viewModel.PrimaryActionText, "文件夹模式主按钮文案不应提装配");
    True(!Directory.Exists(Path.Combine(directory, "XT")) && !Directory.Exists(Path.Combine(directory, "SW")),
        "扫描阶段不得创建输出目录");

    Directory.CreateDirectory(Path.Combine(directory, "XT"));
    File.WriteAllText(Path.Combine(directory, "XT", "A.x_t"), "existing");
    viewModel.SetPartDirectory(directory);
    True(viewModel.Parts.Single(row => row.FileName == "A.par").HasExistingOutput,
        "已有 XT 的零件必须标记为已存在");
    True(viewModel.CanConvert, "仍有未转换零件时必须允许批量转换");

    viewModel.ConvertAsync().GetAwaiter().GetResult();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    Equal(1, partRunCount, "全部零件批次只能启动一次 Worker");
    var firstRequest = capturedRequests.Single();
    True(firstRequest is { Mode: ConversionMode.External, Overwrite: false },
        "文件夹批次必须沿用外界模式且禁止覆盖");
    Equal(2, firstRequest.Jobs.Count, "已有产物必须从批次中摘除，其余零件一次送入 Worker");
    True(firstRequest.Jobs.Select(job => Path.GetFileName(job.SourcePath))
            .SequenceEqual(["B.PAR", "C.par"], StringComparer.OrdinalIgnoreCase),
        "批次只应包含全部尚无产物的顶层零件");
    True(!firstRequest.RecognizeFeatures && !firstRequest.FullyDefineSketches,
        "识别特征和完全定义草图必须默认关闭");
    True(viewModel.Parts.Single(row => row.FileName == "B.PAR").HasExistingOutput,
        "部分失败后成功项必须在重扫中更新为已存在");
    True(!viewModel.Parts.Single(row => row.FileName == "C.par").HasExistingOutput && viewModel.CanConvert,
        "部分失败后无产物项必须保持可重试");

    viewModel.ConvertAsync().GetAwaiter().GetResult();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    Equal(2, partRunCount, "失败项重试应再启动一次 Worker");
    Equal(1, capturedRequests[1].Jobs.Count, "重试批次不得重复发送成功项");
    Equal("C.par", Path.GetFileName(capturedRequests[1].Jobs[0].SourcePath),
        "重试批次只能包含上次失败项");
    True(viewModel.Parts.All(row => row.HasExistingOutput),
        "重试成功后必须重扫并把全部项目更新为已存在");
    True(!viewModel.CanConvert, "全部已有产物后不得重复转换");

    viewModel.SetAssemblySource(assembly);
    True(!viewModel.CanConvert && viewModel.Parts.Count == 0 && viewModel.AssemblyTree.Count == 0,
        "从文件夹切回装配必须清空零件结果和旧计划");
    Equal(ConversionSourceKind.Assembly, viewModel.SourceKind, "来源状态必须回到装配体");

    var empty = Path.Combine(root, "unified-empty");
    Directory.CreateDirectory(empty);
    viewModel.SetPartDirectory(empty);
    Equal(0, viewModel.Parts.Count, "空文件夹必须得到空清单");
    True(!viewModel.CanConvert && viewModel.StatusText.Contains("未找到", StringComparison.Ordinal),
        "空文件夹应禁用转换并给出明确状态");
}

/// <summary>
/// V3.5 界面层：开关必须由"有没有关系"决定，配合结果必须真的走到用户眼前。
/// 承诺是"建不起来的如实报告"——报告只进日志、用户看不见的话，承诺就没兑现。
/// </summary>
static void TestAssemblyMateSwitchAndReport(string root)
{
    var directory = Path.Combine(root, "assembly-mate-ui");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var sub = Path.Combine(directory, "Sub.asm");
    var part = Path.Combine(directory, "P.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(sub, "sub");
    File.WriteAllText(part, "part");

    AssemblyProbeResult Probe(IReadOnlyList<AssemblyRelation>? relations) => new(
        assembly,
        [
            new AssemblyOccurrence("Sub:1", null, sub, true, false, false, Translation(0, 0, 0), null),
            new AssemblyOccurrence("Sub:1/P:1", "Sub:1", part, false, false, false, Translation(0, 0, 0), null),
        ],
        [part], 0, 0, 1, 0, [],
        [
            new AssemblyDocumentReading(assembly, [Sub("Sub:1", sub, 0, 0, 0)], []),
            new AssemblyDocumentReading(sub, [Part("P:1", part, 0, 0, 0)], [], relations),
        ]);

    // 没有关系 → 开关禁用，提示说清为什么。
    var withoutRelations = new AssemblyViewModel(
        (_, _, _) => Task.FromResult(Probe(null)),
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);
    using (withoutRelations)
    {
        withoutRelations.SetSourceFile(assembly);
        withoutRelations.ProbeAsync().GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
        True(!withoutRelations.CanRebuildMates, "没有装配关系时不得允许勾选重建配合");
        True(withoutRelations.RebuildMatesHint.Contains("没有显式装配关系", StringComparison.Ordinal),
            $"提示要说清原因，实得：{withoutRelations.RebuildMatesHint}");
    }

    // 有关系 → 开关可用，提示带上条数；转换后摘要与逐条诊断都要露出来。
    var relation = new AssemblyRelation(sub, 2, MateTypeMapper.Planar, "P:1", "P:1",
        Plane(0, 0, 0, 0, 1, 0), Plane(0, 0, 0, 0, -1, 0));
    var outcome = new MateOutcome(
        RelationTotal: 4, MateRebuilt: 2, SkippedSuppressed: 0, SkippedUnsupported: 0,
        FailedUnmatched: 1, FailedAmbiguous: 0, FailedRejected: 0, ComponentsLeftFixed: 1,
        MaxDriftMeters: 3e-9, Diagnostics: ["#3 PlanarRelation3d：实体定位失败"], GroundApplied: 1);
    var withRelations = new AssemblyViewModel(
        (_, _, _) => Task.FromResult(Probe([relation])),
        (request, progress, _) =>
        {
            True(request.RebuildMates, "勾选后请求里必须带上 RebuildMates");
            True(request.Relations is { Count: > 0 }, "请求里必须带上采集到的关系");
            progress(new WorkerEvent("b", null, ConversionStage.Completed, "完成", Mate: outcome));
            return Task.FromResult(0);
        },
        static () => { },
        Dispatcher.CurrentDispatcher);
    using (withRelations)
    {
        withRelations.SetSourceFile(assembly);
        withRelations.ProbeAsync().GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
        True(withRelations.CanRebuildMates, "有装配关系时开关必须可用");
        True(withRelations.RebuildMatesHint.Contains("1 条", StringComparison.Ordinal),
            $"提示要带上关系条数，实得：{withRelations.RebuildMatesHint}");

        True(withRelations.RebuildMates, "解析到装配关系后必须默认开启重建，不能把核心语义藏成易漏选项");
        Directory.CreateDirectory(Path.Combine(directory, "XT"));
        Directory.CreateDirectory(Path.Combine(directory, "SW"));
        withRelations.ConvertAsync().GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

        True(withRelations.StatusText.Contains("配合重建 2/4", StringComparison.Ordinal),
            $"状态栏要报出配合数，实得：{withRelations.StatusText}");
        True(withRelations.StatusText.Contains("接地", StringComparison.Ordinal),
            "接地关系的去向也要说明，否则用户会以为丢了两条");
        True(withRelations.StatusText.Contains("1 条未建立", StringComparison.Ordinal),
            "建不起来的必须明说，不能只报成功数");
        True(withRelations.WarningSummary.Contains("实体定位失败", StringComparison.Ordinal),
            $"逐条诊断必须走到用户眼前，实得：{withRelations.WarningSummary}");
    }
}

static void TestAssemblyViewModelState(string root)
{
    var directory = Path.Combine(root, "assembly-view-model");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    var changedAssembly = Path.Combine(directory, "Changed.asm");
    var part = Path.Combine(directory, "Part.par");
    File.WriteAllText(assembly, "asm");
    File.WriteAllText(changedAssembly, "changed");
    File.WriteAllText(part, "part");
    var identity = new[]
    {
        1d, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    var probe = new AssemblyProbeResult(
        assembly,
        [new AssemblyOccurrence("Part:1", null, part, false, false, false, identity, null)],
        [part],
        0, 0, 1, 0, []);
    using var viewModel = new AssemblyViewModel(
        (_, _, _) => Task.FromResult(probe),
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);
    viewModel.SetSourceFile(assembly);
    True(viewModel.CanProbe && !viewModel.CanConvert, "选择 .asm 后只能先解析，不能直接转换");
    viewModel.ProbeAsync().GetAwaiter().GetResult();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    True(viewModel.CanConvert, "解析成功且前置检查通过后才允许转换");
    Equal(1, viewModel.Parts.Count, "装配窗口应显示唯一零件清单");
    Equal(1, viewModel.AssemblyTree.Count, "装配窗口应显示真实层级根节点");
    True(viewModel.WarningSummary.Contains("展平", StringComparison.Ordinal), "装配窗口必须明确提示最终输出展平");
    True(!viewModel.RecognizeFeatures, "装配模式 FeatureWorks 必须默认关闭");
    True(!Directory.Exists(Path.Combine(directory, "XT")) && !Directory.Exists(Path.Combine(directory, "SW")),
        "装配窗口解析完成也不得创建输出目录");

    viewModel.SetSourceFile(changedAssembly);
    True(!viewModel.CanConvert && viewModel.Parts.Count == 0 && viewModel.AssemblyTree.Count == 0,
        "切换源文件必须清空旧探查结果并禁用转换");
}

static void TestAssemblyActiveRunDisposal(string root)
{
    var directory = Path.Combine(root, "assembly-dispose-running");
    Directory.CreateDirectory(directory);
    var assembly = Path.Combine(directory, "Top.asm");
    File.WriteAllText(assembly, "asm");
    var workerStarted = new ManualResetEventSlim();
    var workerExited = new ManualResetEventSlim();
    var cancellationObserved = false;
    var viewModel = new AssemblyViewModel(
        async (_, _, cancellationToken) =>
        {
            workerStarted.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("不可达");
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                throw;
            }
            finally
            {
                await Task.Delay(100).ConfigureAwait(false);
                workerExited.Set();
            }
        },
        static (_, _, _) => Task.FromResult(0),
        static () => { },
        Dispatcher.CurrentDispatcher);
    viewModel.SetSourceFile(assembly);
    var run = viewModel.ProbeAsync();
    True(workerStarted.Wait(TimeSpan.FromSeconds(3)), "装配假 Worker 必须启动");
    var dispose = Task.Run(viewModel.Dispose);
    Thread.Sleep(25);
    True(!dispose.IsCompleted, "装配 ViewModel Dispose 必须等待 Worker 收束");
    True(dispose.Wait(TimeSpan.FromSeconds(3)), "装配 ViewModel 必须在取消完成后释放");
    True(cancellationObserved && workerExited.IsSet, "装配生命周期必须传播取消并等待 Worker 退出");
    True(run.IsCompletedSuccessfully, "Dispose 返回前装配操作任务必须完成");
    Throws<ObjectDisposedException>(() => viewModel.ProbeAsync().GetAwaiter().GetResult());
}

static void TestDuplicateOutputRejection(string root)
{
    var external = Path.Combine(root, "duplicates");
    Directory.CreateDirectory(external);
    var sourceA = Path.Combine(external, "A.par");
    var sourceB = Path.Combine(external, "B.par");
    File.WriteAllText(sourceA, "a");
    File.WriteAllText(sourceB, "b");
    var duplicateXt = Path.Combine(external, "same.x_t");
    var jobs = new[]
    {
        new ConversionJob("a", sourceA, duplicateXt, Path.Combine(external, "A.SLDPRT")),
        new ConversionJob("b", sourceB, duplicateXt, Path.Combine(external, "B.SLDPRT")),
    };
    Throws<InvalidDataException>(() => PreflightValidator.ValidateJobs(jobs, overwrite: false));
}

static void TestTemporaryOutput(string root)
{
    var finalPath = Path.Combine(root, "atomic.SLDPRT");
    var temporaryPath = TemporaryOutput.For(finalPath);
    True(Path.GetDirectoryName(temporaryPath) == root, "临时输出必须与正式输出同目录");
    True(temporaryPath.EndsWith(".tmp.SLDPRT", StringComparison.OrdinalIgnoreCase),
        "临时输出必须保留 CAD 可识别的最终扩展名");
    File.WriteAllText(temporaryPath, "stable-output");
    TemporaryOutput.Commit(temporaryPath, finalPath);
    True(File.Exists(finalPath) && !File.Exists(temporaryPath), "提交必须把临时输出原子移动到正式路径");
}

static void TestParasolidTextProbe(string root)
{
    var textPath = Path.Combine(root, "probe.x_t");
    File.WriteAllText(textPath, "**ABCDEFGHIJKLMNOPQRSTUVWXYZ**;\r\nFORMAT=text;\r\nmodeller version SCH_3101255_31100_1300;\r\n");
    var facts = FileProbe.VerifyParasolidText(textPath, CancellationToken.None, TimeSpan.FromSeconds(3));
    Equal("text", facts.ParasolidFormat, "必须识别 Parasolid 文本格式头");
    Equal("SCH_3101255_31100_1300", facts.ParasolidSchema, "必须提取 Parasolid schema");

    var binaryPath = Path.Combine(root, "probe-binary.x_t");
    File.WriteAllText(binaryPath, "FORMAT=binary;\r\n");
    Throws<InvalidDataException>(() =>
        FileProbe.VerifyParasolidText(binaryPath, CancellationToken.None, TimeSpan.FromSeconds(3)));
}

static void TestFeatureRecognitionRetries()
{
    var clears = 0;
    var recognitions = 0;
    var waits = 0;
    var recovered = FeatureRecognizer.RunRecognitionAttempts(
        () =>
        {
            clears++;
        },
        () => ++recognitions < 3 ? 0 : 12,
        () => waits++,
        CancellationToken.None);

    Equal(12, recovered.RecognizedFeatureCount, "异步就绪后应返回识别出的特征数");
    Equal(3, recovered.Attempts, "前两次返回 0 时应继续尝试首件识别");
    Equal(3, clears, "每次识别前都必须清空本地识别实体选择");
    Equal(2, waits, "返回 0 后应等待并泵送消息，上次成功后不再等待");

    clears = 0;
    waits = 0;
    var exhausted = FeatureRecognizer.RunRecognitionAttempts(
        () =>
        {
            clears++;
        },
        static () => 0,
        () => waits++,
        CancellationToken.None);

    Equal(0, exhausted.RecognizedFeatureCount, "达到重试上限后才允许按未识别降级");
    Equal(10, exhausted.Attempts, "识别重试必须有确定的上限");
    Equal(10, clears, "每次重试都必须清空本地识别实体选择");
    Equal(9, waits, "最后一次失败后不应再等待");
}

static void TestFeatureRecognitionSessionGuards()
{
    True(
        FeatureRecognizer.DocumentTitlesMatch("ImportedPart", "importedpart"),
        "活动文档标题比较应忽略大小写");
    True(
        !FeatureRecognizer.DocumentTitlesMatch("ImportedPart", "UserAssembly"),
        "不得把 FeatureWorks 命令发给用户原有文档");

    var empty = new FeatureOutcome(0, false, 0, 0, [], true, 100, "recognition returned 0");
    var success = new FeatureOutcome(4, true, 1, 1, [], false, 100);
    True(
        SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, true, empty),
        "首件首次识别为 0 时应重新导入一次");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 1, true, empty),
        "首件恢复只能执行一次");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(1, 0, true, empty),
        "后续零件识别为 0 时不得套用首件初始化恢复");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, false, empty),
        "FeatureWorks 不可用时重新导入没有意义");
    True(
        !SolidWorksImporter.ShouldRetryFirstRecognition(0, 0, true, success),
        "首件识别成功时不得重新导入");
}

static void TestActiveRunDisposal(string root)
{
    var external = Path.Combine(root, "dispose-running");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "Running.par"), "sample");

    var workerStarted = new ManualResetEventSlim();
    var workerExited = new ManualResetEventSlim();
    var cancellationObserved = false;
    var viewModel = new SE2SWViewModel(
        async (_, progress, cancellationToken) =>
        {
            workerStarted.Set();
            progress(new WorkerEvent(
                "smoke",
                null,
                ConversionStage.SolidEdgeExport,
                "queued progress"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                throw;
            }
            finally
            {
                await Task.Delay(100).ConfigureAwait(false);
                workerExited.Set();
            }
        },
        static () => { },
        Dispatcher.CurrentDispatcher);

    viewModel.SetMode(ConversionMode.External);
    viewModel.SetDirectory(external);
    var run = viewModel.StartAsync();
    True(workerStarted.Wait(TimeSpan.FromSeconds(3)), "假 Worker 必须启动");

    var dispose = Task.Run(viewModel.Dispose);
    Thread.Sleep(25);
    True(!dispose.IsCompleted, "Dispose 必须等待 Worker 收束，不能只发送取消");
    True(dispose.Wait(TimeSpan.FromSeconds(3)), "Dispose 必须在 Worker 收束后返回");
    True(cancellationObserved, "Dispose 必须取消活动批次");
    True(workerExited.IsSet, "Dispose 返回前 Worker 必须已经退出");
    True(run.IsCompletedSuccessfully, "Dispose 返回前 StartAsync 必须完成");
    Dispatcher.CurrentDispatcher.Invoke(static () => { });
    True(viewModel.StatusText != "queued progress", "Dispose 必须撤销尚未执行的 UI 进度回调");
    Throws<ObjectDisposedException>(() => viewModel.StartAsync().GetAwaiter().GetResult());
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}。Expected={expected}, Actual={actual}");
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected exception {typeof(T).Name}");
}

static T Capture<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }
    throw new InvalidOperationException($"Expected exception {typeof(T).Name}");
}

sealed class RecordingShellUiRegistrar : IShellUiRegistrar
{
    public List<ToolWindowDescriptor> Descriptors { get; } = [];
    public int DisposeCount { get; private set; }
    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Descriptors.Add(descriptor);
        return new CallbackDisposable(() => DisposeCount++);
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }
}

sealed class CallbackDisposable(Action callback) : IDisposable
{
    private Action? _callback = callback;

    public void Dispose()
        => Interlocked.Exchange(ref _callback, null)?.Invoke();
}
