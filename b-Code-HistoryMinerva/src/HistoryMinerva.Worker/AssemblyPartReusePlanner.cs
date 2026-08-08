using SE2SW.Contracts;

namespace SE2SW.Worker;

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
        bool recognizeFeatures = false)
    {
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
                // 现场事故（307 实例 / 54 零件的装配体）：`SW\` 目录里有上一轮留下的哑实体
                // SLDPRT，复用判据只看"存在 + 非空 + 不比源旧"，与识别开关无关，
                // 于是 54 个零件全被判定"已转换、跳过"，一个都没进导入管线——
                // 用户开了特征识别，却一个特征都识别不出来。
                //
                // 已有 SLDPRT 里是否含特征，不打开文档就无法判断；而打开的代价
                // 与重新导入相当。因此开启识别时一律重做：识别本来就必须先导入。
                if (recognizeFeatures)
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

            if (File.Exists(job.XtPath))
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
                catch (Exception ex)
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.OutputFormatInvalid,
                        $"已有 XT 无法安全复用：{job.XtPath}；{ex.Message}",
                        ex);
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
