namespace HistoryMinerva;

/// <summary>配合重建结果怎么说给用户听。只做叙述，不做判断。</summary>
public sealed partial class AssemblyViewModel
{
    /// <summary>状态栏尾巴：一句话说清 56 条关系去了哪里。</summary>
    private string FormatMateSummary()
    {
        if (_mateOutcome is not { RelationTotal: > 0 } mate)
            return string.Empty;
        var failed = mate.FailedUnmatched + mate.FailedAmbiguous + mate.FailedRejected;
        var text = $"；配合重建 {mate.MateRebuilt}/{mate.RelationTotal}";
        if (mate.GroundApplied > 0)
            text += $"（另有 {mate.GroundApplied} 条接地关系落为固定）";
        if (failed > 0)
            text += $"，{failed} 条未建立";
        if (mate.ComponentsLeftFixed > 0)
            text += $"，{mate.ComponentsLeftFixed} 个组件保持固定";
        return text;
    }

    /// <summary>
    /// 把逐条诊断并进警告栏。V3.5 的承诺是"建不起来的如实报告"——
    /// 报告只写进日志、用户看不见的话，这个承诺就没兑现。
    /// </summary>
    private void AppendMateDiagnostics()
    {
        if (_mateOutcome is not { Diagnostics.Count: > 0 } mate)
            return;
        var summary = string.Join("；", mate.Diagnostics.Take(6));
        if (mate.Diagnostics.Count > 6)
            summary += $"；……另有 {mate.Diagnostics.Count - 6} 条，详见转换日志";
        WarningSummary = string.IsNullOrWhiteSpace(WarningSummary)
            ? summary
            : WarningSummary + "；" + summary;
    }
}
