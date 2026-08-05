using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using SolidWorks.Interop.fworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RecognitionOptionMatrix;

/// <summary>
/// FeatureWorks 自动识别的选项矩阵实测。
///
/// 起因：生产代码传 <c>0x3FF</c>（全 10 位），而 <c>fwAutomaticRecognitionOptions_e</c>
/// 的高 4 位（64/128/256/512）全是**钣金**选项——等于主动要求钣金识别，
/// 于是普通机械零件被识别成钣金。
///
/// 历史注释称"只传实体类选项 1|4|8|16|32 = 61 恒返回 0"，但 61 少了
/// <c>fwVolume</c>（2）；纯实体全开应当是 <b>63</b>，这个值从未被试过。
///
/// 本探针对同一个零件逐个掩码跑完整流程（导入 → 识别 → 创建特征），
/// 记录识别数、特征树类型、是否出现钣金节点、体积是否守恒。
/// 只读源文件：每次都新导入 XT，不保存任何产物。
/// </summary>
internal static class Program
{
    private static bool _selectBodyBeforeRecognition;
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";

    private static readonly (int Mask, string Label)[] DefaultMatrix =
    [
        (1 | 2 | 4 | 8 | 16 | 32, "63 实体全开（含体积，从未试过）"),
        (1 | 4 | 8 | 16 | 32, "61 历史值（漏了体积）"),
        (0x3FF, "1023 当前生产值（含全部钣金位）"),
        (1 | 2, "3 仅拉伸+体积"),
        (2, "2 仅体积"),
        (1, "1 仅拉伸"),
        (1 | 2 | 4 | 8 | 16 | 32 | 4, "63 重复对照"),
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? xtPath = null;
        var isolated = false;
        var visible = true;
        int[]? masks = null;
        var prime = false;
        var dumpMenu = false;
        var findCommand = false;
        var selectBody = false;
        string? directory = null;
        var listDocs = false;
        string? closeTitle = null;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index] == "--xt") xtPath = Path.GetFullPath(args[index + 1]);
            else if (args[index] == "--isolated") isolated = bool.Parse(args[index + 1]);
            else if (args[index] == "--visible") visible = bool.Parse(args[index + 1]);
            else if (args[index] == "--prime") prime = bool.Parse(args[index + 1]);
            else if (args[index] == "--menu") dumpMenu = bool.Parse(args[index + 1]);
            else if (args[index] == "--findcmd") findCommand = bool.Parse(args[index + 1]);
            else if (args[index] == "--select") selectBody = bool.Parse(args[index + 1]);
            else if (args[index] == "--dir") directory = Path.GetFullPath(args[index + 1]);
            else if (args[index] == "--docs") listDocs = bool.Parse(args[index + 1]);
            else if (args[index] == "--close") closeTitle = args[index + 1];
            else if (args[index] == "--masks") masks = args[index + 1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToArray();
        }

        _selectBodyBeforeRecognition = selectBody;
        if (closeTitle is not null)
            return CloseDocument(closeTitle);
        if (listDocs)
            return ListOpenDocuments();
        if (directory is not null)
            return SurveyDirectory(directory);

        if (xtPath is null || !File.Exists(xtPath))
        {
            Console.Error.WriteLine("用法：RecognitionOptionMatrix --xt <绝对 .x_t 路径>");
            return 2;
        }

        var matrix = masks is null
            ? DefaultMatrix
            : masks.Select(item => (Mask: item, Label: $"{item} 指定掩码")).ToArray();
        var report = new MatrixReport { Source = xtPath, Isolated = isolated, Visible = visible };
        if (dumpMenu)
            return DumpMenu();
        if (findCommand)
            return FindRecognitionCommand(xtPath);
        if (isolated)
            return RunIsolated(report, xtPath, visible, matrix, prime);

        var before = GetPids();
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("COM 返回空实例。");
            if (GetPids().Except(before).Any())
            {
                application.Visible = false;
                application.UserControl = false;
            }

            var featureWorks = AcquireFeatureWorks(application, out var diagnosis)
                ?? throw new InvalidOperationException("取不到 FeatureWorks 自动化对象。" + diagnosis);
            report.AddInDiagnosis = diagnosis;
            Console.Error.WriteLine($"FeatureWorks 来源：{diagnosis}");

            // 基准：不识别，只导入，记录原始几何。
            report.Baseline = Measure(application, xtPath, null, null, out _);
            Console.Error.WriteLine($"基准（不识别）：体积 {report.Baseline?.Volume:G6}，面 {report.Baseline?.FaceCount}");

            foreach (var (mask, label) in matrix)
            {
                var attempt = Measure(application, xtPath, featureWorks, mask, out var note) ?? new Measurement();
                attempt.Mask = mask;
                attempt.Label = label;
                attempt.Note = note;
                if (report.Baseline is { Volume: > 0 } baseline && attempt.Volume > 0)
                {
                    attempt.VolumeRatio = attempt.Volume / baseline.Volume;
                    attempt.VolumeMatches = Math.Abs(attempt.VolumeRatio - 1) < 1e-6;
                }

                report.Attempts.Add(attempt);
                Console.Error.WriteLine(
                    $"掩码 {mask,5} {label,-30} 识别={attempt.RecognizedCount,3} 特征={attempt.FeatureCount,3} "
                    + $"钣金={attempt.SheetMetalFeatures.Count,2} 体积比={attempt.VolumeRatio:F5} {attempt.Note}");
            }

            var best = report.Attempts
                .Where(item => item.VolumeMatches && item.SheetMetalFeatures.Count == 0)
                .OrderByDescending(item => item.RecognizedCount)
                .FirstOrDefault();
            report.Verdict = best is null
                ? "没有任何掩码同时满足「体积守恒 + 无钣金特征」。"
                : $"最佳掩码 {best.Mask}（{best.Label}）：识别 {best.RecognizedCount} 个特征，无钣金节点，体积守恒。";
            report.Success = true;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.Error = ex.Message;
        }
        finally
        {
            if (application is not null)
            {
                if (GetPids().Except(before).Any())
                {
                    try { application.ExitApp(); } catch { }
                }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        Console.Error.WriteLine(report.Verdict ?? report.Error);
        return report.Success ? 0 : 1;
    }

    /// <summary>
    /// 每个掩码一个**全新 SolidWorks 会话**。
    ///
    /// 共享会话下的矩阵结果自相矛盾（63 是 3 的超集却识别 0 个，而 3 识别 18 个），
    /// 说明 FeatureWorks 的状态会跨调用退化——那种数据无法归因到掩码。
    /// 这里每次起一个自有实例、跑完 ExitApp，把会话状态这个变量彻底消掉。
    ///
    /// 不使用 taskkill：自有实例走 ExitApp，绝不按进程名清理用户会话。
    /// </summary>
    private static int RunIsolated(MatrixReport report, string xtPath, bool visible, (int Mask, string Label)[] matrix, bool prime)
    {
        foreach (var (mask, label) in matrix)
        {
            Process? process = null;
            ISldWorks? application = null;
            var attempt = new Measurement { Mask = mask, Label = label };
            try
            {
                var executable = ResolveExecutable()
                    ?? throw new InvalidOperationException("解析不到 sldworks.exe。");
                process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false })
                    ?? throw new InvalidOperationException("Process.Start 返回 null。");
                application = BindByPid(process, TimeSpan.FromSeconds(120))
                    ?? throw new InvalidOperationException("按 PID 绑定超时。");
                application.Visible = visible;
                application.UserControl = false;

                var works = AcquireFeatureWorks(application, out var diagnosis);
                report.AddInDiagnosis ??= diagnosis;
                if (works is null)
                {
                    attempt.Note = "本会话取不到 FeatureWorks。";
                }
                else
                {
                    if (prime)
                        PrimeInteractive(application, works, xtPath, report);
                    report.Baseline ??= Measure(application, xtPath, null, null, out _);
                    var measured = Measure(application, xtPath, works, mask, out var note);
                    if (measured is not null)
                    {
                        measured.Mask = mask;
                        measured.Label = label;
                        measured.Note = note;
                        attempt = measured;
                    }
                }

                if (report.Baseline is { Volume: > 0 } baseline && attempt.Volume > 0)
                {
                    attempt.VolumeRatio = attempt.Volume / baseline.Volume;
                    attempt.VolumeMatches = Math.Abs(attempt.VolumeRatio - 1) < 1e-6;
                }
            }
            catch (Exception ex)
            {
                attempt.Note = "异常：" + ex.Message;
            }
            finally
            {
                if (application is not null)
                {
                    try { application.ExitApp(); } catch { }
                    try { Marshal.FinalReleaseComObject(application); } catch { }
                }
                if (process is not null)
                {
                    try { process.WaitForExit(60000); } catch { }
                    // 不强杀 SolidWorks：强制结束会破坏 FeatureWorks 加载项注册，
                    // 之前已经因此让用户手工去 Tools > Add-ins 重新启用过两次。
                    // ExitApp 收不掉就留着并报告，由人来处置。
                    try { if (!process.HasExited) Console.Error.WriteLine($"  [警告] PID {process.Id} 未在 ExitApp 后退出，已保留，请手工关闭。"); } catch { }
                    process.Dispose();
                }
            }

            report.Attempts.Add(attempt);
            Console.Error.WriteLine(
                $"[独立会话] 掩码 {mask,5} {label,-30} 识别={attempt.RecognizedCount,3} "
                + $"特征={attempt.FeatureCount,3} 钣金={attempt.SheetMetalFeatures.Count,2} "
                + $"体积比={attempt.VolumeRatio:F5} {attempt.Note}");
        }

        var winner = report.Attempts
            .Where(item => item.VolumeMatches && item.SheetMetalFeatures.Count == 0 && item.RecognizedCount > 0)
            .OrderByDescending(item => item.RecognizedCount)
            .FirstOrDefault();
        report.Verdict = winner is null
            ? "独立会话下没有掩码能识别出特征——问题不在掩码。"
            : $"独立会话最佳：掩码 {winner.Mask}（{winner.Label}），识别 {winner.RecognizedCount} 个，体积守恒，无钣金。";
        report.Success = true;
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        Console.Error.WriteLine(report.Verdict);
        return 0;
    }

    /// <summary>
    /// 实测结论：冷会话里 <c>RecognizeFeatureAutomatic</c> 恒返回 0（静默失败），
    /// 只有当这个 SolidWorks 会话被**手工识别过一次**之后，API 才开始正常工作，
    /// 且该状态跨文档、跨客户端进程存活。
    ///
    /// 手工点击走的是 UI 那条路，对应到 API 只剩 <c>RecognizeFeatureInteractive</c>
    /// 这一个成员（整个类型库只有 7 个成员，RunCommand 侧没有 FeatureWorks 命令常量）。
    /// 本方法在冷会话里选中一个面后逐个试候选特征名，看能否用它替代手工点击完成激活。
    /// </summary>
    private static void PrimeInteractive(ISldWorks application, object featureWorks, string xtPath, MatrixReport report)
    {
        string[] candidates =
        [
            "Extrude", "Extrusion", "Boss-Extrude", "Cut-Extrude", "Revolve",
            "Hole", "Fillet", "Chamfer", "Rib", "Draft", "Volume", "Boss", "Cut",
        ];

        var errors = 0;
        ModelDoc2? model = null;
        try
        {
            var importData = application.GetImportFileData(xtPath);
            model = application.LoadFile4(xtPath, "r", importData, ref errors) as ModelDoc2;
            if (model is null)
            {
                report.PrimeLog.Add($"激活用文档导入失败 errors={errors}");
                return;
            }

            application.ActivateDoc3(model.GetTitle(), false, 0, ref errors);

            // 交互式识别针对选中的面，先选一个面再调用。
            var selected = false;
            try
            {
                var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies is { Length: > 0 } && bodies[0] is Body2 body && body.GetFirstFace() is Face2 face)
                    selected = ((Entity)face).Select4(false, null);
            }
            catch (Exception ex)
            {
                report.PrimeLog.Add("选面异常：" + ex.Message);
            }

            report.PrimeLog.Add($"选面{(selected ? "成功" : "失败")}");

            var works = (IFeatureWorksApp)featureWorks;
            try { works.SetAdvancedOptions((short)(1 | 4)); works.SetPerformanceOptions(0); } catch { }

            foreach (var candidate in candidates)
            {
                try
                {
                    var accepted = works.RecognizeFeatureInteractive(candidate, 0);
                    report.PrimeLog.Add($"交互识别「{candidate}」返回 {accepted}");
                    if (accepted)
                    {
                        try { works.CreateFeatures(1); } catch { }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    report.PrimeLog.Add($"交互识别「{candidate}」异常 0x{ex.HResult:X8} {ex.Message}");
                }
            }
        }
        finally
        {
            if (model is not null)
            {
                try { application.CloseDoc(model.GetTitle()); } catch { }
            }
        }

        foreach (var line in report.PrimeLog)
            Console.Error.WriteLine("  [激活] " + line);
    }

    /// <summary>
    /// 起一个全新 SolidWorks，把主窗口的整棵菜单树连同命令 ID 打出来。
    ///
    /// 目的：找到"特征识别"（FeatureWorks）那一项的**真实命令 ID**。
    /// 手工点按钮能激活 FeatureWorks，而 API 不能；如果能把那个命令号读出来，
    /// 就可以用 ISldWorks::RunCommand 触发同一条 UI 路径，从而自动激活。
    /// ID 是从菜单里读出来的，不是猜的。
    /// </summary>
    private static int DumpMenu()
    {
        Process? process = null;
        ISldWorks? application = null;
        try
        {
            var executable = ResolveExecutable() ?? throw new InvalidOperationException("解析不到 sldworks.exe。");
            process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false })
                ?? throw new InvalidOperationException("Process.Start 返回 null。");
            application = BindByPid(process, TimeSpan.FromSeconds(120))
                ?? throw new InvalidOperationException("按 PID 绑定超时。");
            application.Visible = true;

            // 菜单要在有文档打开时才完整（插入菜单在无文档时大多被裁掉）。
            var errors = 0;
            application.NewDocument(application.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart), 0, 0, 0);
            StaWait(2000);

            var handle = IntPtr.Zero;
            for (var attempt = 0; attempt < 60 && handle == IntPtr.Zero; attempt++)
            {
                process.Refresh();
                handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    StaWait(500);
            }

            Console.Error.WriteLine($"主窗口句柄 = 0x{handle.ToInt64():X}");
            var menu = GetMenu(handle);
            if (menu == IntPtr.Zero)
            {
                Console.Error.WriteLine("GetMenu 返回 NULL —— 该版本用的是 Ribbon/自绘菜单，这条路走不通。");
                return 3;
            }

            var hits = new List<string>();
            Walk(menu, string.Empty, 0, hits);
            Console.Error.WriteLine($"--- 命中 {hits.Count} 项 ---");
            foreach (var hit in hits)
                Console.Error.WriteLine("  " + hit);
            _ = errors;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("菜单枚举失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                try { application.ExitApp(); } catch { }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
            if (process is not null)
            {
                try { process.WaitForExit(60000); } catch { }
                try { if (!process.HasExited) Console.Error.WriteLine($"  [警告] PID {process.Id} 未退出，请手工关闭。"); } catch { }
                process.Dispose();
            }
        }
    }

    private static void Walk(IntPtr menu, string path, int depth, List<string> hits)
    {
        if (depth > 6)
            return;
        var count = GetMenuItemCount(menu);
        for (var index = 0; index < count; index++)
        {
            var text = new StringBuilder(512);
            GetMenuString(menu, (uint)index, text, text.Capacity, MfByPosition);
            var label = text.ToString().Replace("&", string.Empty).Trim();
            var full = string.IsNullOrEmpty(path) ? label : path + " → " + label;
            var child = GetSubMenu(menu, index);
            if (child != IntPtr.Zero)
            {
                Walk(child, full, depth + 1, hits);
                continue;
            }

            var id = GetMenuItemID(menu, index);
            if (label.Contains("FeatureWorks", StringComparison.OrdinalIgnoreCase)
                || label.Contains("特征识别", StringComparison.Ordinal)
                || full.Contains("FeatureWorks", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add($"ID={id,-8} {full}");
            }
        }
    }

    private static void StaWait(int milliseconds)
    {
        var until = System.Environment.TickCount64 + milliseconds;
        while (System.Environment.TickCount64 < until)
            Thread.Sleep(50);
    }

    private const uint MfByPosition = 0x00000400;

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")]
    private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder buffer, int max, uint flags);

    /// <summary>
    /// 枚举 SolidWorks 命令 ID，按菜单名反查"特征识别"（FeatureWorks）那一条。
    ///
    /// Win32 GetMenu 在 SW 2025 上返回 NULL（自绘菜单），但 API 自己提供了
    /// <c>ISldWorks::GetLocalizedMenuName(int)</c>：给命令 ID 返回菜单文字。
    /// 于是可以**枚举**而不是猜——遍历 ID 空间，挑出名字里带 FeatureWorks/特征识别的。
    ///
    /// 找到后立刻验证：RunCommand 触发它，再跑一次自动识别，看是否被激活。
    /// </summary>
    private static int FindRecognitionCommand(string xtPath)
    {
        Process? process = null;
        ISldWorks? application = null;
        try
        {
            var executable = ResolveExecutable() ?? throw new InvalidOperationException("解析不到 sldworks.exe。");
            process = Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false })
                ?? throw new InvalidOperationException("Process.Start 返回 null。");
            application = BindByPid(process, TimeSpan.FromSeconds(120))
                ?? throw new InvalidOperationException("按 PID 绑定超时。");
            application.Visible = true;

            var works = AcquireFeatureWorks(application, out var diagnosis);
            Console.Error.WriteLine("FeatureWorks 来源：" + diagnosis);
            if (works is null)
                return 3;

            // 先导入零件：不少命令在没有文档时不注册菜单名。
            var errors = 0;
            var importData = application.GetImportFileData(xtPath);
            var model = application.LoadFile4(xtPath, "r", importData, ref errors) as ModelDoc2;
            if (model is null)
            {
                Console.Error.WriteLine($"导入失败 errors={errors}");
                return 3;
            }
            application.ActivateDoc3(model.GetTitle(), false, 0, ref errors);

            // IFrame 提供 SolidWorks 自己的菜单枚举（Win32 GetMenu 对自绘菜单无效）。
            // 第一个参数实测为文档类型，第二个为顶级菜单名。
            var frame = (Frame)application.Frame();
            foreach (var docType in new[] { 0, 1, 2, 3 })
            {
                for (var top = 0; top <= 8; top++)
                {
                    string? menuName = null;
                    try { menuName = application.GetLocalizedMenuName(top); } catch { }
                    if (string.IsNullOrWhiteSpace(menuName))
                        continue;
                    var clean = menuName.Replace("&", string.Empty);
                    int count;
                    try { count = frame.GetSubMenuCount(docType, clean); } catch { continue; }
                    for (var item = 0; item < count; item++)
                    {
                        string? entry = null;
                        try { entry = frame.IGetSubMenus(docType, clean, item); } catch { }
                        if (string.IsNullOrWhiteSpace(entry))
                            continue;
                        if (entry.Contains("FeatureWorks", StringComparison.OrdinalIgnoreCase)
                            || entry.Contains("特征识别", StringComparison.Ordinal)
                            || entry.Contains("识别特征", StringComparison.Ordinal))
                        {
                            var commandId = -1;
                            try { commandId = application.GetCommandID(entry.Replace("&", string.Empty), docType); } catch { }
                            Console.Error.WriteLine(
                                $"  菜单命中 docType={docType} {clean} → 「{entry}」 GetCommandID={commandId}");
                        }
                    }
                }
            }

            var matches = new List<(int Id, string Name)>();
            var nonEmpty = 0;
            var samples = new List<string>();
            for (var id = 0; id <= 70000; id++)
            {
                string? name = null;
                try { name = application.GetLocalizedMenuName(id); } catch { }
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                nonEmpty++;
                if (samples.Count < 15)
                    samples.Add($"{id}={name.Trim()}");
                if (name.Contains("FeatureWorks", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("特征识别", StringComparison.Ordinal)
                    || name.Contains("识别特征", StringComparison.Ordinal))
                {
                    matches.Add((id, name.Trim()));
                    Console.Error.WriteLine($"  命中 ID={id} 名称=「{name.Trim()}」");
                }
            }

            Console.Error.WriteLine($"--- 对照：{nonEmpty} 个 ID 返回了非空菜单名 ---");
            foreach (var sample in samples)
                Console.Error.WriteLine("    样本 " + sample);
            Console.Error.WriteLine($"--- 共命中 {matches.Count} 条 ---");
            if (matches.Count == 0)
                return 4;

            // 逐个触发候选命令，每次之后测一遍自动识别是否被激活。
            foreach (var (id, name) in matches)
            {
                var enabled = false;
                try { enabled = application.IsCommandEnabled(id); } catch { }
                bool ran;
                try { ran = application.RunCommand(id, string.Empty); }
                catch (Exception ex) { Console.Error.WriteLine($"  ID={id} RunCommand 异常：{ex.Message}"); continue; }
                StaWait(3000);
                Console.Error.WriteLine($"  ID={id}「{name}」可用={enabled} RunCommand={ran}");

                var measured = Measure(application, xtPath, works, 63, out var note);
                Console.Error.WriteLine(
                    $"    → 触发后自动识别 = {measured?.RecognizedCount ?? -1} {note}");
                if (measured is { RecognizedCount: > 0 })
                {
                    Console.Error.WriteLine($"*** 自动激活成功：RunCommand({id}) 之后识别到 {measured.RecognizedCount} 个特征。***");
                    return 0;
                }
            }

            Console.Error.WriteLine("所有候选命令都没能激活自动识别。");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("命令枚举失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                try { application.ExitApp(); } catch { }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
            if (process is not null)
            {
                try { process.WaitForExit(60000); } catch { }
                try { if (!process.HasExited) Console.Error.WriteLine($"  [警告] PID {process.Id} 未退出，请手工关闭。"); } catch { }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 对一整个目录的 XT 逐个跑完整识别流程，量"部分识别到底差多少"。
    ///
    /// 生产的语义守卫要求 <c>CreateFeatures</c> 之后特征树里**不得残留任何 BaseBody**，
    /// 否则整份结果丢弃、退回哑实体。现场 53 个零件全被这条拦下，
    /// 但没人知道残留是"只差一小块"还是"几乎什么都没认出来"。
    ///
    /// 本探针只读：每个零件新导入一次，量完即弃，不保存任何产物。
    /// 附着已在运行的 SolidWorks（须已人工激活 FeatureWorks）。
    /// </summary>
    private static int SurveyDirectory(string directory)
    {
        var files = Directory.GetFiles(directory, "*.x_t").OrderBy(item => item).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"目录里没有 .x_t：{directory}");
            return 2;
        }

        var before = GetPids();
        ISldWorks? application = null;
        var rows = new List<SurveyRow>();
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("COM 返回空实例。");
            var works = AcquireFeatureWorks(application, out var diagnosis)
                ?? throw new InvalidOperationException("取不到 FeatureWorks：" + diagnosis);
            Console.Error.WriteLine("FeatureWorks 来源：" + diagnosis);
            Console.Error.WriteLine($"共 {files.Length} 个零件。");

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var row = new SurveyRow { Name = name };
                try
                {
                    var baseline = Measure(application, file, null, null, out _);
                    var measured = Measure(application, file, works, 63, out var note);
                    row.Note = note;
                    if (baseline is not null && measured is not null)
                    {
                        row.Recognized = measured.RecognizedCount;
                        row.Created = measured.FeaturesCreated;
                        row.VolumeRatio = baseline.Volume > 0 ? measured.Volume / baseline.Volume : 0;
                        row.BaseFaces = baseline.FaceCount;
                        row.Leftovers = measured.FeatureTypes
                            .Where(item => item.EndsWith(":BaseBody", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        row.SheetMetal = measured.SheetMetalFeatures.Count;
                        row.FeatureTypes = measured.FeatureTypes
                            .Where(item => !item.Contains("Folder", StringComparison.OrdinalIgnoreCase)
                                        && !item.EndsWith(":RefPlane", StringComparison.OrdinalIgnoreCase)
                                        && !item.Contains("DetailCabinet", StringComparison.OrdinalIgnoreCase)
                                        && !item.Contains("OriginProfileFeature", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    row.Note = "异常：" + ex.Message;
                }

                rows.Add(row);
                Console.Error.WriteLine(
                    $"{rows.Count,3}/{files.Length} {name,-34} 识别={row.Recognized,3} 残留={row.Leftovers.Count} "
                    + $"钣金={row.SheetMetal} 体积比={row.VolumeRatio:F5} {row.Note}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("测量失败：" + ex.Message);
        }
        finally
        {
            if (application is not null)
            {
                if (GetPids().Except(before).Any())
                {
                    try { application.ExitApp(); } catch { }
                }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
        }

        var measuredRows = rows.Where(item => item.Recognized > 0 || item.Leftovers.Count > 0).ToList();
        var clean = rows.Count(item => item.Recognized > 0 && item.Leftovers.Count == 0);
        var partial = rows.Count(item => item.Recognized > 0 && item.Leftovers.Count > 0);
        var none = rows.Count(item => item.Recognized == 0);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"完全识别（无残留）：{clean}");
        Console.Error.WriteLine($"部分识别（有残留）：{partial}");
        Console.Error.WriteLine($"完全没识别出：      {none}");
        if (measuredRows.Count > 0)
        {
            Console.Error.WriteLine(
                $"识别数中位数：{Median(measuredRows.Select(item => (double)item.Recognized))}，"
                + $"最少 {measuredRows.Min(item => item.Recognized)}，最多 {measuredRows.Max(item => item.Recognized)}");
        }

        Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        return 0;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(item => item).ToArray();
        if (sorted.Length == 0)
            return 0;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// 列出当前 SolidWorks 会话里打开的全部文档。
    /// 借用用户会话时，标题重名会让组件 OpenDoc6 直接返回
    /// swFileWithSameTitleAlreadyOpen(65536)，装配就地失败——先看清楚开着什么。
    /// 只读：不关闭任何文档。
    /// </summary>
    private static int ListOpenDocuments()
    {
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("COM 返回空实例。");
            var count = 0;
            var document = application.GetFirstDocument() as ModelDoc2;
            while (document is not null)
            {
                count++;
                Console.Error.WriteLine(
                    $"  [{count,3}] {document.GetTitle(),-44} 路径={document.GetPathName()}");
                document = document.GetNext() as ModelDoc2;
            }

            Console.Error.WriteLine($"打开的文档总数：{count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("枚举失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
                try { Marshal.FinalReleaseComObject(application); } catch { }
        }
    }

    /// <summary>
    /// 按标题关闭一个打开的文档（不保存）。
    /// 用途：手工激活 FeatureWorks 留下的临时零件会以标题占用装配组件，
    /// 必须关掉才能继续。只关标题精确匹配的那一个，绝不遍历关闭。
    /// </summary>
    private static int CloseDocument(string title)
    {
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("COM 返回空实例。");

            var document = application.GetFirstDocument() as ModelDoc2;
            while (document is not null)
            {
                var current = document.GetTitle();
                if (string.Equals(current, title, StringComparison.OrdinalIgnoreCase))
                {
                    var path = document.GetPathName();
                    application.CloseDoc(current);
                    Console.Error.WriteLine(
                        $"已关闭「{current}」（路径={(string.IsNullOrWhiteSpace(path) ? "未保存" : path)}）。");
                    return 0;
                }

                document = document.GetNext() as ModelDoc2;
            }

            Console.Error.WriteLine($"没有找到标题为「{title}」的文档，未做任何改动。");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("关闭失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
                try { Marshal.FinalReleaseComObject(application); } catch { }
        }
    }

    private static ISldWorks? BindByPid(Process process, TimeSpan timeout)
    {
        var wanted = $"SolidWorks_PID_{process.Id}";
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            IBindCtx? context = null;
            IRunningObjectTable? table = null;
            IEnumMoniker? enumerator = null;
            try
            {
                if (CreateBindCtx(0, out context) == 0 && context is not null)
                {
                    context.GetRunningObjectTable(out table);
                    table.EnumRunning(out enumerator);
                    var monikers = new IMoniker[1];
                    while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                    {
                        monikers[0].GetDisplayName(context, null, out var name);
                        if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                            continue;
                        table.GetObject(monikers[0], out var instance);
                        if (instance is ISldWorks application)
                            return application;
                    }
                }
            }
            catch { }
            process.Refresh();
            if (process.HasExited)
                return null;
            Thread.Sleep(500);
        }

        return null;
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    /// <summary>从 COM 注册解析 sldworks.exe。</summary>
    private static string? ResolveExecutable()
    {
        var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
        if (type is null)
            return null;
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID\\{" + type.GUID + "}\\LocalServer32");
        if (key?.GetValue(null) is not string server || string.IsNullOrWhiteSpace(server))
            return null;
        return System.Environment.ExpandEnvironmentVariables(server.Trim().Trim('"'));
    }

    /// <summary>导入一份干净的 XT，可选执行识别，然后量几何与特征树。每次都用新文档，互不污染。</summary>
    private static Measurement? Measure(
        ISldWorks application,
        string xtPath,
        object? featureWorks,
        int? mask,
        out string? note)
    {
        note = null;
        var errors = 0;
        var importData = application.GetImportFileData(xtPath);
        var model = application.LoadFile4(xtPath, "r", importData, ref errors) as ModelDoc2;
        if (model is null)
        {
            note = $"导入失败 errors={errors}";
            return null;
        }

        try
        {
            if (featureWorks is IFeatureWorksApp works && mask is { } options)
            {
                application.ActivateDoc3(model.GetTitle(), false, 0, ref errors);
                var recognized = 0;
                try
                {
                    // 与生产代码同姿势：先设高级选项，再自动识别，最后创建特征。
                    // 论坛可跑通的例子在识别前会先选中实体；生产代码是 ClearSelection 后直接识别。
                    // 另：有报告称 SetAdvancedOptions 恒返回 false，这里把返回值一并记录。
                    if (_selectBodyBeforeRecognition)
                    {
                        var picked = model.Extension.SelectByID2(
                            string.Empty, "SOLIDBODY", 0, 0, 0, false, 0, null, 0);
                        note = (note ?? string.Empty) + $"选实体={picked} ";
                    }

                    // SetAdvancedOptions 的返回值与识别成败逐次吻合（False 必然识别 0）。
                    // 这里重试到它返回 True 为止，记录用了几次——决定"重试能否救回来"。
                    var advanced = false;
                    var tries = 0;
                    for (; tries < 12 && !advanced; tries++)
                    {
                        try { advanced = works.SetAdvancedOptions((short)(1 | 4)); }
                        catch { }
                        if (!advanced)
                            Thread.Sleep(250);
                    }

                    var performance = works.SetPerformanceOptions(0);
                    note = (note ?? string.Empty) + $"高级选项={advanced}(第{tries}次) 性能选项={performance} ";
                    recognized = works.RecognizeFeatureAutomatic(options);
                }
                catch (Exception ex)
                {
                    note = "识别异常：" + ex.Message;
                }

                var created = false;
                if (recognized > 0)
                {
                    try { created = works.CreateFeatures(1); }
                    catch (Exception ex) { note = (note ?? string.Empty) + " 创建异常：" + ex.Message; }
                }

                var measurement = MeasureModel(model);
                measurement.RecognizedCount = recognized;
                measurement.FeaturesCreated = created;
                return measurement;
            }

            return MeasureModel(model);
        }
        finally
        {
            try { application.CloseDoc(model.GetTitle()); } catch { }
        }
    }

    private static Measurement MeasureModel(ModelDoc2 model)
    {
        var measurement = new Measurement();
        var part = (PartDoc)model;
        var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as Array;
        foreach (var raw in bodies ?? Array.Empty<object>())
        {
            if (raw is not Body2 body)
                continue;
            if (body.GetMassProperties(1) is double[] mass && mass.Length >= 4)
                measurement.Volume += mass[3];
            measurement.FaceCount += body.GetFaceCount();
        }

        var feature = model.FirstFeature() as Feature;
        while (feature is not null)
        {
            var typeName = feature.GetTypeName2() ?? string.Empty;
            measurement.FeatureCount++;
            measurement.FeatureTypes.Add($"{feature.Name}:{typeName}");
            // 钣金相关的特征类型名——用户报告的"钣金节点"就是它们。
            if (typeName.Contains("SheetMetal", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("BaseFlange", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("EdgeFlange", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("SketchBend", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("HemFlange", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("FlatPattern", StringComparison.OrdinalIgnoreCase))
            {
                measurement.SheetMetalFeatures.Add($"{feature.Name}:{typeName}");
            }

            feature = feature.GetNextFeature() as Feature;
        }

        return measurement;
    }

    private static object? AcquireFeatureWorks(ISldWorks application)
        => AcquireFeatureWorks(application, out _);

    private static object? AcquireFeatureWorks(ISldWorks application, out string diagnosis)
    {
        var existing = application.GetAddInObject(FeatureWorksProgId);
        if (existing is not null)
        {
            diagnosis = "加载项已在会话中（手工勾选或启动加载）";
            return existing;
        }

        var path = ResolveFeatureWorksPath();
        if (path is null || !File.Exists(path))
        {
            diagnosis = "找不到 featureworks.dll";
            return null;
        }

        var loadResult = application.LoadAddIn(path);
        var loaded = application.GetAddInObject(FeatureWorksProgId);
        diagnosis = $"加载项原本未加载，LoadAddIn 返回 {loadResult}，取对象{(loaded is null ? "失败" : "成功")}";
        return loaded;
    }

    /// <summary>安装目录取自 LocalServer32 的 sldworks.exe，不要用 GetExecutablePath（会少一层目录）。</summary>
    private static string? ResolveFeatureWorksPath()
    {
        const string classId = "{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}";
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId}\InprocServer32");
        if (key?.GetValue(null) is not string registered || string.IsNullOrWhiteSpace(registered))
            return null;
        var server = System.Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(server))
            return server;

        var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
        if (type is null)
            return null;
        using var serverKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{type.GUID}}}\LocalServer32");
        if (serverKey?.GetValue(null) is not string executable || string.IsNullOrWhiteSpace(executable))
            return null;
        var directory = Path.GetDirectoryName(System.Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"')));
        return directory is null ? null : Path.GetFullPath(Path.Combine(directory, server));
    }

    private static int[] GetPids()
    {
        try { return Process.GetProcessesByName("SLDWORKS").Select(item => item.Id).ToArray(); }
        catch { return []; }
    }
}

internal sealed class MatrixReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool Isolated { get; set; }
    public string? AddInDiagnosis { get; set; }
    public List<string> PrimeLog { get; set; } = [];
    public bool Visible { get; set; }
    public Measurement? Baseline { get; set; }
    public string? Verdict { get; set; }
    public List<Measurement> Attempts { get; } = [];
}

internal sealed class Measurement
{
    public int Mask { get; set; }
    public string Label { get; set; } = string.Empty;
    public int RecognizedCount { get; set; }
    public bool FeaturesCreated { get; set; }
    public double Volume { get; set; }
    public int FaceCount { get; set; }
    public int FeatureCount { get; set; }
    public double VolumeRatio { get; set; }
    public bool VolumeMatches { get; set; }
    public List<string> FeatureTypes { get; } = [];
    public List<string> SheetMetalFeatures { get; } = [];
    public string? Note { get; set; }
}

internal sealed class SurveyRow
{
    public string Name { get; set; } = string.Empty;
    public int Recognized { get; set; }
    public bool Created { get; set; }
    public double VolumeRatio { get; set; }
    public int BaseFaces { get; set; }
    public int SheetMetal { get; set; }
    public List<string> Leftovers { get; set; } = [];
    public List<string> FeatureTypes { get; set; } = [];
    public string? Note { get; set; }
}
