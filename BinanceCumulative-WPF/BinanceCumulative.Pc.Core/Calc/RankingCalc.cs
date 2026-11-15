using BinanceCumulative.Pc.Models;

namespace BinanceCumulative.Pc.Calc;

public enum SortKey { Name, Net, Pct, Coins, Volume, Price }

/// <summary>
/// 표 한 줄 (선택한 기간·방식). 금액(Quote)은 <see cref="Unit"/> 단위 — 호가 자산 그대로이거나 USD·BTC 등으로 환산한 값.
/// 코인별 합산 보기에서는 한 코인의 여러 페어(BNB/USDT + BNB/BTC + BNB/ETH …)를 USD 등 한 단위로 바꿔 더한 값이고,
/// <see cref="Coin"/> 은 그중 거래대금이 가장 큰 대표 페어(그래프용), <see cref="Pairs"/> 가 합친 페어 전부.
/// </summary>
public sealed record RankRow(RankingCoin Coin, Totals Quote, Totals Base, decimal? NetPctOfSupply, decimal? VolPctOfSupply, string Unit,
    IReadOnlyList<RankingCoin> Pairs)
{
    public bool IsCoin => Pairs.Count > 1 || Id.StartsWith(CoinIdPrefix, StringComparison.Ordinal);
    public const string CoinIdPrefix = "COIN:";
    /// <summary>페어 보기: "SPOT:BNBUSDT", 코인 보기: "COIN:SPOT:BNB"</summary>
    public string Id { get; init; } = Coin.Id;
    public string Pair => IsCoin ? Coin.BaseAsset : $"{Coin.BaseAsset}/{Coin.QuoteAsset}";
    /// <summary>코인 보기에서 합친 호가 자산들 (거래대금 큰 순)</summary>
    public string PairList => string.Join(", ", Pairs.Select(p => p.QuoteAsset));
    /// <summary>코인 보기의 대표 가격(USD): USDT 페어가 있으면 그 가격, 없으면 대표 페어 가격을 환산</summary>
    public decimal? PriceUsd { get; init; }
}

/// <summary>서버 순위 데이터를 시장·페어 그룹·검색어로 거르고 정렬한다 (안드로이드·웹과 같은 규칙). 금액 단위 환산·코인별 합산 포함.</summary>
public static class RankingCalc
{
    public const string AllUsd = "ALL_USD";
    /// <summary>단위: 각 페어의 호가 자산 그대로</summary>
    public const string QuoteUnit = "QUOTE";
    /// <summary>단위: USD (호가 자산의 현재 USDT 가격으로 환산)</summary>
    public const string UsdUnit = "USD";
    /// <summary>USD 말고 고를 수 있는 기준 자산: BTC 뿐 (순위에 BTCUSDT 가 있을 때만 보임)</summary>
    public static readonly string[] UnitAssets = ["BTC"];

    /// <summary>상장 이후 전체 기간 키 (서버는 최대 5년까지만 주므로 앱이 일별 집계 DB 로 채운다)</summary>
    public const string AllTimePeriod = "ALL";

    /// <summary>
    /// 서버 순위에 "전체(상장 이후)" 기간을 덧붙인다. 값은 일별 집계 합 — 아직 집계되지 않은 페어는 이 기간에 없다.
    /// 금액은 그날 환율로 바꾼 USDT 합을 쓴다: 표는 호가 금액 × 지금 환율로 USDT 를 만들므로, 호가 금액 자리에 USDT 합 ÷ 지금 환율을 넣는다
    /// (2017년 BTC 호가 거래를 오늘 BTC 가격으로 부풀리지 않게). 환율을 모르면 호가 금액 그대로.
    /// 코인 수는 순매수만 알 수 있으므로 Base 는 (0, 순매수/2) 로 넣어 Net 만 맞춘다.
    /// </summary>
    public static Ranking WithAllTime(Ranking r, IReadOnlyDictionary<string, Dictionary<string, Storage.AllTime>> totals)
    {
        if (r.Periods.Any(p => p.Key == AllTimePeriod)) return r;
        var periods = r.Periods.Append(new RankPeriod(AllTimePeriod, "전체", 0, 0)).ToList();
        var coins = r.Coins.Select(c =>
        {
            if (!totals.TryGetValue(c.Id, out var byName)) return c;
            var byMethod = new Dictionary<SplitMethod, (Totals Quote, Totals Base)>();
            foreach (var (name, t) in byName)
            {
                if (!Enum.TryParse<SplitMethod>(name, out var m)) continue;
                var quote = c.UsdRate is { } rate && rate > 0 && t.AmountUsd is { } au && t.BuyUsd is { } bu
                    ? new Totals(au / rate, bu / rate)
                    : new Totals(t.Amount, t.Buy);
                byMethod[m] = (quote, new Totals(0m, t.NetBase / 2m));
            }
            var p = new Dictionary<string, IReadOnlyDictionary<SplitMethod, (Totals Quote, Totals Base)>>(c.Periods) { [AllTimePeriod] = byMethod };
            return c with { Periods = p };
        }).ToList();
        return r with { Periods = periods, Coins = coins };
    }

    public static IReadOnlyList<string> Markets(Ranking r) => r.Coins.Select(c => c.Market).Distinct().OrderBy(m => m).ToList();

    /// <summary>호가 자산 목록 (페어 수 많은 순)과 개수.</summary>
    public static IReadOnlyList<(string Quote, int Count)> QuoteGroups(Ranking r, string market) =>
        r.Coins.Where(c => c.Market == market).GroupBy(c => c.QuoteAsset)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key).ToList();

    /// <summary>단위 자산 1개의 USD 가격 (USD 는 1, BTC 는 BTCUSDT 현재가 — 현물 우선). 모르면 null.</summary>
    public static decimal? UnitUsdPrice(Ranking r, string unit)
    {
        if (unit is UsdUnit or "USDT") return 1m;
        return r.Coins.Where(c => c.Symbol == unit + "USDT" && c.LastPrice is > 0)
            .OrderBy(c => c.Market == "SPOT" ? 0 : 1).Select(c => c.LastPrice).FirstOrDefault();
    }

    /// <summary>고를 수 있는 기준: USD, 그리고 가격을 아는 BTC.</summary>
    public static IReadOnlyList<string> UnitOptions(Ranking r) =>
        new[] { UsdUnit }.Concat(UnitAssets.Where(a => UnitUsdPrice(r, a) is > 0)).ToList();

    /// <summary>실제 적용되는 단위 이름 (전체 그룹이나 코인별 합산에서 '그대로' 는 USD, 가격 모르는 자산은 USD 로).</summary>
    public static string EffectiveUnit(Ranking r, string group, string unit, bool byCoin = false)
    {
        if (unit == QuoteUnit) return group == AllUsd || byCoin ? UsdUnit : group;
        return UnitUsdPrice(r, unit) is > 0 ? unit : UsdUnit;
    }

    /// <param name="byCoin">true 면 같은 코인(base)의 모든 페어를 한 줄로 합산 (단위는 반드시 환산 단위)</param>
    public static List<RankRow> Rows(Ranking r, string market, string group, string period, SplitMethod method,
        SortKey sort, bool descending, string query = "", string unit = QuoteUnit, bool byCoin = false)
    {
        var eff = EffectiveUnit(r, group, unit, byCoin);
        var convert = unit != QuoteUnit || group == AllUsd || byCoin;  // 호가 자산 → eff 단위로 환산
        var unitPx = convert ? UnitUsdPrice(r, eff) ?? 1m : 1m;        // eff 1개의 USD 가격
        var q = query.Trim().ToUpperInvariant();
        var rows = new List<RankRow>();
        foreach (var c in r.Coins)
        {
            if (c.Market != market) continue;
            if (group != AllUsd && c.QuoteAsset != group) continue;
            if (q.Length > 0 && !c.Symbol.Contains(q) && !c.BaseAsset.Contains(q)) continue;
            if (!c.Periods.TryGetValue(period, out var byMethod) || !byMethod.TryGetValue(method, out var t)) continue;
            var quote = t.Quote;
            if (convert)
            {
                if (c.UsdRate is not { } rate) continue; // 환산할 수 없는 페어는 환산 보기에서 제외
                var k = rate / unitPx;
                quote = new Totals(quote.Amount * k, quote.Buy * k);
            }
            var circ = c.Supply?.Circulating;
            decimal? Pct(decimal v) => circ is > 0 ? v * c.BaseMultiplier / circ.Value * 100m : null;
            rows.Add(new RankRow(c, quote, t.Base, Pct(t.Base.Net), Pct(t.Base.Amount), convert ? eff : c.QuoteAsset, [c])
            {
                PriceUsd = c.LastPrice is { } px && c.UsdRate is { } ur ? px * ur : null,
            });
        }
        if (byCoin) rows = MergeByCoin(rows, r, market, unitPx, eff);
        return Sorted(rows, sort, descending);
    }

    /// <summary>같은 코인(base)의 페어들을 한 줄로: 금액은 같은 단위라 그대로 더하고, 코인 수는 배수(1000PEPE 등)를 풀어 더한다.</summary>
    private static List<RankRow> MergeByCoin(List<RankRow> rows, Ranking r, string market, decimal unitPx, string unit)
    {
        var merged = new List<RankRow>();
        foreach (var g in rows.GroupBy(x => x.Coin.BaseAsset))
        {
            var pairs = g.OrderByDescending(x => x.Quote.Amount).ToList();
            var rep = pairs[0].Coin;
            var quote = new Totals(pairs.Sum(x => x.Quote.Amount), pairs.Sum(x => x.Quote.Buy));
            // 코인 수: 실제 코인 단위로 (배수 풀기). 대표 페어 배수로 다시 표현하지 않고 1배 기준으로 둔다
            var baseTotals = new Totals(pairs.Sum(x => x.Base.Amount * x.Coin.BaseMultiplier), pairs.Sum(x => x.Base.Buy * x.Coin.BaseMultiplier));
            var circ = pairs.Select(x => x.Coin.Supply?.Circulating).FirstOrDefault(c => c is > 0);
            decimal? Pct(decimal v) => circ is > 0 ? v / circ.Value * 100m : null;
            // 대표 가격(USD): USDT 페어 우선
            var usdtPair = pairs.Select(x => x.Coin).FirstOrDefault(c => c.QuoteAsset == "USDT" && c.LastPrice is > 0);
            var priceUsd = usdtPair?.LastPrice ?? pairs.Select(x => x.PriceUsd).FirstOrDefault(p => p is > 0);
            var repForRow = rep.BaseMultiplier == 1m ? rep : rep with { BaseMultiplier = 1m };
            merged.Add(new RankRow(repForRow, quote, baseTotals, Pct(baseTotals.Net), Pct(baseTotals.Amount), unit, pairs.Select(x => x.Coin).ToList())
            {
                Id = $"{RankRow.CoinIdPrefix}{market}:{g.Key}",
                PriceUsd = priceUsd,
            });
        }
        return merged;
    }

    private static List<RankRow> Sorted(List<RankRow> rows, SortKey sort, bool descending)
    {
        IComparable? Key(RankRow x) => sort switch
        {
            SortKey.Name => x.Pair,
            SortKey.Net => x.Quote.Net,
            SortKey.Pct => x.NetPctOfSupply,
            SortKey.Coins => x.Base.Net,
            SortKey.Volume => x.Quote.Amount,
            SortKey.Price => x.IsCoin ? x.PriceUsd : x.Coin.LastPrice,
            _ => x.Quote.Net,
        };
        // 값이 없는 행은 정렬 방향과 관계없이 맨 뒤
        var withKey = rows.Where(x => Key(x) != null).ToList();
        withKey.Sort((a, b) => Key(a)!.CompareTo(Key(b)));
        if (descending) withKey.Reverse();
        withKey.AddRange(rows.Where(x => Key(x) == null));
        return withKey;
    }

    public static (decimal Net, decimal Volume, int Up, int Down) Totals(IReadOnlyList<RankRow> rows) => (
        rows.Sum(x => x.Quote.Net), rows.Sum(x => x.Quote.Amount),
        rows.Count(x => x.Quote.Net > 0), rows.Count(x => x.Quote.Net < 0));
}
