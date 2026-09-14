using HistoryMinerva.Bom;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.10.4 整体打包：外购件品牌联网查询。在写 BOM 之前跑，结果进「品牌」列与外购件清单 H 列。
/// </summary>
public sealed partial class AssemblyViewModel
{
    /// <summary>
    /// 同时在查的种数。每一种都是「一次模型调用 + 若干次搜索」，几十种串行要十几分钟；
    /// 再往上并发，就是在跟搜索服务的限流赌运气。
    /// </summary>
    private const int BrandLookupConcurrency = 3;

    /// <summary>
    /// 连续失败到这个数就不再往下查。Apollo 没装、没配密钥或断网时每一种都会以同一个原因失败，
    /// 再发几十次只是让用户多等几十个超时，最后得到的还是同一句话。
    /// </summary>
    private const int BrandFailureCircuit = 3;

    private readonly object _brandGate = new();

    /// <summary>
    /// 本页面实例里已经确定的品牌（含确认查不到的 N/A），键见 <see cref="PurchasedBrandLookup.Key"/>。
    /// 同一台设备改完模型重新打包不必再花一轮钱；**失败不缓存**——配好密钥再打包就该真的去查。
    /// </summary>
    private readonly Dictionary<string, string> _brandCache = new(StringComparer.Ordinal);

    /// <summary>外购件品牌查询。UI 模块把它接到命令总线；为 null 时（未接宿主）品牌一律 N/A。</summary>
    internal Func<PackagePartEntry, CancellationToken, Task<BrandAnswer>>? BrandLookup { get; set; }

    /// <summary>表格上的初值：查过的显示缓存，没查过的外购件留空，机加件恒为空。</summary>
    private string CachedBrandText(PackagePartEntry entry)
    {
        if (entry.Category != PackagePartCategory.Purchased)
            return string.Empty;
        lock (_brandGate)
            return _brandCache.GetValueOrDefault(PurchasedBrandLookup.Key(entry)) ?? string.Empty;
    }

    private async Task<PurchasedBrandSummary> LookupPurchasedBrandsAsync(
        PackagePlan plan,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan.Purchased.Count == 0)
            return new PurchasedBrandSummary(byId, 0, 0, null);

        var lookup = BrandLookup;
        var groups = plan.Purchased
            .GroupBy(PurchasedBrandLookup.Key, StringComparer.Ordinal)
            .ToArray();
        int toQuery;
        lock (_brandGate)
        {
            toQuery = groups.Count(group =>
                PurchasedBrandLookup.IsQueryable(group.First()) && !_brandCache.ContainsKey(group.Key));
        }

        if (toQuery > 0 && lookup is not null)
        {
            QueueUiUpdate(() => StatusText = $"正在联网查询 {toQuery} 种外购件的品牌");
            _operationProgress?.Report($"正在联网查询 {toQuery} 种外购件的品牌（HistoryApollo）");
        }

        var found = 0;
        var notAvailable = 0;
        var done = 0;
        var consecutiveFailures = 0;
        var tripped = false;
        string? firstFailure = toQuery > 0 && lookup is null ? "品牌查询没有接入命令总线" : null;

        using var gate = new SemaphoreSlim(BrandLookupConcurrency);
        async Task ResolveAsync(IGrouping<string, PackagePartEntry> group)
        {
            var entry = group.First();
            var brand = PurchasedBrandLookup.NotAvailable;
            string? cached;
            lock (_brandGate)
                cached = _brandCache.GetValueOrDefault(group.Key);

            if (cached is not null)
            {
                brand = cached;
            }
            else if (lookup is not null && PurchasedBrandLookup.IsQueryable(entry))
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool skip;
                    lock (_brandGate)
                        skip = tripped;
                    if (!skip)
                    {
                        var answer = await lookup(entry, cancellationToken).ConfigureAwait(false);
                        // 总线把取消翻译成失败回执，不抛异常；不在这里补一刀，取消就会被记成「查询失败」。
                        cancellationToken.ThrowIfCancellationRequested();
                        brand = answer.Brand;
                        int progress;
                        lock (_brandGate)
                        {
                            progress = ++done;
                            if (answer.Failed)
                            {
                                firstFailure ??= answer.Failure;
                                tripped |= ++consecutiveFailures >= BrandFailureCircuit;
                            }
                            else
                            {
                                consecutiveFailures = 0;
                                _brandCache[group.Key] = answer.Brand;
                            }
                        }

                        _operationProgress?.Report(
                            $"品牌 {progress}/{toQuery}：{PurchasedBrandLookup.QuerySpecification(entry)} → {brand}"
                            + (answer.Failed ? $"（{answer.Failure}）" : string.Empty));
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            lock (_brandGate)
            {
                foreach (var member in group)
                    byId[member.Id] = brand;
                if (brand == PurchasedBrandLookup.NotAvailable)
                    notAvailable++;
                else
                    found++;
            }

            QueueUiUpdate(() =>
            {
                foreach (var member in group)
                {
                    if (FindRow(member.Id) is { } row)
                        row.BrandText = brand;
                }
            });
        }

        await Task.WhenAll(groups.Select(ResolveAsync)).ConfigureAwait(false);
        return new PurchasedBrandSummary(byId, found, notAvailable, firstFailure);
    }

    /// <summary>一轮品牌查询的结论。计数按「种」（同规格同名称算一种），不按件。</summary>
    private sealed record PurchasedBrandSummary(
        IReadOnlyDictionary<string, string> ById,
        int Found,
        int NotAvailable,
        string? FirstFailure)
    {
        /// <summary>接在打包结论后面的那半句；没有外购件时为空串。</summary>
        public string Describe()
            => Found + NotAvailable == 0
                ? string.Empty
                : $"；外购件品牌查到 {Found} 种、N/A {NotAvailable} 种"
                  + (FirstFailure is null ? string.Empty : $"（查询失败：{FirstFailure}）");
    }
}
