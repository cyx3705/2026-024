using System.Diagnostics;
using System.Text;

namespace SE2SW.Worker;

internal sealed record OutputFileFacts(
    long Length,
    long StableAfterMilliseconds,
    string? ParasolidFormat = null,
    string? ParasolidSchema = null);

internal static class FileProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    public static OutputFileFacts WaitForStableNonEmptyFile(
        string path,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var maximumWait = timeout ?? DefaultTimeout;
        long previousLength = -1;
        var stableSamples = 0;

        while (stopwatch.Elapsed < maximumWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                Thread.Sleep(100);
                continue;
            }

            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = stream.Length;
                if (length > 0 && length == previousLength)
                {
                    stableSamples++;
                    if (stableSamples >= 3)
                        return new OutputFileFacts(length, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    stableSamples = 0;
                    previousLength = length;
                }
            }
            catch (IOException)
            {
                stableSamples = 0;
            }
            Thread.Sleep(100);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("调用返回后没有生成输出文件。", path);
        if (new FileInfo(path).Length == 0)
            throw new InvalidDataException($"输出文件为空：{path}");
        throw new TimeoutException($"输出文件在 {maximumWait.TotalSeconds:0} 秒内写入未稳定：{path}");
    }

    public static OutputFileFacts VerifyParasolidText(
        string path,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var stable = WaitForStableNonEmptyFile(path, cancellationToken, timeout);
        var header = new byte[2048];
        int read;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = stream.Read(header, 0, header.Length);

        // Latin1 保持字节一一对应；只解析 ASCII 标记，规避本地化 DATE 字段中的 ANSI 字节。
        var text = Encoding.Latin1.GetString(header, 0, read);
        var format = ReadField(text, "FORMAT=");
        if (!string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Parasolid 输出不是文本格式，FORMAT={format ?? "<missing>"}。");

        string? schema = null;
        const string modellerMarker = "modeller version";
        var markerIndex = text.IndexOf(modellerMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var tail = text[(markerIndex + modellerMarker.Length)..];
            var end = tail.IndexOfAny([';', '\r', '\n']);
            if (end >= 0)
                tail = tail[..end];
            schema = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(part => part.StartsWith("SCH_", StringComparison.Ordinal));
        }
        return stable with { ParasolidFormat = format, ParasolidSchema = schema };
    }

    private static string? ReadField(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var start = index + marker.Length;
        var end = text.IndexOfAny([';', '\r', '\n'], start);
        return (end > start ? text[start..end] : text[start..]).Trim();
    }
}
