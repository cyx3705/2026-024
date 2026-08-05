using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolidWorks.Interop.fworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksRecognizeProbe;

/// <summary>
/// V2.0 可实现性探针：.x_t -> 导入 -> FeatureWorks 特征识别 -> 每个草图完全定义 -> .SLDPRT。
/// 只回答"能不能做、代价多大"，不是生产实现。
/// </summary>
internal static class Program
{
    private const string ProgId = "SldWorks.Application";
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";
    private const string SolidWorksProcessName = "SLDWORKS";
    private const int AutomaticRecognitionSelectionMark = 8;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? input = null;
        string? output = null;
        bool visible = false;
        bool skipRecognize = false;
        bool skipFullyDefine = false;
        bool isolatedInstance = false;
        bool importDiagnosis = false;
        bool nativeRecognitionCommand = false;
        int? attachPid = null;
        int recognizeOptions = 0x3FF;
        int advancedOptions = (int)(fwAdvancedOptions_e.fwAdvAddConstraintsToSketch
                                  | fwAdvancedOptions_e.fwAdvAllowWizardHoleRecognition);
        short performanceOptions = 0;
        short createOptions = (short)fwFeatureCreationOptions_e.fwAddConstraintsToSketch;
        int recognitionPasses = 1;
        int[]? recognitionSequence = null;
        string? interactiveFeature = null;
        string selectionMode = "first-mark8";
        string? prepareFrom = null;
        string? inspect = null;
        string[]? expectedCoreSignature = null;
        int? exitOwnedPid = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input": input = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--visible": visible = true; break;
                case "--skip-recognize": skipRecognize = true; break;
                case "--skip-fully-define": skipFullyDefine = true; break;
                case "--isolated": isolatedInstance = true; break;
                case "--import-diagnosis": importDiagnosis = true; break;
                case "--native-recognition-command": nativeRecognitionCommand = true; break;
                case "--attach-pid": attachPid = int.Parse(args[++i]); break;
                case "--options": recognizeOptions = ParseInteger(args[++i]); break;
                case "--advanced-options": advancedOptions = ParseInteger(args[++i]); break;
                case "--performance-options": performanceOptions = checked((short)ParseInteger(args[++i])); break;
                case "--create-options": createOptions = checked((short)ParseInteger(args[++i])); break;
                case "--recognition-passes": recognitionPasses = int.Parse(args[++i]); break;
                case "--recognition-sequence":
                    recognitionSequence = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ParseInteger)
                        .ToArray();
                    break;
                case "--interactive-feature": interactiveFeature = args[++i]; break;
                case "--selection": selectionMode = args[++i].ToLowerInvariant(); break;
                case "--prepare-xt-from": prepareFrom = args[++i]; break;
                case "--inspect": inspect = args[++i]; break;
                case "--expect-core-signature":
                    expectedCoreSignature = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--exit-owned": exitOwnedPid = int.Parse(args[++i]); break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    return 2;
            }
        }

        if (exitOwnedPid is int expectedPid)
        {
            return ExitOwnedApplication(expectedPid);
        }

        if (inspect is not null)
        {
            return Inspect(inspect, attachPid, expectedCoreSignature);
        }

        if (input is null || output is null)
        {
            Console.Error.WriteLine(
                "用法: --input <abs .x_t> --output <abs .SLDPRT> "
                + "[--isolated] [--options <0x3F|63>] [--visible] [--skip-recognize] [--skip-fully-define]");
            return 2;
        }

        var result = new ProbeResult
        {
            Input = input,
            Output = output,
            IsolatedInstance = isolatedInstance,
            ImportDiagnosisRequested = importDiagnosis,
            NativeRecognitionCommandRequested = nativeRecognitionCommand,
            RecognitionOptions = recognizeOptions,
            RecognitionOptionsHex = $"0x{recognizeOptions:X}",
            RecognitionOptionNames = DescribeRecognitionOptions(recognizeOptions),
            AdvancedOptions = advancedOptions,
            PerformanceOptions = performanceOptions,
            CreateOptions = createOptions,
            RequestedRecognitionPasses = recognitionPasses,
            SelectionMode = selectionMode,
            RecognitionSequence = recognitionSequence ?? [],
            InteractiveFeatureType = interactiveFeature,
        };

        if (selectionMode is not ("none" or "first-mark0" or "first-mark8")
            || recognitionPasses is < 1 or > 10
            || recognitionSequence is { Length: 0 }
            || (interactiveFeature is not null && (recognitionSequence is not null || recognitionPasses != 1))
            || (nativeRecognitionCommand && (interactiveFeature is not null || recognitionSequence is not null))
            || (isolatedInstance && attachPid is not null))
        {
            Console.Error.WriteLine($"未知选择策略: {selectionMode}");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        ISldWorks? app = null;
        ModelDoc2? model = null;
        Process? ownedProcess = null;
        Process? attachedProcess = null;
        string? previousActiveTitle = null;
        int[] before = GetPids();

        try
        {
            if (File.Exists(output))
            {
                throw new InvalidOperationException($"输出已存在，拒绝覆盖：{output}");
            }

            Type type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new InvalidOperationException($"ProgID 未注册：{ProgId}");
            result.Clsid = type.GUID.ToString("B").ToUpperInvariant();

            if (attachPid is int existingPid)
            {
                attachedProcess = Process.GetProcessById(existingPid);
                app = BindByPid(attachedProcess, TimeSpan.FromSeconds(15))
                    ?? throw new TimeoutException($"无法按 PID {existingPid} 附着现有 SolidWorks 实例。");
                result.AttachedPid = existingPid;
                previousActiveTitle = (app.ActiveDoc as ModelDoc2)?.GetTitle();
                result.PreviousActiveDocTitle = previousActiveTitle;
            }
            else if (isolatedInstance)
            {
                var executable = ResolveSolidWorksExecutable(type)
                    ?? throw new InvalidOperationException("无法从 COM 注册解析 SLDWORKS.exe。");
                ownedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("启动隔离 SolidWorks 返回 null。");
                app = BindByPid(ownedProcess, TimeSpan.FromSeconds(120))
                    ?? throw new TimeoutException($"无法按 PID {ownedProcess.Id} 绑定隔离 SolidWorks 实例。");
                result.NewPids = [ownedProcess.Id];
                result.CreatedNewInstance = true;
            }
            else
            {
                app = (ISldWorks)Activator.CreateInstance(type)!;
            }
            result.TimingsMs["AppLaunch"] = Mark(sw);

            if (!isolatedInstance)
            {
                int[] after = GetPids();
                result.NewPids = after.Except(before).ToArray();
                result.CreatedNewInstance = result.NewPids.Length > 0;
            }
            result.PreexistingPids = before;

            if (attachPid is null)
                app.Visible = visible;
            result.SolidWorksRevision = app.RevisionNumber();

            // FeatureWorks registers an in-proc server as ".\\fworks\\fworks.dll".
            // Resolve that relative COM path against the SOLIDWORKS install root, not the
            // caller's project directory.
            var installedFeatureWorksDll = ResolveFeatureWorksDll();
            var featureWorksDirectory = installedFeatureWorksDll is null
                ? null
                : Path.GetDirectoryName(installedFeatureWorksDll);
            var solidWorksInstallDirectory = featureWorksDirectory is null
                ? null
                : Directory.GetParent(featureWorksDirectory)?.FullName;
            if (solidWorksInstallDirectory is not null)
            {
                System.Environment.CurrentDirectory = solidWorksInstallDirectory;
                result.Notes.Add("FeatureWorks COM 工作目录=" + solidWorksInstallDirectory);
            }

            // 一键完全定义示例里用的是 "Point1@Origin" 这种英文名。中文界面下要么改中文名，
            // 要么打开这个开关强制英文特征名。探针直接打开，并在结束时还原。
            bool oldEnglishNames = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames, true);
            result.OldEnglishFeatureNames = oldEnglishNames;

            bool old3Di = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect);
            result.Old3DInterconnect = old3Di;

            // ---- 阶段 0：先把 FeatureWorks 加载项挂起来 ----
            // 顺序有讲究：加载项在 Load 时才挂接文档事件，先导入再加载会看不到已打开的文档。
            object? fwObject = null;
            if (!skipRecognize)
            {
                fwObject = app.GetAddInObject(FeatureWorksProgId);
                if (fwObject is null)
                {
                    string? dll = ResolveFeatureWorksDll();
                    result.FeatureWorksDll = dll;
                    if (dll is not null && File.Exists(dll))
                    {
                        result.LoadAddInResult = app.LoadAddIn(dll);
                        fwObject = app.GetAddInObject(FeatureWorksProgId);
                        result.LoadedAddInOnDemand = fwObject is not null;
                    }
                }

                result.FeatureWorksAddInObtained = fwObject is not null;
                result.FeatureWorksObjectType = fwObject?.GetType().FullName;
                result.TimingsMs["LoadAddIn"] = Mark(sw);
            }

            // ---- 对照组：把一个原生 SLDPRT 先导出成 .x_t，用来区分
            //      "API 没接通" 和 "这个零件本来就识别不出特征"。
            if (prepareFrom is not null)
            {
                int openErrors = 0;
                int openWarnings = 0;
                var source = (ModelDoc2?)app.OpenDoc6(
                    prepareFrom,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings);

                if (source is null)
                {
                    throw new InvalidOperationException($"OpenDoc6 失败：{prepareFrom} errors={openErrors}");
                }

                int xtErrors = 0;
                int xtWarnings = 0;
                bool exported = source.Extension.SaveAs3(
                    input,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, null, ref xtErrors, ref xtWarnings);
                result.Notes.Add($"对照组导出 {Path.GetFileName(prepareFrom)} -> .x_t：{exported}，errors={xtErrors}");
                app.CloseDoc(source.GetTitle());
                Marshal.FinalReleaseComObject(source);
                result.TimingsMs["PrepareXt"] = Mark(sw);
            }

            // ---- 阶段 1：导入 .x_t ----
            int errors = 0;
            if (Path.GetExtension(input).Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase))
            {
                var warnings = 0;
                model = (ModelDoc2?)app.OpenDoc6(
                    input,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref errors,
                    ref warnings);
                result.InputKind = "SavedPart";
                result.LoadFileWarnings = warnings;
            }
            else
            {
                object? importData = app.GetImportFileData(input);
                result.ImportDataType = importData?.GetType().FullName ?? "(null)";
                model = app.LoadFile4(input, "r", importData, ref errors);
                result.InputKind = "Parasolid";
            }
            result.LoadFileErrors = errors;
            result.TimingsMs["Import"] = Mark(sw);

            if (model is null)
            {
                throw new InvalidOperationException($"LoadFile4 返回 null，errors={errors}");
            }

            // FeatureWorks 作用于 ActiveDoc。附着到用户已有会话时，导入的文档不一定是活动文档。
            int activateErrors = 0;
            app.ActivateDoc3(model.GetTitle(), false, 0, ref activateErrors);
            result.ActivateDocErrors = activateErrors;
            result.ActiveDocTitle = (app.ActiveDoc as ModelDoc2)?.GetTitle();

            if (importDiagnosis)
            {
                result.ImportDiagnosisResult = ((PartDoc)model).ImportDiagnosis(
                    CloseAllGaps: true,
                    RemoveFaces: true,
                    FixFaces: true,
                    Options: 0);
                PumpAndWait(300);
                result.TimingsMs["ImportDiagnosis"] = Mark(sw);
            }

            // A fresh isolated instance can report "add-in already loaded" before it exposes
            // the automation object. Production refreshes GetAddInObject after activating the
            // imported document, so the probe must do the same before declaring FeatureWorks
            // unavailable.
            if (!skipRecognize)
            {
                var refreshedFeatureWorks = app.GetAddInObject(FeatureWorksProgId);
                if (refreshedFeatureWorks is null)
                {
                    var dll = result.FeatureWorksDll ?? ResolveFeatureWorksDll();
                    if (dll is not null && File.Exists(dll))
                    {
                        result.FeatureWorksDll = dll;
                        var unloadResult = app.UnloadAddIn(dll);
                        PumpAndWait(500);
                        result.LoadAddInResult = app.LoadAddIn(dll);
                        for (var loadAttempt = 1; loadAttempt <= 20 && refreshedFeatureWorks is null; loadAttempt++)
                        {
                            PumpAndWait(500);
                            refreshedFeatureWorks = app.GetAddInObject(FeatureWorksProgId);
                            result.Notes.Add(
                                $"FeatureWorks 对象等待 {loadAttempt}/20：{refreshedFeatureWorks is not null}");
                        }
                        if (refreshedFeatureWorks is null)
                        {
                            foreach (var identifier in new[]
                                     {
                                         "{16B0AE52-0817-11D7-A7F8-0006299907FB}",
                                         "{7CF8CA03-1DCE-11D1-A89B-0020AF351FA9}",
                                         "FeatureWorksApp",
                                     })
                            {
                                refreshedFeatureWorks = app.GetAddInObject(identifier);
                                result.Notes.Add(
                                    $"GetAddInObject({identifier})={refreshedFeatureWorks is not null}");
                                if (refreshedFeatureWorks is not null)
                                    break;
                            }
                        }
                        result.Notes.Add(
                            $"活动文档后重载 FeatureWorks：UnloadAddIn={unloadResult}, "
                            + $"LoadAddIn={result.LoadAddInResult}, object={refreshedFeatureWorks is not null}");
                    }
                }

                if (refreshedFeatureWorks is not null)
                    fwObject = refreshedFeatureWorks;
                result.FeatureWorksAddInObtained = fwObject is not null;
                result.LoadedAddInOnDemand |= refreshedFeatureWorks is not null;
            }

            result.DocTypeAfterImport = ((swDocumentTypes_e)model.GetType()).ToString();
            result.BodyCountAfterImport = CountBodies(model);
            result.FeatureNamesAfterImport = ListFeatures(model);

            // ---- 阶段 2：FeatureWorks 特征识别 ----
            if (!skipRecognize)
            {
                if (fwObject is null)
                {
                    result.Notes.Add($"GetAddInObject(\"{FeatureWorksProgId}\") 返回 null —— 加载项未启用。");
                }
                else
                {
                    var refreshedFeatureWorks = app.GetAddInObject(FeatureWorksProgId);
                    if (refreshedFeatureWorks is not null)
                        fwObject = refreshedFeatureWorks;
                    var fw = (IFeatureWorksApp)fwObject;

                    // 建议：让 FeatureWorks 在生成特征时就给草图加几何关系，
                    // 后续 FullyDefineSketch 的工作量会小很多。
                    result.SetAdvancedOptions = fw.SetAdvancedOptions((short)advancedOptions);

                    result.SetPerformanceOptions = fw.SetPerformanceOptions(performanceOptions);

                    // 官方 VBA 示例在识别前先选了一个面。这里选实体的第一个面，
                    // 用来区分"必须有预选"和"API 本身没生效"。
                    model.ClearSelection2(true);
                    using (var initialSelection = PrepareRecognitionSelection(model, selectionMode))
                        result.FaceSelected = initialSelection is not null;
                    result.SeedSelectionMark = SelectionMark(selectionMode);

                    result.RecognizedFeatureCount = 0;
                    result.CreateFeaturesResult = false;
                    if (nativeRecognitionCommand)
                    {
                        model.ClearSelection2(true);
                        result.NativeRecognitionCommandStarted = app.RunCommand(1999, string.Empty);
                        PumpAndWait(1000);
                        result.NativeRecognitionCommandAccepted = app.RunCommand(-2, string.Empty);
                        PumpAndWait(15_000);
                        result.NativeRecognitionWaitMilliseconds = 15_000;
                        result.CreateFeaturesResult = result.NativeRecognitionCommandStarted
                            && result.NativeRecognitionCommandAccepted;
                        result.RecognizedFeatureCount = CountCoreFeatures(model);
                        result.RecognizeAttempts = 1;
                        result.CompletedRecognitionPasses = result.CreateFeaturesResult ? 1 : 0;
                    }
                    else if (interactiveFeature is not null)
                    {
                        model.ClearSelection2(true);
                        using var selection = PrepareRecognitionSelection(model, selectionMode);
                        result.FaceSelected = selection is not null;
                        result.InteractiveRecognitionResult =
                            (selectionMode == "none" || selection is not null)
                            && fw.RecognizeFeatureInteractive(interactiveFeature, 0);
                        result.RecognizedFeatureCount = result.InteractiveRecognitionResult ? 1 : 0;
                        result.RecognizeAttempts = 1;
                        result.CompletedRecognitionPasses = result.InteractiveRecognitionResult ? 1 : 0;
                    }
                    else
                    {
                        var recognitionOptions = recognitionSequence
                            ?? Enumerable.Repeat(recognizeOptions, recognitionPasses).ToArray();
                        for (int pass = 1; pass <= recognitionOptions.Length; pass++)
                        {
                            var passOptions = recognitionOptions[pass - 1];
                            var passCount = 0;
                            for (int attempt = 1; attempt <= 10; attempt++)
                            {
                                model.ClearSelection2(true);
                                using var selection = PrepareRecognitionSelection(model, selectionMode);
                                result.FaceSelected = selection is not null;
                                int count = selectionMode == "none" || selection is not null
                                    ? fw.RecognizeFeatureAutomatic(passOptions)
                                    : 0;
                                result.RecognitionTrials[$"pass{pass:00}-options0x{passOptions:X}-attempt{attempt:00}"] = count;
                                if (count > 0)
                                {
                                    passCount = count;
                                    result.RecognizeAttempts += attempt;
                                    break;
                                }

                                PumpAndWait(300);
                            }

                            if (passCount == 0)
                            {
                                if (recognitionSequence is null)
                                    break;
                                continue;
                            }
                            result.RecognizedFeatureCount += passCount;
                            result.CompletedRecognitionPasses++;
                            PumpAndWait(300);
                        }
                    }
                    if (!nativeRecognitionCommand && result.RecognizedFeatureCount > 0)
                    {
                        result.CreateFeaturesResult = fw.CreateFeatures(createOptions);
                    }
                    result.TimingsMs["RecognizeAndCreate"] = Mark(sw);

                    Marshal.FinalReleaseComObject(fwObject);
                }
            }

            result.FeatureNamesAfterRecognize = ListFeatures(model);

            // ---- 阶段 3：逐个草图完全定义 ----
            if (!skipFullyDefine)
                result.Sketches = FullyDefineAllSketches(model);
            result.TimingsMs["FullyDefine"] = Mark(sw);

            // ---- 阶段 4：保存 .SLDPRT ----
            int saveErrors = 0;
            int saveWarnings = 0;
            result.SaveResult = model.Extension.SaveAs3(
                output,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                null,
                ref saveErrors,
                ref saveWarnings);
            result.SaveErrors = saveErrors;
            result.SaveWarnings = saveWarnings;
            result.TimingsMs["Save"] = Mark(sw);

            if (File.Exists(output))
            {
                result.OutputLength = new FileInfo(output).Length;
            }

            app.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swUseEnglishLanguageFeatureNames, oldEnglishNames);

            result.Success = result.SaveResult && result.OutputLength > 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            try
            {
                if (model is not null)
                {
                    app?.CloseDoc(model.GetTitle());
                    Marshal.FinalReleaseComObject(model);
                }
            }
            catch { }

            if (app is not null && attachPid is not null && !string.IsNullOrWhiteSpace(previousActiveTitle))
            {
                try
                {
                    var restoreErrors = 0;
                    app.ActivateDoc3(previousActiveTitle, false, 0, ref restoreErrors);
                    result.RestoreActiveDocErrors = restoreErrors;
                }
                catch { }
            }

            try
            {
                if (app is not null && result.CreatedNewInstance)
                {
                    app.ExitApp();
                    result.ExitAppCalled = true;
                }
            }
            catch { }

            if (app is not null)
            {
                try { Marshal.FinalReleaseComObject(app); } catch { }
            }

            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.WaitForExit(20000))
                    {
                        ownedProcess.Kill(entireProcessTree: true);
                        ownedProcess.WaitForExit(20000);
                    }
                }
                catch { }
                finally { ownedProcess.Dispose(); }
            }
            attachedProcess?.Dispose();

            if (result.ExitAppCalled)
            {
                result.LeakedPids = result.NewPids.Where(IsAlive).ToArray();
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));

        return result.Success ? 0 : 1;
    }

    /// <summary>验收清理用：仅当系统中唯一的 SolidWorks PID 与预期一致时正常退出。</summary>
    private static int ExitOwnedApplication(int expectedPid)
    {
        var pids = GetPids();
        if (pids.Length != 1 || pids[0] != expectedPid)
        {
            Console.Error.WriteLine(
                $"拒绝退出 SolidWorks：预期唯一 PID={expectedPid}，实际={string.Join(',', pids)}");
            return 1;
        }

        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (type is null)
        {
            Console.Error.WriteLine("SldWorks.Application 未注册。");
            return 2;
        }

        ISldWorks? app = null;
        try
        {
            app = (ISldWorks)Activator.CreateInstance(type)!;
            app.ExitApp();
            return 0;
        }
        finally
        {
            if (app is not null && Marshal.IsComObject(app))
            {
                Marshal.FinalReleaseComObject(app);
            }
        }
    }

    /// <summary>
    /// 遍历特征树，对每个 ProfileFeature（草图）执行"完全定义草图"。
    /// 模式来自 SW API Help 示例 Fully_Define_Underdefined_Sketch_Example_CSharp。
    /// </summary>
    private static List<SketchFact> FullyDefineAllSketches(ModelDoc2 model)
    {
        var facts = new List<SketchFact>();
        var extension = model.Extension;
        var sketchManager = model.SketchManager;

        const int markHorizontal = 2;
        const int markVertical = 4;
        int relations =
            (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Concentric
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Parallel
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Perpendicular
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Tangent
          | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Equal;

        var sketchFeatures = new List<Feature>();
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null)
        {
            if (feature.GetTypeName2() == "ProfileFeature")
            {
                sketchFeatures.Add(feature);
            }

            feature = (Feature?)feature.GetNextFeature();
        }

        foreach (Feature sketchFeature in sketchFeatures)
        {
            var fact = new SketchFact { Name = sketchFeature.Name };
            try
            {
                var sketch = sketchFeature.GetSpecificFeature2() as Sketch;
                fact.SegmentCount = ((object[]?)sketch?.GetSketchSegments())?.Length ?? -1;
                fact.StatusBefore = ((swConstrainedStatus_e)(sketch?.GetConstrainedStatus() ?? 0)).ToString();

                model.ClearSelection2(true);
                sketchFeature.Select2(false, 0);
                sketchManager.InsertSketch(false);   // 进入编辑

                // 参数 HorizontalDatumDisp/VerticalDatumDisp 传 null 时，
                // SW 要求用选择标记 6（= 2|4）预选基准，这里用原点。
                fact.DatumSelected = extension.SelectByID2(
                    "Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, false,
                    markHorizontal | markVertical, null, 0);

                fact.FullyDefineReturn = sketchManager.FullyDefineSketch(
                    true, true, relations, true,
                    1, null, 1, null, 1, 1);

                sketchManager.InsertSketch(true);    // 退出并重建

                var after = sketchFeature.GetSpecificFeature2() as Sketch;
                fact.StatusAfter = ((swConstrainedStatus_e)(after?.GetConstrainedStatus() ?? 0)).ToString();
                fact.Ok = fact.StatusAfter == swConstrainedStatus_e.swFullyConstrained.ToString();
            }
            catch (Exception ex)
            {
                fact.Error = ex.Message;
                try { sketchManager.InsertSketch(true); } catch { }
            }

            facts.Add(fact);
        }

        return facts;
    }

    private static List<string> ListFeatures(ModelDoc2 model)
    {
        var names = new List<string>();
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null && names.Count < 200)
        {
            names.Add($"{feature.Name} [{feature.GetTypeName2()}]");
            feature = (Feature?)feature.GetNextFeature();
        }

        return names;
    }

    private static int CountCoreFeatures(ModelDoc2 model)
    {
        var count = 0;
        var scanned = 0;
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null && scanned++ < 200)
        {
            if (IsCoreFeatureType(feature.GetTypeName2()))
                count++;
            feature = (Feature?)feature.GetNextFeature();
        }

        return count;
    }

    /// <summary>验收用：在自有 SolidWorks 进程中只读重开 SLDPRT，并输出结构化特征树。</summary>
    private static int Inspect(string path, int? attachPid, string[]? expectedCoreSignature)
    {
        var result = new InspectResult
        {
            Path = Path.GetFullPath(path),
            PreexistingPids = GetPids(),
        };
        ISldWorks? app = null;
        ModelDoc2? model = null;
        Process? ownedProcess = null;
        Process? attachedProcess = null;
        string? previousActiveTitle = null;
        try
        {
            if (!File.Exists(result.Path))
                throw new FileNotFoundException("待检查 SLDPRT 不存在。", result.Path);
            if (attachPid is int existingPid)
            {
                attachedProcess = Process.GetProcessById(existingPid);
                app = BindByPid(attachedProcess, TimeSpan.FromSeconds(15))
                    ?? throw new TimeoutException($"无法按 PID {existingPid} 附着检查用 SolidWorks。");
                result.AttachedPid = existingPid;
                previousActiveTitle = (app.ActiveDoc as ModelDoc2)?.GetTitle();
            }
            else
            {
                var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                    ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
                var executable = ResolveSolidWorksExecutable(type)
                    ?? throw new InvalidOperationException("无法从 COM 注册解析 SLDWORKS.exe。");
                ownedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("启动检查用 SolidWorks 返回 null。");
                result.OwnedPid = ownedProcess.Id;
                app = BindByPid(ownedProcess, TimeSpan.FromSeconds(120))
                    ?? throw new TimeoutException($"无法按 PID {ownedProcess.Id} 绑定检查用 SolidWorks。");
                app.Visible = false;
                app.UserControl = false;
            }
            result.SolidWorksRevision = app.RevisionNumber();

            var errors = 0;
            var warnings = 0;
            model = (ModelDoc2?)app.OpenDoc6(
                result.Path,
                (int)swDocumentTypes_e.swDocPART,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                string.Empty,
                ref errors,
                ref warnings);
            result.OpenErrors = errors;
            result.OpenWarnings = warnings;
            if (model is null)
                throw new InvalidOperationException($"OpenDoc6 失败 errors={errors} warnings={warnings}");

            result.Title = model.GetTitle();
            result.BodyCount = CountBodies(model);
            result.Features = InspectFeatures(model);
            result.ActualCoreSignature = result.Features
                .Where(feature => IsCoreFeatureType(feature.TypeName))
                .Select(feature => feature.TypeName)
                .ToArray();
            result.ExpectedCoreSignature = expectedCoreSignature ?? [];
            result.SignatureMatched = expectedCoreSignature is null
                || result.ActualCoreSignature.SequenceEqual(expectedCoreSignature, StringComparer.OrdinalIgnoreCase);
            result.Success = result.SignatureMatched;
            if (!result.SignatureMatched)
            {
                result.Error = "核心特征签名不一致。";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.HResult = $"0x{unchecked((uint)ex.HResult):X8}";
        }
        finally
        {
            if (model is not null)
            {
                try { app?.CloseDoc(model.GetTitle()); } catch { }
                ReleaseCom(model);
            }
            if (app is not null)
            {
                if (attachPid is not null)
                {
                    if (!string.IsNullOrWhiteSpace(previousActiveTitle))
                    {
                        try
                        {
                            var restoreErrors = 0;
                            app.ActivateDoc3(previousActiveTitle, false, 0, ref restoreErrors);
                        }
                        catch { }
                    }
                }
                else
                {
                    try { app.ExitApp(); } catch { }
                }
                ReleaseCom(app);
            }
            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.WaitForExit(20000))
                    {
                        result.ForcedTermination = true;
                        ownedProcess.Kill(entireProcessTree: true);
                        ownedProcess.WaitForExit(20000);
                    }
                }
                catch { }
                finally { ownedProcess.Dispose(); }
            }
            attachedProcess?.Dispose();
            result.LeftoverPids = GetPids().Except(result.PreexistingPids).ToArray();
            if (result.LeftoverPids.Length > 0)
                result.Success = false;
        }

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return result.Success ? 0 : 1;
    }

    private static List<InspectFeature> InspectFeatures(ModelDoc2 model)
    {
        var result = new List<InspectFeature>();
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null && result.Count < 512)
        {
            var next = (Feature?)feature.GetNextFeature();
            Sketch? sketch = null;
            try
            {
                var typeName = feature.GetTypeName2();
                string? sketchStatus = null;
                if (typeName == "ProfileFeature")
                {
                    sketch = feature.GetSpecificFeature2() as Sketch;
                    if (sketch is not null)
                        sketchStatus = ((swConstrainedStatus_e)sketch.GetConstrainedStatus()).ToString();
                }
                result.Add(new InspectFeature(feature.Name, typeName, sketchStatus));
            }
            finally
            {
                ReleaseCom(sketch);
                ReleaseCom(feature);
                feature = next;
            }
        }
        return result;
    }

    private static bool IsCoreFeatureType(string typeName)
        => typeName is "BaseBody" or "ImportedBody" or "Imported" or "Extrusion" or "ICE"
            or "RefSurface" or "Thicken" or "HoleWzd" or "Fillet" or "Chamfer" or "Revolution" or "Rib";

    /// <summary>抽干本 STA 线程的消息队列并等待，给 SolidWorks 的异步工作留出时间。</summary>
    private static void PumpAndWait(int milliseconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessage(out NativeMessage msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(20);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint flags);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    private static SeedFaceSelection? SelectFirstFace(ModelDoc2 model, int mark)
    {
        try
        {
            var part = (PartDoc)model;
            var bodies = (object[]?)part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            if (bodies is null || bodies.Length == 0)
            {
                return null;
            }

            var body = (Body2)bodies[0];
            var face = body.GetFirstFace() as Face2;
            if (face is null)
            {
                return null;
            }

            var selectionManager = (SelectionMgr)model.SelectionManager;
            var selectData = (SelectData)selectionManager.CreateSelectData();
            selectData.Mark = mark;
            var entity = (Entity)face;
            if (!entity.Select4(false, selectData))
            {
                ReleaseCom(selectData);
                return null;
            }

            return new SeedFaceSelection(body, face, selectData);
        }
        catch
        {
            return null;
        }
    }

    private static SeedFaceSelection? PrepareRecognitionSelection(ModelDoc2 model, string selectionMode)
        => selectionMode switch
        {
            "none" => null,
            "first-mark0" => SelectFirstFace(model, 0),
            "first-mark8" => SelectFirstFace(model, AutomaticRecognitionSelectionMark),
            _ => throw new ArgumentOutOfRangeException(nameof(selectionMode)),
        };

    private static int SelectionMark(string selectionMode)
        => selectionMode switch
        {
            "first-mark8" => AutomaticRecognitionSelectionMark,
            _ => 0,
        };

    private static int ParseInteger(string value)
        => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value[2..], 16)
            : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string[] DescribeRecognitionOptions(int options)
    {
        (int Value, string Name)[] known =
        [
            (1, "Extrude"),
            (2, "Volume"),
            (4, "Revolve"),
            (8, "Holes"),
            (16, "Chamfils"),
            (32, "Ribs"),
            (64, "BaseFlange"),
            (128, "SketchedBend"),
            (256, "AutoEdgeFlange"),
            (512, "AutoHemFlange"),
        ];
        return known.Where(item => (options & item.Value) != 0).Select(item => item.Name).ToArray();
    }

    private static string? ResolveSolidWorksExecutable(Type applicationType)
    {
        using var key = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey($@"CLSID\{{{applicationType.GUID}}}\LocalServer32");
        if (key?.GetValue(null) is not string server || string.IsNullOrWhiteSpace(server))
            return null;
        return System.Environment.ExpandEnvironmentVariables(server.Trim().Trim('"'));
    }

    private static ISldWorks? BindByPid(Process process, TimeSpan timeout)
    {
        var monikerName = $"SolidWorks_PID_{process.Id}";
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var instance = GetRunningObject(monikerName);
            if (instance is ISldWorks application)
                return application;
            if (instance is not null && Marshal.IsComObject(instance))
            {
                try { Marshal.FinalReleaseComObject(instance); } catch { }
            }

            process.Refresh();
            if (process.HasExited)
                return null;
            Thread.Sleep(500);
        }

        return null;
    }

    private static object? GetRunningObject(string displayName)
    {
        IBindCtx? context = null;
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        try
        {
            if (CreateBindCtx(0, out context) != 0 || context is null)
                return null;
            context.GetRunningObjectTable(out table);
            if (table is null)
                return null;
            table.EnumRunning(out enumerator);
            if (enumerator is null)
                return null;
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(context, null, out var name);
                    if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    table.GetObject(moniker, out var instance);
                    return instance;
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(moniker);
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(table);
            ReleaseCom(context);
        }

        return null;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    private static int CountBodies(ModelDoc2 model)
    {
        try
        {
            var part = (PartDoc)model;
            var bodies = (object[]?)part.GetBodies2((int)swBodyType_e.swSolidBody, false);
            return bodies?.Length ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// FeatureWorks CLSID {7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}，
    /// InprocServer32 是相对路径 ".\fworks\fworks.dll"，需要拼上 SolidWorks 安装目录。
    /// 安装目录从 SldWorks.Application 的 LocalServer32 反推，不硬编码。
    /// </summary>
    private static string? ResolveFeatureWorksDll()
    {
        using Microsoft.Win32.RegistryKey? fwKey = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey(@"CLSID\{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}\InprocServer32");
        if (fwKey?.GetValue(null) is not string relative)
        {
            return null;
        }

        if (Path.IsPathRooted(relative))
        {
            return relative;
        }

        Type? appType = Type.GetTypeFromProgID(ProgId, throwOnError: false);
        if (appType is null)
        {
            return null;
        }

        using Microsoft.Win32.RegistryKey? swKey = Microsoft.Win32.Registry.ClassesRoot
            .OpenSubKey($@"CLSID\{appType.GUID:B}\LocalServer32");
        if (swKey?.GetValue(null) is not string server)
        {
            return null;
        }

        string exe = server.Trim('"').Split('"')[0];
        string? dir = Path.GetDirectoryName(exe);
        return dir is null ? null : Path.GetFullPath(Path.Combine(dir, relative));
    }

    private static int[] GetPids() =>
        Process.GetProcessesByName(SolidWorksProcessName).Select(p => { int id = p.Id; p.Dispose(); return id; }).OrderBy(x => x).ToArray();

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long Mark(Stopwatch sw)
    {
        long ms = sw.ElapsedMilliseconds;
        sw.Restart();
        return ms;
    }
}

internal sealed class SeedFaceSelection(Body2 body, Face2 face, SelectData selectData) : IDisposable
{
    private object? _body = body;
    private object? _face = face;
    private object? _selectData = selectData;

    public void Dispose()
    {
        Release(ref _selectData);
        Release(ref _face);
        Release(ref _body);
    }

    private static void Release(ref object? value)
    {
        var current = Interlocked.Exchange(ref value, null);
        if (current is null || !Marshal.IsComObject(current))
            return;
        try { Marshal.FinalReleaseComObject(current); } catch { }
    }
}

internal sealed class ProbeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string? Clsid { get; set; }
    public string? SolidWorksRevision { get; set; }
    public int[] PreexistingPids { get; set; } = Array.Empty<int>();
    public int[] NewPids { get; set; } = Array.Empty<int>();
    public bool CreatedNewInstance { get; set; }
    public bool ExitAppCalled { get; set; }
    public int[] LeakedPids { get; set; } = Array.Empty<int>();
    public bool OldEnglishFeatureNames { get; set; }
    public bool Old3DInterconnect { get; set; }
    public bool IsolatedInstance { get; set; }
    public bool ImportDiagnosisRequested { get; set; }
    public int ImportDiagnosisResult { get; set; } = int.MinValue;
    public bool NativeRecognitionCommandRequested { get; set; }
    public bool NativeRecognitionCommandStarted { get; set; }
    public bool NativeRecognitionCommandAccepted { get; set; }
    public bool NativeRecognitionCommandTimedOut { get; set; }
    public int NativeRecognitionWaitMilliseconds { get; set; }
    public int? AttachedPid { get; set; }
    public string? PreviousActiveDocTitle { get; set; }
    public int RestoreActiveDocErrors { get; set; }
    public int RecognitionOptions { get; set; }
    public string? RecognitionOptionsHex { get; set; }
    public string[] RecognitionOptionNames { get; set; } = [];
    public int AdvancedOptions { get; set; }
    public int PerformanceOptions { get; set; }
    public short CreateOptions { get; set; }
    public int RequestedRecognitionPasses { get; set; }
    public int[] RecognitionSequence { get; set; } = [];
    public string? InteractiveFeatureType { get; set; }
    public bool InteractiveRecognitionResult { get; set; }
    public int CompletedRecognitionPasses { get; set; }
    public string SelectionMode { get; set; } = string.Empty;

    public string? ImportDataType { get; set; }
    public string InputKind { get; set; } = string.Empty;
    public int LoadFileErrors { get; set; }
    public int LoadFileWarnings { get; set; }
    public int ActivateDocErrors { get; set; }
    public string? ActiveDocTitle { get; set; }
    public string? DocTypeAfterImport { get; set; }
    public int BodyCountAfterImport { get; set; }
    public List<string> FeatureNamesAfterImport { get; set; } = new();

    public string? FeatureWorksDll { get; set; }
    public int LoadAddInResult { get; set; } = int.MinValue;
    public bool LoadedAddInOnDemand { get; set; }
    public bool FeatureWorksAddInObtained { get; set; }
    public string? FeatureWorksObjectType { get; set; }
    public bool SetAdvancedOptions { get; set; }
    public bool SetPerformanceOptions { get; set; }
    public bool FaceSelected { get; set; }
    public int SeedSelectionMark { get; set; }
    public int RecognizedFeatureCount { get; set; } = -1;
    public int RecognizedFeatureCountLateBound { get; set; } = -1;
    public Dictionary<string, int> RecognitionTrials { get; set; } = new();
    public int RecognizeAttempts { get; set; }
    public bool CreateFeaturesResult { get; set; }
    public List<string> FeatureNamesAfterRecognize { get; set; } = new();

    public List<SketchFact> Sketches { get; set; } = new();

    public bool SaveResult { get; set; }
    public int SaveErrors { get; set; }
    public int SaveWarnings { get; set; }
    public long OutputLength { get; set; }

    public Dictionary<string, long> TimingsMs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal sealed class InspectResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? HResult { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? SolidWorksRevision { get; set; }
    public int[] PreexistingPids { get; set; } = [];
    public int OwnedPid { get; set; }
    public int? AttachedPid { get; set; }
    public int[] LeftoverPids { get; set; } = [];
    public bool ForcedTermination { get; set; }
    public int OpenErrors { get; set; }
    public int OpenWarnings { get; set; }
    public int BodyCount { get; set; }
    public List<InspectFeature> Features { get; set; } = [];
    public string[] ExpectedCoreSignature { get; set; } = [];
    public string[] ActualCoreSignature { get; set; } = [];
    public bool SignatureMatched { get; set; }
}

internal sealed record InspectFeature(string Name, string TypeName, string? SketchStatus);

internal sealed class SketchFact
{
    public string? Name { get; set; }
    public int SegmentCount { get; set; }
    public string? StatusBefore { get; set; }
    public bool DatumSelected { get; set; }
    public int FullyDefineReturn { get; set; }
    public string? StatusAfter { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
