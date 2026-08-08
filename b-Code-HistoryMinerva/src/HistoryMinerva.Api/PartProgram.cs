namespace SWuse.Api;

/// <summary>标记一个工作区中的唯一零件构建入口。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SwuseEntryAttribute : Attribute
{
}

/// <summary>用户零件程序的基类。Worker 只会执行标记为 <see cref="SwuseEntryAttribute"/> 的实现。</summary>
public abstract class PartProgram
{
    public abstract void Build(PartBuilder part);
}

/// <summary>支持的草图基准面。</summary>
public enum ReferencePlane
{
    Front,
    Top,
    Right,
}

/// <summary>仅由 <see cref="PartBuilder"/> 创建的草图句柄。</summary>
public sealed class SketchRef
{
    internal SketchRef(string name) => Name = name;

    public string Name { get; }
}

/// <summary>仅由 <see cref="PartBuilder"/> 创建的特征句柄。</summary>
public sealed class FeatureRef
{
    internal FeatureRef(string name) => Name = name;

    public string Name { get; }
}

/// <summary>Worker 内部的几何执行后端；用户代码只通过 <see cref="PartBuilder"/> 间接使用它。</summary>
public interface IPartBackend
{
    SketchRef BeginSketch(string name, ReferencePlane plane);
    void EndSketch(SketchRef sketch);
    void AddLine(SketchRef sketch, double x1, double y1, double x2, double y2);
    void AddCircle(SketchRef sketch, double x, double y, double radius);
    FeatureRef Extrude(string name, SketchRef sketch, double depthMeters, bool reverse);
    FeatureRef CutExtrude(string name, SketchRef sketch, double depthMeters, bool reverse);
}

/// <summary>受控的零件建模 API。所有线性长度均为米。</summary>
public sealed class PartBuilder
{
    private readonly IPartBackend _backend;

    public PartBuilder(IPartBackend backend)
        => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public SketchRef Sketch(string name, ReferencePlane plane, Action<SketchBuilder> draw)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("草图名称不能为空。", nameof(name));
        ArgumentNullException.ThrowIfNull(draw);
        var sketch = _backend.BeginSketch(name.Trim(), plane);
        try
        {
            draw(new SketchBuilder(_backend, sketch));
            return sketch;
        }
        finally
        {
            _backend.EndSketch(sketch);
        }
    }

    public FeatureRef Extrude(string name, SketchRef sketch, double depthMeters, bool reverse = false)
    {
        ValidateFeature(name, sketch, depthMeters);
        return _backend.Extrude(name.Trim(), sketch, depthMeters, reverse);
    }

    public FeatureRef CutExtrude(string name, SketchRef sketch, double depthMeters, bool reverse = false)
    {
        ValidateFeature(name, sketch, depthMeters);
        return _backend.CutExtrude(name.Trim(), sketch, depthMeters, reverse);
    }

    private static void ValidateFeature(string name, SketchRef sketch, double depthMeters)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("特征名称不能为空。", nameof(name));
        ArgumentNullException.ThrowIfNull(sketch);
        if (!double.IsFinite(depthMeters) || depthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(depthMeters), "深度必须是有限的正米数。");
    }
}

/// <summary>一个打开草图中的二维几何 API。单位为米。</summary>
public sealed class SketchBuilder
{
    private readonly IPartBackend _backend;
    private readonly SketchRef _sketch;

    internal SketchBuilder(IPartBackend backend, SketchRef sketch)
    {
        _backend = backend;
        _sketch = sketch;
    }

    public void Line(double x1, double y1, double x2, double y2)
    {
        ValidateFinite(x1, nameof(x1));
        ValidateFinite(y1, nameof(y1));
        ValidateFinite(x2, nameof(x2));
        ValidateFinite(y2, nameof(y2));
        if (x1 == x2 && y1 == y2)
            throw new ArgumentException("线段两端不能重合。");
        _backend.AddLine(_sketch, x1, y1, x2, y2);
    }

    public void Circle(double x, double y, double radiusMeters)
    {
        ValidateFinite(x, nameof(x));
        ValidateFinite(y, nameof(y));
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), "半径必须是有限的正米数。");
        _backend.AddCircle(_sketch, x, y, radiusMeters);
    }

    public void Rectangle(double left, double bottom, double widthMeters, double heightMeters)
    {
        if (!double.IsFinite(widthMeters) || widthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthMeters));
        if (!double.IsFinite(heightMeters) || heightMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightMeters));
        Line(left, bottom, left + widthMeters, bottom);
        Line(left + widthMeters, bottom, left + widthMeters, bottom + heightMeters);
        Line(left + widthMeters, bottom + heightMeters, left, bottom + heightMeters);
        Line(left, bottom + heightMeters, left, bottom);
    }

    public void CenteredRectangle(double centerX, double centerY, double widthMeters, double heightMeters)
        => Rectangle(centerX - widthMeters / 2d, centerY - heightMeters / 2d, widthMeters, heightMeters);

    private static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "坐标必须是有限数值。");
    }
}
