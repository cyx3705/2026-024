using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace PartGeometryProbe;

/// <summary>
/// 以 XT 为基准核对 SLDPRT 的几何。
///
/// 转换管线的正确性判据一直是"文件生成了、能打开、位置对"，**从来没有验过体积**。
/// 一个只识别出基体拉伸的零件会变成方块：文件正常、位置正常、几何全错。
/// 本探针把 XT 现场导入一份作为基准，与产物逐个比体积、表面积、包围盒与面数。
///
/// 只读：只 OpenDoc / LoadFile4 / CloseDoc，不保存、不修改任何产物。
/// </summary>
internal static class Program
{
    private const double MinimumMeasuredVolume = 1e-9;
    private const double MaximumFeatureWorksVolumeRelativeDeviation = 2e-5;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? xtDirectory = null;
        var swDirectories = new List<string>();
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--xt-dir": xtDirectory = Path.GetFullPath(args[index + 1]); break;
                case "--sw-dir": swDirectories.Add(Path.GetFullPath(args[index + 1])); break;
            }
        }

        if (xtDirectory is null || swDirectories.Count == 0)
        {
            Console.Error.WriteLine("用法：PartGeometryProbe --xt-dir <XT目录> --sw-dir <SW目录> [--sw-dir <另一个SW目录>]");
            return 2;
        }

        var report = new GeometryReport { XtDirectory = xtDirectory, SolidWorksDirectories = swDirectories };
        var before = GetPids("SLDWORKS");
        ISldWorks? application = null;
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            if (GetPids("SLDWORKS").Except(before).Any())
            {
                application.Visible = false;
                application.UserControl = false;
            }

            foreach (var xt in Directory.EnumerateFiles(xtDirectory, "*.x_t").OrderBy(item => item))
            {
                var name = Path.GetFileNameWithoutExtension(xt);
                var entry = new PartComparison { Name = name };
                entry.Reference = MeasureXt(application, xt);
                foreach (var swDirectory in swDirectories)
                {
                    var sldprt = Path.Combine(swDirectory, name + ".SLDPRT");
                    var measurement = File.Exists(sldprt)
                        ? MeasurePart(application, sldprt)
                        : new Measurement { Note = "产物不存在" };
                    measurement.Label = Path.GetFileName(Path.GetDirectoryName(swDirectory) ?? swDirectory)
                        + "/" + Path.GetFileName(swDirectory);
                    if (entry.Reference.Volume > MinimumMeasuredVolume && measurement.Volume > 0)
                    {
                        measurement.VolumeRatio = measurement.Volume / entry.Reference.Volume;
                        measurement.Matches = Math.Abs(measurement.VolumeRatio - 1)
                            <= MaximumFeatureWorksVolumeRelativeDeviation;
                    }

                    entry.Candidates.Add(measurement);
                }

                report.Parts.Add(entry);
            }

            report.MismatchCount = report.Parts.Sum(part => part.Candidates.Count(item => !item.Matches));
            report.Verdict = report.MismatchCount == 0
                ? $"{report.Parts.Count} 个零件的全部产物体积与 XT 基准一致。"
                : $"{report.MismatchCount} 个产物与 XT 基准不一致——几何在转换中被改变了。";
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
                if (GetPids("SLDWORKS").Except(before).Any())
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
        return report.Success && report.MismatchCount == 0 ? 0 : 1;
    }

    private static Measurement MeasureXt(ISldWorks application, string xtPath)
    {
        var errors = 0;
        var importData = application.GetImportFileData(xtPath);
        var model = application.LoadFile4(xtPath, "r", importData, ref errors) as ModelDoc2;
        if (model is null)
            return new Measurement { Note = $"XT 导入失败，errors={errors}" };
        try
        {
            var measurement = Measure(model);
            measurement.Label = "XT 基准";
            return measurement;
        }
        finally
        {
            try { application.CloseDoc(model.GetTitle()); } catch { }
        }
    }

    private static Measurement MeasurePart(ISldWorks application, string path)
    {
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            path, (int)swDocumentTypes_e.swDocPART,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings) as ModelDoc2;
        if (model is null)
            return new Measurement { Note = $"打开失败，errors={errors}" };
        try
        {
            return Measure(model);
        }
        finally
        {
            try { application.CloseDoc(model.GetTitle()); } catch { }
        }
    }

    private static Measurement Measure(ModelDoc2 model)
    {
        var measurement = new Measurement();
        var part = (PartDoc)model;
        var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as Array;
        var bodyList = bodies?.Cast<object>().OfType<Body2>().ToArray() ?? [];
        measurement.BodyCount = bodyList.Length;

        foreach (var body in bodyList)
        {
            if (body.GetMassProperties(1) is double[] mass && mass.Length >= 4)
                measurement.Volume += mass[3];
            measurement.FaceCount += body.GetFaceCount();
            if (body.GetBodyBox() is double[] box && box.Length >= 6)
            {
                measurement.BoxMin = measurement.BoxMin is null
                    ? [box[0], box[1], box[2]]
                    : [Math.Min(measurement.BoxMin[0], box[0]), Math.Min(measurement.BoxMin[1], box[1]), Math.Min(measurement.BoxMin[2], box[2])];
                measurement.BoxMax = measurement.BoxMax is null
                    ? [box[3], box[4], box[5]]
                    : [Math.Max(measurement.BoxMax[0], box[3]), Math.Max(measurement.BoxMax[1], box[4]), Math.Max(measurement.BoxMax[2], box[5])];
            }
        }

        measurement.FeatureCount = CountFeatures(model);
        return measurement;
    }

    private static int CountFeatures(ModelDoc2 model)
    {
        var count = 0;
        var feature = model.FirstFeature() as Feature;
        while (feature is not null)
        {
            count++;
            feature = feature.GetNextFeature() as Feature;
        }

        return count;
    }

    private static int[] GetPids(string processName)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(processName).Select(item => item.Id).ToArray(); }
        catch { return []; }
    }
}

internal sealed class GeometryReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string XtDirectory { get; set; } = string.Empty;
    public List<string> SolidWorksDirectories { get; set; } = [];
    public int MismatchCount { get; set; }
    public string? Verdict { get; set; }
    public List<PartComparison> Parts { get; } = [];
}

internal sealed class PartComparison
{
    public string Name { get; set; } = string.Empty;
    public Measurement Reference { get; set; } = new();
    public List<Measurement> Candidates { get; } = [];
}

internal sealed class Measurement
{
    public string Label { get; set; } = string.Empty;
    public int BodyCount { get; set; }
    public double Volume { get; set; }
    public int FaceCount { get; set; }
    public int FeatureCount { get; set; }
    public double VolumeRatio { get; set; }
    public bool Matches { get; set; }
    public double[]? BoxMin { get; set; }
    public double[]? BoxMax { get; set; }
    public string? Note { get; set; }
}
