using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <param name="NeedsExport">
/// 需要从源零件重新导出 XT 的任务。Solid Edge 与 SolidWorks 特征整备都先落到
/// 交付用 <c>.x_t</c>，再进入导入识别。
/// </param>
internal sealed record AssemblyPartReusePlan(
    IReadOnlyList<ConversionJob> ReusableSolidWorksParts,
    IReadOnlyList<ConversionJob> ImportFromExistingXt,
    IReadOnlyList<ConversionJob> NeedsExport,
    // 开启特征识别时被判定为"必须重做"的已有 SLDPRT。它们不是复用项，
    // 但调用方需要知道有哪些，才能如实告诉用户旧产物会被重新生成。
    IReadOnlyList<ConversionJob> RegeneratedForRecognition);

/// <summary>
/// Plans a retry without overwriting any user file. Existing outputs are only reused when
/// they are non-empty, no older than their source part, and (for XT) valid Parasolid text.
/// </summary>
internal static class AssemblyPartReusePlanner
{
    public static AssemblyPartReusePlan Create(
        IReadOnlyList<ConversionJob> jobs,
        CancellationToken cancellationToken,
        bool recognizeFeatures = false,
        ConversionSourceFormat sourceFormat = ConversionSourceFormat.SolidEdge)
    {
        var usesParasolid = ConversionPathLayout.UsesParasolidHandoff(sourceFormat);
        var reusableParts = new List<ConversionJob>();
        var importFromXt = new List<ConversionJob>();
        var needsExport = new List<ConversionJob>();
        var regenerated = new List<ConversionJob>();

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new FileInfo(job.SourcePath);
            if (!source.Exists)
                throw new FileNotFoundException("装配零件源文件不存在。", job.SourcePath);

            if (File.Exists(job.SolidWorksPath))
            {
                // SolidWorks 特征整备不看原零件有没有特征，也不复用上一轮 SLDPRT：
                // 那份产物可能是源特征树拷贝。一律重做：先 XT，再导入识别。
                // Solid Edge 仍只在开启识别时重做；关掉识别才能复用已有 SLDPRT。
                if (sourceFormat == ConversionSourceFormat.SolidWorks || recognizeFeatures)
                {
                    regenerated.Add(job);
                }
                else
                {
                    ValidateReusableFile(job.SolidWorksPath, source, "SLDPRT");
                    reusableParts.Add(job);
                    continue;
                }
            }

            if (usesParasolid && File.Exists(job.XtPath))
            {
                ValidateReusableFile(job.XtPath, source, "XT");
                try
                {
                    _ = FileProbe.VerifyParasolidText(
                        job.XtPath,
                        cancellationToken,
                        TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 坏掉的 XT 必须重导，不能把「格式无效」当成整批失败。
                    needsExport.Add(job);
                    continue;
                }
                importFromXt.Add(job);
                continue;
            }

            needsExport.Add(job);
        }

        return new AssemblyPartReusePlan(reusableParts, importFromXt, needsExport, regenerated);
    }

    private static void ValidateReusableFile(string path, FileInfo source, string kind)
    {
        var output = new FileInfo(path);
        output.Refresh();
        if (output.Length == 0)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputEmpty,
                $"已有 {kind} 为空，拒绝覆盖或复用：{path}");
        }
        if (output.LastWriteTimeUtc < source.LastWriteTimeUtc)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputUnstable,
                $"已有 {kind} 早于源零件，拒绝复用：{path}；请移走旧产物后重试。");
        }
    }
}
