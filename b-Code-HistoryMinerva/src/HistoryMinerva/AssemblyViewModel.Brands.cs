using System.IO;
using HistoryMinerva.Bom;
using HistoryMinerva.Contracts;

namespace HistoryMinerva;

/// <summary>
/// V4.10.4 整体打包：外购件品牌联网查询。在写 BOM 之前跑，结果进「品牌」列与外购件清单 H 列。
/// V4.10.5：页面开关可以整个关掉它（DEC-066）。模型的思考、搜索与答复由 HistoryApollo 自己逐轮写进控制台，
/// 这里只报每一种的结论和没发起调用的原因。
/// V4.16：文件名里没有名称的外购件，同一次调用顺带查名称，进「名称」列与外购件清单 E 列。
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
    /// 本页面实例里已经确定的品牌（含确认查不到的 N/A）与顺带查到的名称，键见 <see cref="PurchasedBrandLookup.Key"/>。
    /// 同一台设备改完模型重新打包不必再花一轮钱；**失败不缓存**——配好密钥再打包就该真的去查。
    /// </summary>
    private readonly Dictionary<string, LookupResult> _brandCache = new(StringComparer.Ordinal);

    /// <summary>外购件品牌查询。UI 模块把它接到命令总线；为 null 时（未接宿主）品牌一律 N/A。</summary>
    internal Func<PackagePartEntry, CancellationToken, Task<BrandAnswer>>? BrandLookup { get; set; }

    /// <summary>V4.16：打包的六个分步开关，缺省全开。</summary>
    internal PackageSteps PackageSteps { get; private set; } = PackageSteps.All;

    /// <summary>
    /// 「AI 查品牌」开关（V4.10.5，DEC-066）。关着时打包不调模型、不搜索，品牌列与外购件清单 H 列留空，
    /// **也不拿缓存填**：用户关掉它，表上看到的就该是「这一轮没查」，而不是一份说不清来历的旧结果。
    /// </summary>
    internal bool BrandLookupEnabled => PackageSteps.BrandLookup;

    /// <summary>拨开关：记下新值，把表里外购件的品牌与名称列立即换成与之相符的显示，并重算打包按钮。</summary>
    internal void SetPackageSteps(PackageSteps steps)
    {
        PackageSteps = steps;
        foreach (var entry in _packagePlan?.Purchased ?? Array.Empty<PackagePartEntry>())
        {
            if (FindRow(entry.Id) is { } row)
            {
                row.BrandText = CachedBrandText(entry);
                row.PartName = DisplayName(entry);
            }
        }

        OnPropertyChanged(nameof(CanConvert));
    }

    internal void SetBrandLookupEnabled(bool enabled) => SetPackageSteps(PackageSteps with { BrandLookup = enabled });

    /// <summary>表格上的初值：开关开着时显示缓存，没查过的外购件留空；机加件恒为空。</summary>
    private string CachedBrandText(PackagePartEntry entry)
        => CachedLookup(entry)?.Brand ?? string.Empty;

    /// <summary>名称列：文件名里有名称就是它；没有时显示 AI 查到的（开关开着且查过），否则留空。</summary>
    private string DisplayName(PackagePartEntry entry)
        => PurchasedBrandLookup.NeedsName(entry) ? CachedLookup(entry)?.Name ?? string.Empty : entry.PartName;

    private LookupResult? CachedLookup(PackagePartEntry entry)
    {
        if (entry.Category != PackagePartCategory.Purchased || !BrandLookupEnabled)
            return null;
        lock (_brandGate)
            return _brandCache.GetValueOrDefault(PurchasedBrandLookup.Key(entry));
    }

    private async Task<PurchasedBrandSummary> LookupPurchasedBrandsAsync(
        PackagePlan plan,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan.Purchased.Count == 0)
            return new PurchasedBrandSummary(byId, namesById, 0, 0, 0, null);

        if (!BrandLookupEnabled)
        {
            QueueUiUpdate(() =>
            {
                foreach (var entry in plan.Purchased)
                {
                    if (FindRow(entry.Id) is { } row)
                    {
                        row.BrandText = string.Empty;
                        row.PartName = entry.PartName;
                    }
                }
            });
            _operationProgress?.Report("AI 查品牌已关闭：本次不调用模型、不联网搜索，品牌列与外购件清单 H 列留空，缺名称的外购件名称也留空");
            return new PurchasedBrandSummary(byId, namesById, 0, 0, 0, null, Skipped: true);
        }

        var lookup = BrandLookup;
        var groups = plan.Purchased
            .GroupBy(PurchasedBrandLookup.Key, StringComparer.Ordinal)
            .ToArray();
        int toQuery;
        int nameQueries;
        lock (_brandGate)
        {
            var pending = groups
                .Where(group => PurchasedBrandLookup.IsQueryable(group.First()) && !_brandCache.ContainsKey(group.Key))
                .ToArray();
            toQuery = pending.Length;
            nameQueries = pending.Count(group => PurchasedBrandLookup.NeedsName(group.First()));
        }

        if (toQuery > 0 && lookup is not null)
        {
            var names = nameQueries > 0 ? $"（其中 {nameQueries} 种文件名里没有名称，顺带查名称）" : string.Empty;
            QueueUiUpdate(() => StatusText = $"正在联网查询 {toQuery} 种外购件的品牌{names}");
            _operationProgress?.Report($"正在联网查询 {toQuery} 种外购件的品牌{names}（模型的思考、搜索与答复由 HistoryApollo 逐轮输出）");
        }

        var found = 0;
        var notAvailable = 0;
        var namesFound = 0;
        var done = 0;
        var consecutiveFailures = 0;
        var tripped = false;
        string? firstFailure = toQuery > 0 && lookup is null ? "品牌查询没有接入命令总线" : null;

        using var gate = new SemaphoreSlim(BrandLookupConcurrency);
        async Task ResolveAsync(IGrouping<string, PackagePartEntry> group)
        {
            var entry = group.First();
            var query = PurchasedBrandLookup.QuerySpecification(entry);
            var label = query.Length > 0 ? query : Path.GetFileNameWithoutExtension(entry.SourcePath);
            var brand = PurchasedBrandLookup.NotAvailable;
            string? name = null;
            // 没有发起调用的原因。发起了的，经过由 Apollo 在控制台上逐轮输出，这里只报结论。
            string? skipped;
            LookupResult? cached;
            lock (_brandGate)
                cached = _brandCache.GetValueOrDefault(group.Key);

            if (cached is not null)
            {
                (brand, name) = (cached.Brand, cached.Name);
                skipped = "沿用本页已查到的结果，未再调用";
            }
            else if (!PurchasedBrandLookup.IsQueryable(entry))
            {
                skipped = "文件名里没有可搜索的型号，未查询";
            }
            else if (lookup is null)
            {
                skipped = "品牌查询没有接入命令总线";
            }
            else
            {
                skipped = $"连续 {BrandFailureCircuit} 次查询失败后停查，未调用";
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool skip;
                    lock (_brandGate)
                        skip = tripped;
                    if (!skip)
                    {
                        skipped = null;
                        var answer = await lookup(entry, cancellationToken).ConfigureAwait(false);
                        // 总线把取消翻译成失败回执，不抛异常；不在这里补一刀，取消就会被记成「查询失败」。
                        cancellationToken.ThrowIfCancellationRequested();
                        brand = answer.Brand;
                        // 只给文件名里没有名称的件补名称；模型多嘴给了别的件 name，不采信。
                        name = PurchasedBrandLookup.NeedsName(entry) ? answer.Name : null;
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
                                _brandCache[group.Key] = new LookupResult(answer.Brand, name);
                            }
                        }

                        _operationProgress?.Report(
                            $"品牌 {progress}/{toQuery}：{label} → {brand}"
                            + (name is null ? string.Empty : $"，名称 {name}")
                            + (answer.Failed ? $"（查询失败：{answer.Failure}）" : string.Empty));
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            if (skipped is not null)
                _operationProgress?.Report($"品牌：{label} → {brand}｜{skipped}");

            lock (_brandGate)
            {
                foreach (var member in group)
                {
                    byId[member.Id] = brand;
                    if (name is not null)
                        namesById[member.Id] = name;
                }

                if (brand == PurchasedBrandLookup.NotAvailable)
                    notAvailable++;
                else
                    found++;
                if (name is not null)
                    namesFound++;
            }

            QueueUiUpdate(() =>
            {
                foreach (var member in group)
                {
                    if (FindRow(member.Id) is { } row)
                    {
                        row.BrandText = brand;
                        if (name is not null)
                            row.PartName = name;
                    }
                }
            });
        }

        await Task.WhenAll(groups.Select(ResolveAsync)).ConfigureAwait(false);
        return new PurchasedBrandSummary(byId, namesById, found, notAvailable, namesFound, firstFailure);
    }

    /// <summary>缓存的一种外购件：品牌，以及（文件名里没有名称时）顺带查到的名称。</summary>
    private sealed record LookupResult(string Brand, string? Name);

    /// <summary>
    /// 一轮品牌查询的结论。计数按「种」（同型号同名称算一种），不按件。
    /// <c>Skipped</c> 表示「AI 查品牌」开关关着，这一轮根本没查。
    /// </summary>
    private sealed record PurchasedBrandSummary(
        IReadOnlyDictionary<string, string> ById,
        IReadOnlyDictionary<string, string> NamesById,
        int Found,
        int NotAvailable,
        int NamesFound,
        string? FirstFailure,
        bool Skipped = false)
    {
        /// <summary>接在打包结论后面的那半句；没有外购件时为空串。</summary>
        public string Describe()
        {
            if (Skipped)
                return "；未开启 AI 查品牌，品牌列留空";
            return Found + NotAvailable == 0
                ? string.Empty
                : $"；外购件品牌查到 {Found} 种、N/A {NotAvailable} 种"
                  + (NamesFound > 0 ? $"，补全名称 {NamesFound} 种" : string.Empty)
                  + (FirstFailure is null ? string.Empty : $"（查询失败：{FirstFailure}）");
        }
    }
}
