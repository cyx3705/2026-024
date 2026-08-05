using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace FeatureWorksProbe;

/// <summary>
/// 把 <c>FeatureRecognizer.Prepare</c> 拆成可观测的步骤，定位
/// "LoadAddIn 成功但 GetAddInObject 恒返回 null" 到底卡在哪一环。
///
/// 重点验一个顺序假设：<c>GetAddInObject</c> 是否要求 SolidWorks 已有打开的文档。
/// 生产代码在导入任何零件**之前**调用 Prepare，若假设成立，这就是顺序缺陷而非环境问题。
/// </summary>
internal static class Program
{
    private const string FeatureWorksProgId = "FeatureWorks.FeatureWorksApp";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? xtPath = null;
        string? addinOverride = null;
        var startupDelayMilliseconds = 0;
        var loadArguments = "r";
        var useOpenDoc = false;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index] == "--xt") xtPath = Path.GetFullPath(args[index + 1]);
            else if (args[index] == "--addin") addinOverride = Path.GetFullPath(args[index + 1]);
            else if (args[index] == "--startup-delay-ms") startupDelayMilliseconds = int.Parse(args[index + 1]);
            else if (args[index] == "--load-args") loadArguments = args[index + 1];
            else if (args[index] == "--open-doc") useOpenDoc = bool.Parse(args[index + 1]);
        }
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("COM 返回空实例。");
            var owned = GetPids("SLDWORKS").Except(before).ToArray();
            Console.WriteLine($"[0] SolidWorks 实例：{(owned.Length > 0 ? "本进程新建" : "附着到已有会话")}；版本 {application.RevisionNumber()}");
            if (owned.Length > 0)
            {
                application.Visible = false;
                application.UserControl = false;
            }

            // 步骤 1：无文档时直接取自动化对象。
            Console.WriteLine($"[1] 无文档时 GetAddInObject → {Describe(TryGetAddIn(application))}");

            // 步骤 2：解析加载项路径并加载。
            var path = addinOverride ?? ResolvePath(application);
            Console.WriteLine($"[2] FeatureWorks 加载项路径 → {path ?? "<未找到>"}；存在={path is not null && File.Exists(path)}");
            if (path is not null && File.Exists(path))
            {
                var load = application.LoadAddIn(path);
                Console.WriteLine($"[3] LoadAddIn → {load}（0=成功 2=已加载 7=授权缺失）");
                Console.WriteLine($"[4] 加载后仍无文档 GetAddInObject → {Describe(TryGetAddIn(application))}");
            }

            // 步骤 5：开一个真实零件文档后再取——这一步是本探针的目的。
            if (xtPath is not null && File.Exists(xtPath))
            {
                if (startupDelayMilliseconds > 0)
                {
                    Console.WriteLine($"[4.5] 冷启动等待 → {startupDelayMilliseconds} ms");
                    Thread.Sleep(startupDelayMilliseconds);
                }
                var errors = 0;
                var warnings = 0;
                ModelDoc2? model;
                if (useOpenDoc)
                {
                    model = application.OpenDoc6(
                        xtPath,
                        (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        loadArguments,
                        ref errors,
                        ref warnings) as ModelDoc2;
                }
                else
                {
                    var importData = application.GetImportFileData(xtPath);
                    model = application.LoadFile4(xtPath, loadArguments, importData, ref errors) as ModelDoc2;
                }
                Console.WriteLine($"[5] 导入 XT → {(model is null ? $"失败 errors={errors}, warnings={warnings}" : "成功：" + model.GetTitle())}");
                if (model is not null)
                {
                    Console.WriteLine($"[6] **有文档后 GetAddInObject → {Describe(TryGetAddIn(application))}**");
                    if (path is not null && File.Exists(path))
                    {
                        var reload = application.LoadAddIn(path);
                        Console.WriteLine($"[7] 有文档后重新 LoadAddIn → {reload}，再取 → {Describe(TryGetAddIn(application))}");
                    }

                    try { application.CloseDoc(model.GetTitle()); } catch { }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("探针异常：" + ex.Message);
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                if (GetPids("SLDWORKS").Except(before).Any())
                {
                    try { application.ExitApp(); } catch { }
                }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
        }
    }

    private static object? TryGetAddIn(ISldWorks application)
    {
        try { return application.GetAddInObject(FeatureWorksProgId); }
        catch (Exception ex) { return "异常：" + ex.Message; }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => text,
        _ => "非空（" + value.GetType().Name + "）",
    };

    /// <summary>与生产代码同一套：从 FeatureWorks 的 CLSID 注册项读 InprocServer32。</summary>
    private static string? ResolvePath(ISldWorks application)
    {
        const string featureWorksClassId = "{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}";
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{featureWorksClassId}\InprocServer32");
        if (key?.GetValue(null) is not string registered || string.IsNullOrWhiteSpace(registered))
            return null;
        var server = System.Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(server))
            return server;
        // 注册的是相对路径 .worksworks.dll，要按 SolidWorks 安装目录拼。
        var executable = application.GetExecutablePath();
        var installDirectory = string.IsNullOrWhiteSpace(executable)
            ? null
            : Path.GetDirectoryName(executable);
        return installDirectory is null ? null : Path.GetFullPath(Path.Combine(installDirectory, server));
    }

    private static int[] GetPids(string processName)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(processName).Select(item => item.Id).ToArray(); }
        catch { return []; }
    }
}
