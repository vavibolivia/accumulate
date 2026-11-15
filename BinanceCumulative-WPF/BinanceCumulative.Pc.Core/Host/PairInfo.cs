using System.Globalization;
using System.Text.Json;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.Storage;

namespace BinanceCumulative.Pc.Host;

/// <summary>전체 1분봉 수집 진행: 받아 둔 달 / 받아야 할 달 (상장 월~이번 달), 다 받은 페어 수 / 거래 중 페어 수.</summary>
public readonly record struct CollectProgress(long HaveMonths, long NeedMonths, int DonePairs, int TotalPairs, int KnownPairs)
{
    public double Percent => NeedMonths <= 0 ? 0 : Math.Round(100.0 * HaveMonths / NeedMonths, 1);
}

/// <summary>
/// 페어별 시작일(바이낸스 첫 1분봉)과 수집률.
/// - 대상: 바이낸스 현물에서 지금 거래 중(TRADING)인 페어 전부 (서버가 받는 대상과 같음).
/// - 시작일: 바이낸스 API 로 한 번 받아 DB(pair_listing)에 둔다.
/// - 수집률: 서버 데이터 폴더(klines/&lt;시장&gt;/&lt;심볼&gt;)에 상장 월부터 이번 달까지 월 파일이 몇 개 있는지.
/// </summary>
public sealed class PairInfo(LocalDb db, HttpMessageHandler? handler = null)
{
    private const string Spot = "SPOT";
    private readonly Dictionary<string, long> _listing = Load(db);
    private Dictionary<string, (int Have, int Need)> _collected = new();
    private List<string> _trading = new();
    private DateTime _tradingAt = DateTime.MinValue;
    private readonly HttpClient _http = handler == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : new HttpClient(handler, false);

    private static Dictionary<string, long> Load(LocalDb db)
    {
        try { return db.GetListings(); } catch (Exception) { return new(); }
    }

    // ---- 주식 토큰 (바이낸스 bStocks: AAPLB · NVDAB · SPYB …) ----

    private const string StocksMetaKey = "stocks.bases";
    public const string StockTag = "bStocks";
    private const string ProductsUrl = "https://www.binance.com/bapi/asset/v2/public/asset-service/product/get-products?includeEtf=true";

    /// <summary>바이낸스에서 태그를 못 받았을 때 쓰는 목록 (2026-11-15 기준 bStocks 태그 105개)</summary>
    public static readonly string[] DefaultStockBases =
    [
        "AAOIB", "AAPLB", "ADBEB", "AGPUB", "ALABB", "AMATB", "AMCB", "AMDB", "AMZNB", "APPB", "ARMB", "ASMLB", "ASTSB", "AVGOB", "AXTIB",
        "BABAB", "BBB", "BEB", "BMNRB", "BNCB", "BOTB", "BRKBB", "BSPB", "CBRSB", "COHRB", "COINB", "CRCLB", "CRDOB", "CRMB", "CRWDB",
        "CRWVB", "CYPHB", "DELLB", "DJTB", "DRAMB", "EWYB", "FLNCB", "FWDIB", "GEVB", "GLWB", "GMEB", "GOOGLB", "GPROB", "GSB", "HIMSB",
        "HOODB", "HPEB", "IBMB", "INTCB", "INTWB", "IRENB", "JPMB", "KORUB", "KSTRB", "LITEB", "LLYB", "MDBB", "METAB", "MRNAB", "MRVLB",
        "MSFTB", "MSTRB", "MUB", "MUUB", "MVLLB", "NBISB", "NFLXB", "NOKB", "NVDAB", "ORCLB", "PDDB", "PENGB", "PLTRB", "PYPLB", "QCOMB",
        "QNTB", "QQQB", "RDDTB", "RKLBB", "SECZB", "SHAZB", "SKDDB", "SKHYB", "SKUUB", "SMCIB", "SMHB", "SNDKB", "SNXXB", "SOXLB", "SOXSB",
        "SPCXB", "SPYB", "SQQQB", "STXB", "TMFB", "TQQQB", "TSLAB", "TSMB", "USARB", "USDEB", "UVXYB", "VSTB", "WDCB", "WENB", "ZMB",
    ];

    private HashSet<string> _stocks = LoadStocks(db);
    private DateTime _stocksAt = DateTime.MinValue;

    private static HashSet<string> LoadStocks(LocalDb db)
    {
        var set = new HashSet<string>(DefaultStockBases, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (db.GetMeta(StocksMetaKey) is { } json && JsonSerializer.Deserialize<List<string>>(json) is { } saved) set.UnionWith(saved);
        }
        catch (Exception) { /* 저장본이 없거나 깨졌으면 기본 목록만 */ }
        return set;
    }

    /// <summary>주식 토큰인가 (바이낸스 bStocks 태그가 붙은 코인). 한 번 주식으로 확인된 코인은 계속 주식으로 본다.</summary>
    public bool IsStock(string baseAsset) => _stocks.Contains(baseAsset);

    /// <summary>바이낸스 상품 목록에서 bStocks 태그가 붙은 코인 이름들. 못 받으면 null.</summary>
    public static List<string>? ParseStockBases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        return data.EnumerateArray()
            .Where(p => p.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                        && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == StockTag))
            .Select(p => p.TryGetProperty("b", out var b) ? b.GetString() : null)
            .Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList();
    }

    /// <summary>1시간마다 바이낸스에서 주식 토큰 목록을 새로 받아 더한다 (새로 상장된 주식 반영). 실패하면 가진 목록 그대로.</summary>
    private async Task RefreshStocksAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _stocksAt <= TimeSpan.FromHours(1)) return;
        try
        {
            using var res = await _http.GetAsync(ProductsUrl, ct);
            if (!res.IsSuccessStatusCode) return;
            var found = ParseStockBases(await res.Content.ReadAsStringAsync(ct));
            if (found == null) return;
            _stocksAt = DateTime.UtcNow;
            var next = new HashSet<string>(_stocks, StringComparer.OrdinalIgnoreCase);
            next.UnionWith(found);
            if (next.Count == _stocks.Count) return;
            _stocks = next;
            try { db.SetMeta(StocksMetaKey, JsonSerializer.Serialize(next.OrderBy(x => x, StringComparer.Ordinal).ToList())); } catch (Exception) { /* 다음에 다시 */ }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 부가 정보 — 실패해도 기본 목록으로 동작
        }
    }

    public long? ListedAt(RankingCoin c)
    {
        lock (_listing) return _listing.TryGetValue(c.Id, out var v) ? v : null;
    }

    public (int Have, int Need)? Collected(RankingCoin c) =>
        _collected.TryGetValue(c.Id, out var v) ? v : null;

    /// <summary>
    /// 한 번 돌기: 거래 중 페어 목록(1시간마다) → 시작일 모르는 페어만 바이낸스에서 → 데이터 폴더를 훑어 수집률.
    /// 데이터 폴더가 없으면(원격 서버) 수집률은 비운다.
    /// </summary>
    public async Task<CollectProgress?> UpdateAsync(IEnumerable<RankingCoin> ranked, string? dataFolder, CancellationToken ct)
    {
        await RefreshStocksAsync(ct);
        if (DateTime.UtcNow - _tradingAt > TimeSpan.FromHours(1) && await TradingSymbolsAsync(ct) is { Count: > 0 } t)
        {
            _trading = t;
            _tradingAt = DateTime.UtcNow;
        }
        var ids = _trading.Select(s => $"{Spot}:{s}").Concat(ranked.Select(c => c.Id)).Distinct().ToList();
        await FillListingsAsync(ids, ct);
        if (dataFolder == null) return null;
        return await Task.Run(() => Scan(ids, dataFolder, DateTime.UtcNow), ct);
    }

    private CollectProgress Scan(List<string> ids, string dataFolder, DateTime nowUtc)
    {
        var d = new Dictionary<string, (int, int)>();
        foreach (var id in ids)
        {
            long first;
            lock (_listing) if (!_listing.TryGetValue(id, out first)) continue;
            var (market, symbol) = Split(id);
            d[id] = Count(Path.Combine(dataFolder, "klines", market, symbol), first, nowUtc);
        }
        _collected = d;
        var trading = new HashSet<string>(_trading.Select(s => $"{Spot}:{s}"));
        var scope = d.Where(kv => trading.Count == 0 || trading.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        return new CollectProgress(scope.Sum(v => (long)v.Item1), scope.Sum(v => (long)v.Item2),
            scope.Count(v => v.Item1 >= v.Item2), trading.Count > 0 ? trading.Count : d.Count, scope.Count);
    }

    private static (string Market, string Symbol) Split(string id)
    {
        var i = id.IndexOf(':');
        return (id[..i], id[(i + 1)..]);
    }

    /// <summary>바이낸스 현물 거래 중 페어 (서버의 수집 대상과 같은 기준)</summary>
    private async Task<List<string>?> TradingSymbolsAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync("https://api.binance.com/api/v3/exchangeInfo?permissions=SPOT", ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            return doc.RootElement.GetProperty("symbols").EnumerateArray()
                .Where(s => s.GetProperty("status").GetString() == "TRADING")
                .Select(s => s.GetProperty("symbol").GetString()!).ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task FillListingsAsync(List<string> ids, CancellationToken ct)
    {
        List<string> missing;
        lock (_listing) missing = ids.Where(id => !_listing.ContainsKey(id)).ToList();
        // 서버도 바이낸스에서 받는 중이라 천천히 (동시 2개)
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (id, token) =>
        {
            var (market, symbol) = Split(id);
            var first = await FirstMinuteAsync(market, symbol, token);
            if (first == null) return;
            lock (_listing) _listing[id] = first.Value;
            try { db.PutListing(market, symbol, first.Value); } catch (Exception) { /* 다음에 다시 */ }
        });
    }

    private async Task<long?> FirstMinuteAsync(string market, string symbol, CancellationToken ct)
    {
        var url = market == Spot
            ? $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval=1m&startTime=0&limit=1"
            : $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval=1m&startTime=0&limit=1";
        try
        {
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var arr = doc.RootElement;
            return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 ? arr[0][0].GetInt64() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>상장 월 ~ 이번 달 중 월 파일(.days.json · .bin · .bin.gz)이 있는 달 수.</summary>
    public static (int Have, int Need) Count(string dir, long firstMs, DateTime nowUtc)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(firstMs).UtcDateTime;
        var need = (nowUtc.Year - start.Year) * 12 + nowUtc.Month - start.Month + 1;
        if (need < 1) need = 1;
        if (!Directory.Exists(dir)) return (0, need);
        var months = new HashSet<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.Length < 8 || name[4] != '-') continue;
            if (!(name.EndsWith(".days.json") || name.EndsWith(".bin") || name.EndsWith(".bin.gz"))) continue;
            if (DateTime.TryParseExact(name[..7], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
                && m >= new DateTime(start.Year, start.Month, 1) && m <= nowUtc)
                months.Add(name[..7]);
        }
        return (Math.Min(months.Count, need), need);
    }
}
