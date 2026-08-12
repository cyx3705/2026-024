using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// 将稳定的 Worker 进度合同映射为 UI 文本；不参与 ViewModel 生命周期或调度。
/// </summary>
internal static class ConversionProgressPresenter
{
    public static string GetRowStatus(WorkerEvent workerEvent, string currentStatus)
        => workerEvent.Stage switch
        {
            ConversionStage.SolidEdgeExport => "导出 XT",
            ConversionStage.SolidWorksExport => "导出 XT",
            ConversionStage.SolidWorksImport => "生成 SW",
            ConversionStage.FeatureRecognition => "识别特征",
            ConversionStage.SketchFullyDefine => "定义草图",
            ConversionStage.Completed => "完成",
            ConversionStage.Skipped when workerEvent.ReuseKind == ReuseKind.ExistingSolidWorksPart => "复用 SW",
            ConversionStage.Skipped when workerEvent.ReuseKind == ReuseKind.ExistingXt => "复用 XT",
            ConversionStage.Skipped => "跳过",
            ConversionStage.Failed => "失败",
            ConversionStage.Cancelled => "已取消",
            _ => currentStatus,
        };

    public static string FormatMessage(WorkerEvent workerEvent)
        => workerEvent.ErrorClass == ConversionErrorClass.None
            ? workerEvent.Message
            : $"[{workerEvent.ErrorClass}] {workerEvent.Message}";

    public static void ApplyFeatureOutcome(ConversionFileRow row, FeatureOutcome? outcome)
    {
        if (outcome is null)
            return;
        if (outcome.DegradedToDumbSolid)
        {
            row.FeatureText = outcome.RecognizedFeatureCount == 0 ? "未识别" : $"{outcome.RecognizedFeatureCount} 未生成";
            row.SketchText = "—";
            row.HasFeatureWarning = true;
            return;
        }

        // 部分识别：特征树可用，但有几何没认出来。行里要看得见，否则用户打开零件
        // 发现还杵着一个导入体，会以为产物坏了。
        row.FeatureText = outcome.ResidualImportedBodyCount > 0
            ? $"{outcome.RecognizedFeatureCount} 部分"
            : outcome.RecognizedFeatureCount.ToString();
        row.SketchText = $"{outcome.SketchFullyDefined}/{outcome.SketchTotal}";
        row.HasFeatureWarning = outcome.SketchFullyDefined < outcome.SketchTotal
            || outcome.ResidualImportedBodyCount > 0;
    }
}
