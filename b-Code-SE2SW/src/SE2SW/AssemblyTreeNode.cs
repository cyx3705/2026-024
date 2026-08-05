using System.Collections.ObjectModel;
using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class AssemblyTreeNode
{
    public AssemblyTreeNode(string displayName, string sourcePath, string stateText)
    {
        DisplayName = displayName;
        SourcePath = sourcePath;
        StateText = stateText;
    }

    public string DisplayName { get; }
    public string SourcePath { get; }
    public string StateText { get; }
    public ObservableCollection<AssemblyTreeNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;

    /// <param name="probe">装配探查返回的实例清单。</param>
    /// <param name="nodes">
    /// V3.3 的装配节点。给出时子装配会标注它将生成的 <c>.SLDASM</c>；
    /// 为空则沿用 V3.0 的展平文案。
    /// </param>
    public static AssemblyTreeNode Build(AssemblyProbeResult probe, IReadOnlyList<AssemblyNode>? nodes = null)
    {
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes ?? [])
            outputs[Path.GetFullPath(node.SourceAssemblyPath)] = Path.GetFileName(node.OutputPath);

        var rootState = outputs.TryGetValue(Path.GetFullPath(probe.SourceAssemblyPath), out var rootOutput)
            ? $"源装配体；生成 {rootOutput}"
            : "源装配体";
        var root = new AssemblyTreeNode(
            Path.GetFileName(probe.SourceAssemblyPath),
            probe.SourceAssemblyPath,
            rootState);

        var byId = new Dictionary<string, AssemblyTreeNode>(StringComparer.OrdinalIgnoreCase);
        var instanceCounts = probe.Occurrences
            .GroupBy(item => (item.ParentId ?? string.Empty) + "|" + Path.GetFullPath(item.SourcePath),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var occurrence in probe.Occurrences)
        {
            var name = occurrence.OccurrenceId.Split('/').LastOrDefault() ?? occurrence.OccurrenceId;
            var states = new List<string>();
            if (occurrence.IsSubAssembly)
            {
                states.Add(outputs.TryGetValue(Path.GetFullPath(occurrence.SourcePath), out var output)
                    ? $"子装配；生成 {output}"
                    : "子装配");
            }
            else
            {
                states.Add("零件");
            }

            var countKey = (occurrence.ParentId ?? string.Empty) + "|" + Path.GetFullPath(occurrence.SourcePath);
            if (instanceCounts.TryGetValue(countKey, out var count) && count > 1)
                states.Add($"同层 ×{count}");
            if (occurrence.IsSuppressed)
                states.Add("抑制，跳过");
            if (occurrence.IsHidden)
                states.Add("隐藏，仍插入");
            if (!string.IsNullOrWhiteSpace(occurrence.Diagnostic))
                states.Add(occurrence.Diagnostic);

            var node = new AssemblyTreeNode(name, occurrence.SourcePath, string.Join("；", states));
            byId[occurrence.OccurrenceId] = node;
            if (occurrence.ParentId is not null && byId.TryGetValue(occurrence.ParentId, out var parent))
                parent.Children.Add(node);
            else
                root.Children.Add(node);
        }

        return root;
    }
}
