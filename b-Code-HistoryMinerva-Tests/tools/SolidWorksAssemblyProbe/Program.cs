using System.Text;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksAssemblyProbe;

/// <summary>
/// SW 自转换管线的只读探针：把一个 <c>.SLDASM</c> 读成"组件树 + 配合"两张表，
/// 用实测数据回答契约设计必须先确定的四件事：
///
///   1. 顶层组件的 <c>Transform2</c> 是不是本装配坐标系下的局部矩阵；
///   2. 配合能不能从特征树里稳定取到（MateGroup → IMate2），类型/对齐/距离怎么读；
///   3. <c>IMateEntity2</c> 的 <c>Reference</c> 能不能取到面，
///      面几何（零件系 → 乘组件变换）与 <c>EntityParams</c> 是否一致；
///   4. 哪些组件是固定的（IsFixed），能不能当作"接地"处理。
///
/// 只读：Silent + ReadOnly 打开，不保存、不修改、不新建。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? assemblyPath = null;
        string? outputPath = null;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--assembly": assemblyPath = Path.GetFullPath(args[index + 1]); break;
                case "--out": outputPath = Path.GetFullPath(args[index + 1]); break;
            }
        }

        if (assemblyPath is null)
        {
            Console.Error.WriteLine("用法：SolidWorksAssemblyProbe --assembly <绝对 .SLDASM 路径> [--out <报告.json>]");
            return 2;
        }

        ISldWorks? application = null;
        var report = new AssemblyReport { SourcePath = assemblyPath };
        try
        {
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false)
                ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            application = (ISldWorks?)Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。");
            report.SolidWorksVersion = application.RevisionNumber();

            Inspect(application, assemblyPath, report);
            Console.WriteLine(Summarize(report));
            if (outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
                Console.WriteLine("报告：" + outputPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"探测失败：{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            if (application is not null)
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(application);
        }
    }

    private static void Inspect(ISldWorks application, string assemblyPath, AssemblyReport report)
    {
        var errors = 0;
        var warnings = 0;
        var model = application.OpenDoc6(
            assemblyPath,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
            string.Empty,
            ref errors,
            ref warnings);
        report.OpenErrors = errors;
        report.OpenWarnings = warnings;
        if (model is null)
            throw new InvalidOperationException($"OpenDoc6 返回 null，errors={errors}, warnings={warnings}");

        try
        {
            report.Title = model.GetTitle();
            var assembly = (AssemblyDoc)model;
            report.ResolvedLightweight = assembly.ResolveAllLightWeightComponents(false);

            var topLevel = assembly.GetComponents(true) as object[] ?? [];
            report.TopLevelCount = topLevel.Length;
            foreach (var item in topLevel)
            {
                if (item is Component2 component)
                    report.Children.Add(ReadComponent(component));
            }

            var all = assembly.GetComponents(false) as object[] ?? [];
            report.AllComponentCount = all.Length;
            foreach (var item in all)
            {
                if (item is Component2 component)
                    report.AllComponents.Add(ReadComponent(component));
            }

            ReadMates(model, report);
        }
        finally
        {
            application.CloseDoc(model.GetTitle());
        }
    }

    private static ComponentReport ReadComponent(Component2 component)
    {
        var transform = component.Transform2;
        var raw = transform?.ArrayData as double[];
        var children = component.GetChildren() as object[] ?? [];
        return new ComponentReport
        {
            Name2 = component.Name2,
            PathName = component.GetPathName(),
            IsSuppressed = component.IsSuppressed(),
            Visible = component.Visible,
            IsFixed = component.IsFixed(),
            IsVirtual = component.IsVirtual,
            Suppression = component.GetSuppression2(),
            ChildCount = children.Length,
            ReferencedConfiguration = component.ReferencedConfiguration,
            Transform2 = raw,
        };
    }

    private static void ReadMates(IModelDoc2 model, AssemblyReport report)
    {
        var feature = (Feature?)model.FirstFeature();
        while (feature is not null)
        {
            var typeName = SafeTypeName(feature);
            report.TopLevelFeatureTypes.Add($"{feature.Name} [{typeName}]");
            if (string.Equals(typeName, "MateGroup", StringComparison.OrdinalIgnoreCase))
            {
                var sub = (Feature?)feature.GetFirstSubFeature();
                var index = 0;
                while (sub is not null)
                {
                    report.Mates.Add(ReadMate(sub, index++));
                    sub = (Feature?)sub.GetNextSubFeature();
                }
            }

            feature = (Feature?)feature.GetNextFeature();
        }
    }

    private static MateReport ReadMate(Feature feature, int index)
    {
        var report = new MateReport
        {
            Index = index,
            FeatureName = feature.Name,
            FeatureTypeName = SafeTypeName(feature),
            IsSuppressed = Try(() => feature.IsSuppressed(), false),
        };

        object? specific = null;
        try
        {
            specific = feature.GetSpecificFeature2();
        }
        catch (Exception ex)
        {
            report.Diagnostic = "GetSpecificFeature2 失败：" + ex.Message;
            return report;
        }

        if (specific is not Mate2 mate)
        {
            report.Diagnostic = "子特征不是 IMate2：" + (specific?.GetType().Name ?? "null");
            return report;
        }

        report.MateType = Try(() => mate.Type, -1);
        report.MateTypeName = Enum.IsDefined(typeof(swMateType_e), report.MateType)
            ? ((swMateType_e)report.MateType).ToString()
            : "?";
        report.Alignment = Try(() => mate.Alignment, -1);
        report.Flipped = Try(() => mate.Flipped, false);
        report.CanBeFlipped = Try(() => mate.CanBeFlipped, false);
        report.ConcentricAlignmentType = Try(() => mate.GetConcentricAlignmentType(), -1);
        report.MinimumVariation = Try(() => mate.MinimumVariation, double.NaN);
        report.MaximumVariation = Try(() => mate.MaximumVariation, double.NaN);
        // IMate2 没有 Distance/Angle 属性：距离与角度配合的数值挂在它的显示尺寸上。
        report.DimensionValue = ReadDimensionValue(mate, out var dimensionDiagnostic);
        report.DimensionDiagnostic = dimensionDiagnostic;
        report.MateEntityCount = Try(() => mate.GetMateEntityCount(), -1);

        for (var entityIndex = 0; entityIndex < Math.Max(0, report.MateEntityCount); entityIndex++)
        {
            var entity = Try(() => (MateEntity2?)mate.MateEntity(entityIndex), null);
            report.Entities.Add(ReadMateEntity(entity, entityIndex));
        }

        return report;
    }

    /// <summary>
    /// 距离/角度配合的数值：IMate2 自己不带，挂在 DisplayDimension → Dimension 上。
    /// SystemValue 是国际单位（米 / 弧度）。
    /// </summary>
    private static double ReadDimensionValue(Mate2 mate, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            var display = mate.DisplayDimension2[0];
            if (display is null)
            {
                diagnostic = "无 DisplayDimension（重合/同心一类无数值配合）。";
                return double.NaN;
            }

            var dimension = display.GetDimension2(0);
            if (dimension is null)
            {
                diagnostic = "DisplayDimension 无 Dimension。";
                return double.NaN;
            }

            return dimension.GetSystemValue3(
                (int)swInConfigurationOpts_e.swThisConfiguration, null) is double[] { Length: > 0 } values
                ? values[0]
                : dimension.SystemValue;
        }
        catch (Exception ex)
        {
            diagnostic = "读取尺寸失败：" + ex.Message;
            return double.NaN;
        }
    }

    private static MateEntityReport ReadMateEntity(MateEntity2? entity, int index)
    {
        var report = new MateEntityReport { Index = index };
        if (entity is null)
        {
            report.Diagnostic = "MateEntity 返回 null。";
            return report;
        }

        report.ReferenceType = Try(() => entity.ReferenceType, -1);
        report.ReferenceTypeName = Enum.IsDefined(typeof(swMateEntity2ReferenceType_e), report.ReferenceType)
            ? ((swMateEntity2ReferenceType_e)report.ReferenceType).ToString()
            : "?";
        report.EntityParams = Try(() => entity.EntityParams as double[], null);

        var component = Try(() => entity.ReferenceComponent as Component2, null);
        if (component is not null)
        {
            report.ComponentName = Try(() => component.Name2, null);
            report.ComponentPath = Try(() => component.GetPathName(), null);
            report.ComponentTransform = Try(() => component.Transform2?.ArrayData as double[], null);
        }

        var reference = Try(() => entity.Reference, null);
        report.ReferenceRuntimeType = reference?.GetType().Name;
        if (reference is Face2 face)
        {
            report.ReferenceIsFace = true;
            var surface = Try(() => face.GetSurface() as Surface, null);
            if (surface is not null)
            {
                report.SurfaceIsPlane = Try(() => surface.IsPlane(), false);
                report.SurfaceIsCylinder = Try(() => surface.IsCylinder(), false);
                report.SurfaceIsCone = Try(() => surface.IsCone(), false);
                if (report.SurfaceIsPlane)
                    report.SurfaceParams = Try(() => surface.PlaneParams as double[], null);
                else if (report.SurfaceIsCylinder)
                    report.SurfaceParams = Try(() => surface.CylinderParams as double[], null);
                else if (report.SurfaceIsCone)
                    report.SurfaceParams = Try(() => surface.ConeParams as double[], null);
            }
        }
        else if (reference is Entity generic)
        {
            report.ReferenceEntityType = Try(() => generic.GetType(), -1);
        }

        return report;
    }

    private static string SafeTypeName(Feature feature)
    {
        try { return feature.GetTypeName2(); }
        catch { return "(读取失败)"; }
    }

    private static T Try<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static string Summarize(AssemblyReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"装配：{report.SourcePath}");
        builder.AppendLine($"SW 版本：{report.SolidWorksVersion}；打开 errors={report.OpenErrors} warnings={report.OpenWarnings}；轻化解析={report.ResolvedLightweight}");
        builder.AppendLine($"一级组件 {report.TopLevelCount} 个，全部组件 {report.AllComponentCount} 个，配合 {report.Mates.Count} 条");
        foreach (var child in report.Children)
        {
            builder.AppendLine(
                $"  · {child.Name2} | {Path.GetFileName(child.PathName)} | 固定={child.IsFixed} 抑制={child.IsSuppressed} "
                + $"可见={child.Visible} 子件={child.ChildCount} 配置={child.ReferencedConfiguration}");
            builder.AppendLine($"      T2=[{Format(child.Transform2)}]");
        }

        foreach (var mate in report.Mates)
        {
            builder.AppendLine(
                $"  # {mate.Index} {mate.FeatureName} [{mate.FeatureTypeName}] 类型={mate.MateType}({mate.MateTypeName}) "
                + $"对齐={mate.Alignment} 翻转={mate.Flipped} 尺寸={mate.DimensionValue:G6}({mate.DimensionDiagnostic}) "
                + $"同心对齐={mate.ConcentricAlignmentType} 抑制={mate.IsSuppressed} 实体数={mate.MateEntityCount} {mate.Diagnostic}");
            foreach (var entity in mate.Entities)
            {
                builder.AppendLine(
                    $"      [{entity.Index}] 引用类型={entity.ReferenceType}({entity.ReferenceTypeName}) "
                    + $"运行时={entity.ReferenceRuntimeType} 面={entity.ReferenceIsFace} "
                    + $"平面={entity.SurfaceIsPlane} 圆柱={entity.SurfaceIsCylinder} 圆锥={entity.SurfaceIsCone} "
                    + $"组件={entity.ComponentName} {entity.Diagnostic}");
                builder.AppendLine($"          EntityParams=[{Format(entity.EntityParams)}]");
                builder.AppendLine($"          SurfaceParams=[{Format(entity.SurfaceParams)}]");
            }
        }

        return builder.ToString();
    }

    private static string Format(double[]? values)
        => values is null ? "" : string.Join(", ", values.Select(value => value.ToString("G6")));

    private sealed class AssemblyReport
    {
        public string SourcePath { get; set; } = string.Empty;
        public string? SolidWorksVersion { get; set; }
        public string? Title { get; set; }
        public int OpenErrors { get; set; }
        public int OpenWarnings { get; set; }
        public int ResolvedLightweight { get; set; }
        public int TopLevelCount { get; set; }
        public int AllComponentCount { get; set; }
        public List<ComponentReport> Children { get; } = [];
        public List<ComponentReport> AllComponents { get; } = [];
        public List<MateReport> Mates { get; } = [];
        public List<string> TopLevelFeatureTypes { get; } = [];
    }

    private sealed class ComponentReport
    {
        public string? Name2 { get; set; }
        public string? PathName { get; set; }
        public bool IsSuppressed { get; set; }
        public int Visible { get; set; }
        public bool IsFixed { get; set; }
        public bool IsVirtual { get; set; }
        public int Suppression { get; set; }
        public int ChildCount { get; set; }
        public string? ReferencedConfiguration { get; set; }
        public double[]? Transform2 { get; set; }
    }

    private sealed class MateReport
    {
        public int Index { get; set; }
        public string? FeatureName { get; set; }
        public string? FeatureTypeName { get; set; }
        public bool IsSuppressed { get; set; }
        public int MateType { get; set; }
        public string? MateTypeName { get; set; }
        public int Alignment { get; set; }
        public bool Flipped { get; set; }
        public bool CanBeFlipped { get; set; }
        public int ConcentricAlignmentType { get; set; }
        public double MinimumVariation { get; set; }
        public double MaximumVariation { get; set; }
        public double DimensionValue { get; set; }
        public string? DimensionDiagnostic { get; set; }
        public int MateEntityCount { get; set; }
        public string? Diagnostic { get; set; }
        public List<MateEntityReport> Entities { get; } = [];
    }

    private sealed class MateEntityReport
    {
        public int Index { get; set; }
        public int ReferenceType { get; set; }
        public string? ReferenceTypeName { get; set; }
        public double[]? EntityParams { get; set; }
        public string? ComponentName { get; set; }
        public string? ComponentPath { get; set; }
        public double[]? ComponentTransform { get; set; }
        public string? ReferenceRuntimeType { get; set; }
        public bool ReferenceIsFace { get; set; }
        public int ReferenceEntityType { get; set; }
        public bool SurfaceIsPlane { get; set; }
        public bool SurfaceIsCylinder { get; set; }
        public bool SurfaceIsCone { get; set; }
        public double[]? SurfaceParams { get; set; }
        public string? Diagnostic { get; set; }
    }
}
