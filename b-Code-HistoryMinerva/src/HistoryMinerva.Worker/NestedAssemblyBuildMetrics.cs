using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// Keeps nested-assembly counters auditable. A reused assembly is not reopened during the
/// current run, so its planned children must never be reported as newly inserted or fixed.
/// </summary>
internal sealed class NestedAssemblyBuildMetrics
{
    private int _componentTotal;
    private int _componentInserted;
    private int _componentFixed;
    private int _reusedAssemblyCount;
    private int _reusedAssemblyPlannedComponentCount;

    public NestedAssemblyBuildMetrics(int skippedSuppressed)
    {
        if (skippedSuppressed < 0)
            throw new ArgumentOutOfRangeException(nameof(skippedSuppressed));
        SkippedSuppressed = skippedSuppressed;
    }

    public int ComponentTotal => _componentTotal;
    public int ComponentInserted => _componentInserted;
    public int ComponentFixed => _componentFixed;
    public int ReusedAssemblyCount => _reusedAssemblyCount;
    public int ReusedAssemblyPlannedComponentCount => _reusedAssemblyPlannedComponentCount;
    public int SkippedSuppressed { get; }

    public void AddPlannedNode(AssemblyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _componentTotal += node.Children.Count;
    }

    public void AddReusedNode(AssemblyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _reusedAssemblyCount++;
        _reusedAssemblyPlannedComponentCount += node.Children.Count;
    }

    public void AddBuiltNode(int inserted, int fixedCount)
    {
        if (inserted < 0)
            throw new ArgumentOutOfRangeException(nameof(inserted));
        if (fixedCount < 0 || fixedCount > inserted)
            throw new ArgumentOutOfRangeException(nameof(fixedCount));
        _componentInserted += inserted;
        _componentFixed += fixedCount;
    }
}
