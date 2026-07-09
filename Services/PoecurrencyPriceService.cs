using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 从 poecurrency.top 获取并归一化价格数据。
/// 参考 build_poe2scout_price_patch.py 中的 normalize_poecurrency_summary 与价格选择逻辑。
/// </summary>
public class PoecurrencyPriceService : IPriceService
{
    private const string DefaultSummaryUrl = "https://poecurrency.top/api/summary?version=2";
    private const string ValidateSummaryUrlFormat = "https://poecurrency.top/api/summary_validate?token={0}&version=2";
    // db/price 接口：hours=24 取 24 小时数据，version=2 指定 POE2，currency_unit 按物品单位传入。
    private const string DbPriceUrlFormat = "https://poecurrency.top/api/db/price?item_name={0}&category_label={1}&hours=24&currency_unit={2}&version=2";
    private readonly HttpClient _httpClient;

    /// <summary>db/price 接口并发限制（5 个并发，避免触发限流）。</summary>
    private static readonly SemaphoreSlim DbPriceRateLimiter = new(5, 5);

    /// <summary>db/price 价格缓存，30 分钟过期，避免同次刷新重复请求。</summary>
    private static readonly ConcurrentDictionary<string, (decimal price, DateTime cachedAt, string currencyUnit)> DbPriceCache = new();
    private static readonly TimeSpan DbPriceCacheExpiry = TimeSpan.FromMinutes(30);

    public string DataSourceLabel => "poecurrency.top (国服)";
    public bool IsChina => true;

    /// <summary>通货价格查询 Token，为空时使用公共 summary 接口，非空时使用 summary_validate 接口。</summary>
    public string? Token { get; set; }

    public PoecurrencyPriceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// IPriceService 接口实现：使用 Token 属性拉取价格。
    /// </summary>
    Task<ObservableCollection<PoecurrencyItem>> IPriceService.FetchPricesAsync(CancellationToken cancellationToken)
    {
        return FetchPricesAsync(token: Token, cancellationToken: cancellationToken);
    }

    public async Task<ObservableCollection<PoecurrencyItem>> FetchPricesAsync(
        string? summaryUrl = null,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        string url;
        if (!string.IsNullOrWhiteSpace(token))
        {
            url = string.Format(ValidateSummaryUrlFormat, Uri.EscapeDataString(token.Trim()));
        }
        else
        {
            url = string.IsNullOrWhiteSpace(summaryUrl) ? DefaultSummaryUrl : summaryUrl;
        }
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var summary = NormalizeSummary(root);
        // 仅当使用无 token 的公共 summary 接口时，才需要对 HasError 物品走 db/price 修复。
        // summary_validate 接口（有 token）已过滤异常数据，无需二次修复。
        var useValidate = !string.IsNullOrWhiteSpace(token);
        return await BuildItemsAsync(summary, useValidate, cancellationToken);
    }

    /// <summary>
    /// 将接口响应归一化为统一的分类/物品结构。
    /// </summary>
    private static List<PoecurrencyCategory> NormalizeSummary(JsonElement root)
    {
        var categories = new List<PoecurrencyCategory>();

        JsonElement categoryList;
        if (root.ValueKind == JsonValueKind.Array)
        {
            categoryList = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            categoryList = FirstPresent(root, "value", "data", "items", "list", "result");
        }
        else
        {
            throw new InvalidOperationException("poecurrency.top 返回的数据格式不正确。");
        }

        if (categoryList.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("poecurrency.top 返回的分类列表格式不正确。");
        }

        foreach (var categoryElement in categoryList.EnumerateArray())
        {
            if (categoryElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = GetString(categoryElement, "category_label", "category", "label", "name");
            var itemsElement = FirstPresent(categoryElement, "items", "data", "list", "children");
            if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = new List<PoecurrencyRawItem>();
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                if (itemElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(itemElement, "item_name", "name", "itemName", "item");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                items.Add(new PoecurrencyRawItem
                {
                    Name = name,
                    LatestBuy1 = GetDecimal(itemElement, "latest_buy1", "latest_buy", "buy1", "buy_price"),
                    LatestSell1 = GetDecimal(itemElement, "latest_sell1", "latest_sell", "sell1", "sell_price"),
                    BuyAverage = GetDecimal(itemElement, "buy_avg", "avg_buy", "buyAverage", "buy"),
                    SellAverage = GetDecimal(itemElement, "sell_avg", "avg_sell", "sellAverage", "sell"),
                    BuyAverageYesterday = GetDecimal(itemElement, "buy_avg_yesterday", "avg_buy_yesterday"),
                    SellAverageYesterday = GetDecimal(itemElement, "sell_avg_yesterday", "avg_sell_yesterday"),
                    PreviousBuy1 = GetDecimal(itemElement, "prev_buy1", "previous_buy1", "prev_buy"),
                    CurrencyUnit = GetString(itemElement, "currency_unit", "unit"),
                    ExplicitExalted = GetDecimal(itemElement, "e", "price_e", "exalted", "exalted_price"),
                    HasError = GetBool(itemElement, "error"),
                    ErrorInfo = GetString(itemElement, "error_info"),
                });
            }

            categories.Add(new PoecurrencyCategory
            {
                Label = label,
                Items = items,
            });
        }

        return categories;
    }

    /// <summary>
    /// 对异常物品调用 api/db/price 获取 24 小时明细，过滤离群点后取 sell1 中位数作为修复价。
    /// 仅对 HasError 的物品触发。返回 (价格, 来源, 货币单位)。参考 agent/价格修复方案.md。
    /// currencyUnit 按物品原始单位传入（d/c/e），用于接口查询指定计价货币。
    /// </summary>
    private async Task<(decimal price, string source, string currencyUnit)?> RepairPriceFromDbPriceAsync(
        string itemName, string categoryLabel, string currencyUnit, CancellationToken ct)
    {
        var cacheKey = $"{categoryLabel}/{itemName}/{currencyUnit}";
        if (DbPriceCache.TryGetValue(cacheKey, out var cached)
            && DateTime.Now - cached.cachedAt < DbPriceCacheExpiry)
        {
            return (cached.price, "db_price_median_24h_cached", cached.currencyUnit);
        }

        await DbPriceRateLimiter.WaitAsync(ct);
        try
        {
            // 接口 currency_unit 只接受 d/c，e 单位的物品按 e 查询（接口可能默认返回 d）。
            var queryUnit = string.IsNullOrEmpty(currencyUnit) ? "d" : currencyUnit;
            var url = string.Format(DbPriceUrlFormat,
                Uri.EscapeDataString(itemName),
                Uri.EscapeDataString(categoryLabel),
                Uri.EscapeDataString(queryUnit));

            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLogger.Instance.Warn($"db/price 请求失败：{(int)resp.StatusCode} item={itemName}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var (price, filteredCount, validatedCount, median, dbCurrencyUnit) =
                SelectSellPriceFromDbRecords(doc.RootElement);

            if (price > 0)
            {
                // 优先用 db/price 返回的货币单位（更准确），回退到入参的单位。
                var finalUnit = !string.IsNullOrEmpty(dbCurrencyUnit) ? dbCurrencyUnit : currencyUnit;
                DbPriceCache[cacheKey] = (price, DateTime.Now, finalUnit);
                AppLogger.Instance.Info(
                    $"价格修复：item={itemName}, category={categoryLabel}, " +
                    $"修复价={price}, source=db_price_median_24h, unit={finalUnit}, " +
                    $"过滤后={filteredCount}条, validated={validatedCount}条, 中位数={median}");
                return (price, "db_price_median_24h", finalUnit);
            }

            AppLogger.Instance.Warn($"db/price 过滤后无有效数据：item={itemName}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"db/price 修复异常：item={itemName}, {ex.Message}");
            return null;
        }
        finally
        {
            DbPriceRateLimiter.Release();
        }
    }

    /// <summary>
    /// 解析 db/price 返回的 24 小时明细，3 层过滤后按优先级选取 sell1 中位数。
    /// 返回 (最终价格, 过滤后记录数, validated 记录数, 中位数, 货币单位)。
    /// 货币单位取第一条有效记录的 currency_unit（同一物品 24 小时内单位应一致）。
    /// </summary>
    private static (decimal price, int filteredCount, int validatedCount, decimal median, string currencyUnit)
        SelectSellPriceFromDbRecords(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, 0, 0, "");
        }

        // 解析所有记录
        var records = new List<(decimal sell1, decimal vol, bool validated, DateTime time)>();
        string currencyUnit = "";
        foreach (var el in root.EnumerateArray())
        {
            var sell1 = GetDecimal(el, "sell1");
            var vol = GetDecimal(el, "sell1_vol");
            var validated = GetBool(el, "validated");
            var dtStr = GetString(el, "datetime");
            var dt = DateTime.TryParse(dtStr, out var t) ? t : DateTime.MinValue;

            // 第 1 层：基础有效性过滤
            if (sell1 > 0 && vol > 0)
            {
                records.Add((sell1, vol, validated, dt));
                // 取第一条有效记录的 currency_unit（同物品单位应一致）
                if (string.IsNullOrEmpty(currencyUnit))
                {
                    currencyUnit = GetString(el, "currency_unit")?.Trim().ToLowerInvariant() ?? "";
                }
            }
        }

        if (records.Count == 0)
        {
            return (0, 0, 0, 0, "");
        }

        // 第 2 层：基于中位数过滤离群点（偏离中位数 >3x 的丢弃）
        var median1 = Median(records.Select(r => r.sell1).OrderBy(x => x).ToList());
        records = records
            .Where(r => r.sell1 >= median1 / 3m && r.sell1 <= median1 * 3m)
            .ToList();

        if (records.Count == 0)
        {
            return (0, 0, 0, 0, currencyUnit);
        }

        // 重新计算过滤后的中位数 M'（更稳健的基准）
        var median2 = Median(records.Select(r => r.sell1).OrderBy(x => x).ToList());

        // 第 3 层：可信度过滤
        var validatedRecords = records.Where(r => r.validated).ToList();
        var filteredRecords = new List<(decimal sell1, decimal vol, bool validated, DateTime time)>();

        foreach (var r in records)
        {
            if (r.validated)
            {
                // 高可信：保留
                filteredRecords.Add(r);
            }
            else if (r.sell1 >= median2 * 0.7m && r.sell1 <= median2 * 1.3m)
            {
                // 中可信：validated=0 但在中位数 ±30% 内，保留
                filteredRecords.Add(r);
            }
            else if (r.vol < 3 && (r.sell1 < median2 * 0.5m || r.sell1 > median2 * 1.5m))
            {
                // 离群少量挂单：丢弃（典型污染源）
                continue;
            }
            else
            {
                // validated=0 且偏离中位数 > 30%：丢弃
                continue;
            }
        }

        if (filteredRecords.Count == 0)
        {
            return (0, 0, 0, 0, currencyUnit);
        }

        // 按优先级选取 sell1 作为最终价格
        List<decimal> candidates;
        string source;

        // 优先级 1：最近 6 小时 validated=1 记录
        var recentValidated = filteredRecords
            .Where(r => r.validated && r.time > DateTime.Now.AddHours(-6))
            .ToList();
        if (recentValidated.Count >= 3)
        {
            candidates = recentValidated.Select(r => r.sell1).ToList();
            source = "db_price_recent6h_validated_median";
        }
        // 优先级 2：全部 validated=1 记录
        else if (validatedRecords.Count >= 3)
        {
            candidates = validatedRecords.Select(r => r.sell1).ToList();
            source = "db_price_validated_median";
        }
        // 优先级 3：过滤后全部记录
        else if (filteredRecords.Count >= 3)
        {
            candidates = filteredRecords.Select(r => r.sell1).ToList();
            source = "db_price_filtered_median";
        }
        // 优先级 4：过滤后记录几何均值（< 3 条）
        else
        {
            candidates = filteredRecords.Select(r => r.sell1).ToList();
            source = "db_price_filtered_geomean";
        }

        var finalPrice = candidates.Count >= 3
            ? Median(candidates.OrderBy(x => x).ToList())
            : GeoMean(candidates);

        return (finalPrice, filteredRecords.Count, validatedRecords.Count, median2, currencyUnit);
    }

    /// <summary>计算排序后列表的中位数。</summary>
    private static decimal Median(List<decimal> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2m
            : sorted[mid];
    }

    /// <summary>计算多个价格值的几何均值（用于过滤后记录数 < 3 时的回退）。</summary>
    private static decimal GeoMean(List<decimal> values)
    {
        if (values.Count == 0) return 0;
        if (values.Count == 1) return values[0];
        if (values.Count == 2) return GeoMean(values[0], values[1]);

        double logSum = 0;
        int count = 0;
        foreach (var v in values)
        {
            if (v > 0)
            {
                logSum += Math.Log((double)v);
                count++;
            }
        }
        return count > 0 ? (decimal)Math.Exp(logSum / count) : 0;
    }

    private async Task<ObservableCollection<PoecurrencyItem>> BuildItemsAsync(
        List<PoecurrencyCategory> categories,
        bool useValidate,
        CancellationToken cancellationToken)
    {
        var result = new ObservableCollection<PoecurrencyItem>();

        // 先推导神圣石/崇高石换算比例。
        // 国服 poecurrency.top 中 Divine Orb 以 E 标价时，其价格即为 D/E 比例。
        decimal divineExaltedRatio = 0;
        // 同时推导混沌石/崇高石换算比例（C→E），用于以 c 计价的物品换算。
        decimal chaosExaltedRatio = 0;
        foreach (var category in categories)
        {
            foreach (var raw in category.Items)
            {
                if (IsDivine(raw.Name) && GetUnit(raw) == "e")
                {
                    var price = ChooseSimplePrice(raw);
                    if (price > divineExaltedRatio)
                    {
                        divineExaltedRatio = price;
                    }
                }
                else if (IsChaos(raw.Name))
                {
                    var chaosUnit = GetUnit(raw);
                    var chaosPrice = ChooseSimplePrice(raw);
                    if (chaosPrice > 0)
                    {
                        if (chaosUnit == "e")
                        {
                            // 混沌石以崇高石计价：1 c = chaosPrice e
                            if (chaosPrice > chaosExaltedRatio)
                            {
                                chaosExaltedRatio = chaosPrice;
                            }
                        }
                        else if (chaosUnit == "d" && divineExaltedRatio > 0)
                        {
                            // 混沌石以神圣石计价：1 c = chaosPrice d = chaosPrice * divineExaltedRatio e
                            var chaosExalted = chaosPrice * divineExaltedRatio;
                            if (chaosExalted > chaosExaltedRatio)
                            {
                                chaosExaltedRatio = chaosExalted;
                            }
                        }
                    }
                }
            }
        }

        AppLogger.Instance.Info(
            $"货币换算比例：D→E={divineExaltedRatio}, C→E={chaosExaltedRatio}");

        foreach (var category in categories)
        {
            foreach (var raw in category.Items)
            {
                var unit = GetUnit(raw);
                var (price, source) = ComputePrice(raw, unit, divineExaltedRatio);

                // 触发修复：仅当使用无 token 的公共 summary 接口（useValidate=false）
                // 且物品 HasError 时，才调用 api/db/price 获取 24 小时明细重新计算。
                // summary_validate 接口（有 token）已过滤异常数据，无需二次修复。
                if (!useValidate && raw.HasError)
                {
                    var originalPrice = price;
                    var repaired = await RepairPriceFromDbPriceAsync(
                        raw.Name, category.Label, unit, cancellationToken);
                    if (repaired.HasValue && repaired.Value.price > 0)
                    {
                        price = repaired.Value.price;
                        source = repaired.Value.source;
                        // 用 db/price 返回的货币单位覆盖，确保 price 和 unit 一致。
                        if (!string.IsNullOrEmpty(repaired.Value.currencyUnit))
                        {
                            unit = repaired.Value.currencyUnit;
                        }
                        AppLogger.Instance.Info(
                            $"价格修复对比：item={raw.Name}, 修复前={originalPrice}, 修复后={price}, " +
                            $"unit={unit}, source={source}");
                    }
                }

                if (price <= 0)
                {
                    continue;
                }

                decimal priceExalted;
                string unitNote;
                if (unit == "d")
                {
                    priceExalted = divineExaltedRatio > 0 ? price * divineExaltedRatio : 0;
                    unitNote = $"d_to_e@{divineExaltedRatio}";
                }
                else if (unit == "c")
                {
                    // 混沌石计价：需通过 chaosExaltedRatio 换算为崇高石。
                    priceExalted = chaosExaltedRatio > 0 ? price * chaosExaltedRatio : 0;
                    unitNote = $"c_to_e@{chaosExaltedRatio}";
                }
                else
                {
                    priceExalted = price;
                    unitNote = "e";
                }

                if (IsDivine(raw.Name) && unit == "e" && priceExalted <= 0)
                {
                    priceExalted = price;
                    unitNote = "e";
                }

                if (priceExalted <= 0)
                {
                    continue;
                }

                result.Add(new PoecurrencyItem
                {
                    CategoryLabel = category.Label,
                    ItemName = raw.Name,
                    LatestBuy1 = raw.LatestBuy1,
                    LatestSell1 = raw.LatestSell1,
                    BuyAverage = raw.BuyAverage,
                    SellAverage = raw.SellAverage,
                    PreviousBuy1 = raw.PreviousBuy1,
                    CurrencyUnit = unit,
                    HasError = raw.HasError,
                    ErrorInfo = raw.ErrorInfo,
                    PriceExalted = priceExalted,
                    DivineExaltedRatio = divineExaltedRatio,
                    SourcePair = $"poecurrency.top/{category.Label}/{source}/{unitNote}",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 简化版价格计算：优先复刻 Python 脚本的主要分支。
    /// </summary>
    private static (decimal price, string source) ComputePrice(
        PoecurrencyRawItem raw,
        string unit,
        decimal divineExaltedRatio)
    {
        // 1. 显式的 exalted 价格字段优先级最高。
        if (raw.ExplicitExalted > 0)
        {
            return (raw.ExplicitExalted, "explicit_exalted");
        }

        // 2. 神圣石单独处理。
        if (IsDivine(raw.Name))
        {
            return ComputeDivinePrice(raw);
        }

        // 3. 普通物品：均价作为参考，结合 latest_buy1 / latest_sell1 选择。
        // 先检测 buy/sell 严重失衡（>100 倍）：buy 侧极可能被 OCR 放大污染。
        // 此时几何均值会被拉偏，改用 sell 侧中位数作为最终价（不论 error 标志）。
        var avgSpread = (raw.BuyAverage > 0 && raw.SellAverage > 0)
            ? SpreadRatio(raw.BuyAverage, raw.SellAverage) : 0;
        var latestSpread = (raw.LatestBuy1 > 0 && raw.LatestSell1 > 0)
            ? SpreadRatio(raw.LatestBuy1, raw.LatestSell1) : 0;

        if (avgSpread > 100 || latestSpread > 100)
        {
            var sellCandidates = new[] { raw.LatestSell1, raw.SellAverage, raw.SellAverageYesterday }
                .Where(v => v > 0).OrderBy(v => v).ToList();
            if (sellCandidates.Count > 0)
            {
                var median = sellCandidates[sellCandidates.Count / 2];
                return (median, "sell_median_ocr_polluted_buy");
            }
        }

        var avg = GeometricMean(raw.BuyAverage, raw.SellAverage);

        if (unit == "d")
        {
            var shifted = TryDigitShiftedDivinePrice(raw);
            if (shifted.price > 0)
            {
                return shifted;
            }

            var latest = ChoosePairPrice(raw.LatestBuy1, raw.LatestSell1, "latest_buy1", "latest_sell1");
            if (latest.price > 0 && !latest.source.EndsWith("spread_gt_5x"))
            {
                return latest;
            }
        }

        if (raw.HasError)
        {
            // latest 的 spread 还算正常（≤10 倍），用 latest。
            if (latestSpread > 0 && latestSpread <= 10)
            {
                var latest = ChoosePairPrice(raw.LatestBuy1, raw.LatestSell1, "latest_buy1", "latest_sell1");
                if (latest.price > 0)
                    return (latest.price, $"{latest.source}_error_fallback");
            }

            // 均价还能用。
            if (avg > 0)
            {
                return (avg, $"{GeometricMeanSource(raw.BuyAverage, raw.SellAverage)}_error_fallback");
            }

            // 今日数据全废，回退到昨日 sell 均价（比 buy 稳定）。
            if (raw.SellAverageYesterday > 0)
                return (raw.SellAverageYesterday, "sell_avg_yesterday_error_fallback");
            if (raw.BuyAverageYesterday > 0)
                return (raw.BuyAverageYesterday, "buy_avg_yesterday_error_fallback");
            if (raw.PreviousBuy1 > 0)
                return (raw.PreviousBuy1, "prev_buy1_error_fallback");
        }

        var latestWithRef = ChoosePairPriceWithReference(
            raw.LatestBuy1,
            raw.LatestSell1,
            "latest_buy1",
            "latest_sell1",
            avg,
            GeometricMeanSource(raw.BuyAverage, raw.SellAverage));
        if (latestWithRef.price > 0)
        {
            return latestWithRef;
        }

        if (avg > 0)
        {
            return (avg, GeometricMeanSource(raw.BuyAverage, raw.SellAverage));
        }

        return (0, "");
    }

    private static (decimal price, string source) ComputeDivinePrice(PoecurrencyRawItem raw)
    {
        var stable = raw.BuyAverage > 0 ? raw.BuyAverage : raw.SellAverage;
        var stableField = raw.BuyAverage > 0 ? "buy_avg" : "sell_avg";

        if (raw.HasError)
        {
            if (stable > 0)
            {
                return (stable, $"{stableField}_divine_error_fallback");
            }

            // 今日均价为 0 时，回退到昨日均价（比 prev_buy1 更稳定）。
            var stableYesterday = raw.BuyAverageYesterday > 0 ? raw.BuyAverageYesterday : raw.SellAverageYesterday;
            var stableYesterdayField = raw.BuyAverageYesterday > 0 ? "buy_avg_yesterday" : "sell_avg_yesterday";
            if (stableYesterday > 0)
            {
                return (stableYesterday, $"{stableYesterdayField}_divine_error_fallback");
            }

            if (raw.PreviousBuy1 > 0)
            {
                return (raw.PreviousBuy1, "prev_buy1_divine_error_fallback");
            }
        }

        // 买卖价差异常（>5x）：选择最接近均价的那个，避免极端挂单污染神圣石比例。
        if (raw.LatestBuy1 > 0 && raw.LatestSell1 > 0
            && SpreadRatio(raw.LatestBuy1, raw.LatestSell1) > 5)
        {
            var closest = ClosestToReference(stable,
                (raw.LatestBuy1, "latest_buy1"),
                (raw.LatestSell1, "latest_sell1"));
            if (closest.price > 0)
            {
                return (closest.price, $"{closest.source}_divine_spread_fallback");
            }
        }

        // 最新买价相对均价异常（>5x）：回退到均价，防止异常挂单影响 D/E 换算。
        if (raw.LatestBuy1 > 0 && stable > 0
            && SpreadRatio(raw.LatestBuy1, stable) > 5)
        {
            return (stable, $"{stableField}_divine_latest_outlier_fallback");
        }

        if (raw.LatestBuy1 > 0)
        {
            return (raw.LatestBuy1, "latest_buy1_divine_ratio");
        }

        if (raw.LatestSell1 > 0)
        {
            return (raw.LatestSell1, "latest_sell1_divine_ratio");
        }

        if (stable > 0)
        {
            return (stable, $"{stableField}_divine_ratio");
        }

        return (0, "");
    }

    private static decimal ChooseSimplePrice(PoecurrencyRawItem raw)
    {
        var result = ChoosePairPrice(raw.LatestBuy1, raw.LatestSell1, "latest_buy1", "latest_sell1");
        if (result.price > 0)
        {
            return result.price;
        }

        return GeometricMean(raw.BuyAverage, raw.SellAverage);
    }

    private static (decimal price, string source) ChoosePairPrice(
        decimal buy,
        decimal sell,
        string buyField,
        string sellField)
    {
        if (buy > 0 && sell > 0)
        {
            var ratio = SpreadRatio(buy, sell);
            if (ratio <= 5)
            {
                return (GeoMean(buy, sell), $"geo_{buyField}_{sellField}");
            }

            return buy <= sell
                ? (buy, $"{buyField}_conservative_spread_gt_5x")
                : (sell, $"{sellField}_conservative_spread_gt_5x");
        }

        if (sell > 0)
        {
            return (sell, $"{sellField}_only");
        }

        if (buy > 0)
        {
            return (buy, $"{buyField}_only");
        }

        return (0, "");
    }

    private static (decimal price, string source) ChoosePairPriceWithReference(
        decimal buy,
        decimal sell,
        string buyField,
        string sellField,
        decimal reference,
        string referenceField)
    {
        if (buy > 0 && sell > 0)
        {
            var ratio = SpreadRatio(buy, sell);
            if (ratio > 5 && reference > 0)
            {
                var closest = ClosestToReference(reference, (buy, buyField), (sell, sellField));
                if (closest.price > 0)
                {
                    return (closest.price, $"{closest.source}_closest_to_{referenceField}_spread_gt_5x");
                }
            }
        }

        return ChoosePairPrice(buy, sell, buyField, sellField);
    }

    private static (decimal price, string source) TryDigitShiftedDivinePrice(PoecurrencyRawItem raw)
    {
        if (raw.LatestBuy1 <= 0 || raw.LatestSell1 <= 0)
        {
            return (0, "");
        }

        var high = Math.Max(raw.LatestBuy1, raw.LatestSell1);
        var low = Math.Min(raw.LatestBuy1, raw.LatestSell1);

        if (low == Math.Truncate(low))
        {
            return (0, "");
        }

        var ratio = SpreadRatio(high, low);
        if (ratio < 20 || ratio > 200)
        {
            return (0, "");
        }

        if (high < 50 || high > 1000)
        {
            return (0, "");
        }

        var scaledHigh = high / 100;
        if (SpreadRatio(low, scaledHigh) > 5)
        {
            return (0, "");
        }

        var highField = raw.LatestBuy1 >= raw.LatestSell1 ? "latest_buy1" : "latest_sell1";
        var lowField = raw.LatestBuy1 >= raw.LatestSell1 ? "latest_sell1" : "latest_buy1";
        return (GeoMean(low, scaledHigh), $"geo_{lowField}_{highField}_d_digit_shift_100x");
    }

    private static (decimal price, string source) ClosestToReference(
        decimal reference,
        params (decimal price, string source)[] candidates)
    {
        var positive = candidates.Where(c => c.price > 0).ToList();
        if (positive.Count == 0)
        {
            return (0, "");
        }

        if (reference <= 0)
        {
            return positive.OrderByDescending(c => c.price).First();
        }

        return positive
            .OrderBy(c => SpreadRatio(c.price, reference))
            .First();
    }

    private static string GetUnit(PoecurrencyRawItem raw)
    {
        var rawUnit = raw.CurrencyUnit?.Trim().ToLowerInvariant() ?? "";
        if (rawUnit is "d" or "divine" or "divine orb" or "divine_orb" or "神圣石" or "神圣宝珠")
        {
            return "d";
        }

        if (rawUnit is "e" or "exalted" or "exalted orb" or "exalted_orb" or "崇高石" or "崇高宝珠")
        {
            return "e";
        }

        if (rawUnit is "c" or "chaos" or "chaos orb" or "chaos_orb" or "混沌石" or "混沌宝珠")
        {
            return "c";
        }

        return "e";
    }

    private static bool IsDivine(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace(" ", "")
            .Replace("\u3000", "");
        return normalized is "神圣石" or "神圣宝珠" or "divineorb" or "divine";
    }

    /// <summary>判断是否为混沌石（用于推导 c→e 换算比例）。</summary>
    private static bool IsChaos(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace(" ", "")
            .Replace("\u3000", "");
        return normalized is "混沌石" or "混沌宝珠" or "chaosorb" or "chaos";
    }

    private static decimal GeometricMean(decimal a, decimal b)
    {
        if (a > 0 && b > 0)
        {
            return (decimal)Math.Sqrt((double)(a * b));
        }

        if (a > 0)
        {
            return a;
        }

        if (b > 0)
        {
            return b;
        }

        return 0;
    }

    private static string GeometricMeanSource(decimal buy, decimal sell)
    {
        if (buy > 0 && sell > 0)
        {
            return "geo_buy_avg_sell_avg";
        }

        if (buy > 0)
        {
            return "buy_avg_only";
        }

        if (sell > 0)
        {
            return "sell_avg_only";
        }

        return "avg_unavailable";
    }

    private static decimal SpreadRatio(decimal left, decimal right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }

        return Math.Max(left, right) / Math.Min(left, right);
    }

    private static decimal GeoMean(decimal a, decimal b)
    {
        return (decimal)Math.Sqrt((double)(a * b));
    }

    #region Json Helpers

    private static JsonElement FirstPresent(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                return value;
            }
        }

        return default;
    }

    private static string GetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                var text = value.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static decimal GetDecimal(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
                {
                    return decimalValue;
                }

                if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }

    private static bool GetBool(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            var text = value.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (text is "1" or "true" or "yes")
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    private sealed class PoecurrencyCategory
    {
        public string Label { get; set; } = "";
        public List<PoecurrencyRawItem> Items { get; set; } = [];
    }

    private sealed class PoecurrencyRawItem
    {
        public string Name { get; set; } = "";
        public decimal LatestBuy1 { get; set; }
        public decimal LatestSell1 { get; set; }
        public decimal BuyAverage { get; set; }
        public decimal SellAverage { get; set; }
        public decimal BuyAverageYesterday { get; set; }
        public decimal SellAverageYesterday { get; set; }
        public decimal PreviousBuy1 { get; set; }
        public string CurrencyUnit { get; set; } = "";
        public decimal ExplicitExalted { get; set; }
        public bool HasError { get; set; }
        public string ErrorInfo { get; set; } = "";
    }
}
