namespace SE2SW.Worker;

internal static class TemporaryOutput
{
    public static string For(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException($"无法解析输出目录：{finalPath}");
        var baseName = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $".{baseName}.{Guid.NewGuid():N}.tmp{extension}");
    }

    /// <summary>
    /// 默认**不覆盖**：这是转换器"绝不动用户已有产物"的最后一道闸。
    ///
    /// 只有调用方已经明确判定该产物必须重做时才传 <paramref name="overwrite"/>。
    /// 现场事故：开启特征识别后零件必须重新导入，请求里 Overwrite 也置了 true，
    /// 但落盘这一步写死了 false，53 个零件全部止于「当文件已存在时，无法创建该文件」。
    /// 同卷内 File.Move 覆盖是原子替换，不会留下半个文件。
    /// </summary>
    public static void Commit(string temporaryPath, string finalPath, bool overwrite = false)
        => File.Move(temporaryPath, finalPath, overwrite);

    public static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
