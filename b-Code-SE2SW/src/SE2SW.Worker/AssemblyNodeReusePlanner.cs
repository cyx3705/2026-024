using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// V3.3 §3.7：装配产物的复用判定。
///
/// 零件的规则是"产物比源文件新"。这条对装配**不成立**：子装配 <c>.asm</c> 本身没改，
/// 但它里面的某个 <c>.par</c> 改了，产物就已经过期。因此装配产物必须比它**递归依赖的每一个文件**都新。
///
/// V3.6.6：判不出"可安全复用"时**一律重做**，不再抛异常要求用户移走旧产物。
///
/// 原来的拒绝式策略是为了防"输出目录里混着来历不明的同名 SLDASM"，
/// 但那是从冒烟环境推出来的假设——真实用法是转换到空目录（或本工具自己的历史产物）。
/// 代价却很实在：UI 默认开着"重建装配关系"，只要目录里已有 SLDASM 就整轮失败，
/// 重跑一次都做不到。既然产物本来就是本工具生成的，重做比拒绝合理。
/// </summary>
internal static class AssemblyNodeReusePlanner
{
    /// <returns><c>true</c> 表示可以直接复用已有产物，跳过生成；<c>false</c> 表示重做。</returns>
    public static bool CanReuse(AssemblyNode node, bool requireMateRebuild = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!File.Exists(node.OutputPath))
            return false;

        // 时间戳只能证明几何依赖没有更新，不能证明这个 SLDASM 曾按当前关系集建立过配合。
        // 复用会得到一个"成功"的旧装配却根本没调用 SolidWorksMateRebuilder——所以必须重做。
        if (requireMateRebuild)
            return false;

        var output = new FileInfo(node.OutputPath);
        output.Refresh();
        if (output.Length == 0)
            return false;

        foreach (var dependency in Enumerable.Repeat(node.SourceAssemblyPath, 1).Concat(node.Dependencies))
        {
            var source = new FileInfo(dependency);
            // 依赖读不到修改时间，或产物早于任一依赖，都证明不了产物是新的：重做。
            if (!source.Exists || output.LastWriteTimeUtc < source.LastWriteTimeUtc)
                return false;
        }

        return true;
    }
}
