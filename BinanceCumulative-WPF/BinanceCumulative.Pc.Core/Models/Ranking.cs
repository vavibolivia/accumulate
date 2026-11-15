using System.Globalization;
using System.Text.Json;

namespace BinanceCumulative.Pc.Models;

/// <summary>분할 방식 (서버와 같은 이름).</summary>
public enum SplitMethod { PRICE_PATH, CLOSE_POSITION, TAKER }

public static class SplitMethodInfo
{
    public static string Label(SplitMethod m) => m switch
    {
        SplitMethod.PRICE_PATH => "A 경로",
        SplitMethod.CLOSE_POSITION => "B 종가",
        _ => "C 체결",
    };

    public static string Description(SplitMethod m) => m switch
    {
        SplitMethod.PRICE_PATH => "A: 양봉은 매수, 봉 안에서 내린 만큼은 매도 (음봉은 반대). 가격 움직임에서 추정한 값",
        SplitMethod.CLOSE_POSITION => "B: 종가가 고가에 가까울수록 매수. 가격 움직임에서 추정한 값",
        _ => "C: 바이낸스 실제 시장가 매수/매도 체결량 (가격과 독립된 정보)",
    };
}

/// <summary>금액(호가 자산) 또는 코인 수의 합계와 매수분.</summary>
public readonly record struct Totals(decimal Amount, decimal Buy)
{
    public decimal Sell => Amount - Buy;
    public decimal Net => Buy - Sell;
}

public sealed record Supply(string Name, string Source, decimal? Circulating, decimal? Total, decimal? Max);

public sealed record RankPeriod(string Key, string Label, int Months, long FromMillis);

/// <summary>한 페어의 기간·방식별 누적 결과.</summary>
public sealed record RankingCoin(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Market,
    decimal? LastPrice,
    long? FirstOpenTime,
    long? LastOpenTime,
    decimal BaseMultiplier,
    decimal? UsdRate,
    Supply? Supply,
    IReadOnlyDictionary<string, IReadOnlyDictionary<SplitMethod, (Totals Quote, Totals Base)>> Periods)
{
    public string Id => $"{Market}:{Symbol}";
}

/// <summary>서버의 /ranking.json.</summary>
public sealed record Ranking(long GeneratedAt, string Note, IReadOnlyList<RankPeriod> Periods, IReadOnlyList<RankingCoin> Coins)
{
    public static Ranking Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var periods = root.GetProperty("periods").EnumerateArray()
            .Select(p => new RankPeriod(p.GetProperty("key").GetString()!, p.GetProperty("label").GetString()!,
                p.GetProperty("months").GetInt32(), p.GetProperty("from").GetInt64()))
            .ToList();
        var coins = root.GetProperty("coins").EnumerateArray().Select(ParseCoin).ToList();
        return new Ranking(root.GetProperty("generatedAt").GetInt64(),
            root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "", periods, coins);
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static decimal? OptDec(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind != JsonValueKind.Null ? Dec(e) : null;

    private static long? OptLong(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;

    private static string OptStr(JsonElement o, string name, string def) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : def;

    private static RankingCoin ParseCoin(JsonElement o)
    {
        var periods = new Dictionary<string, IReadOnlyDictionary<SplitMethod, (Totals, Totals)>>();
        foreach (var p in o.GetProperty("p").EnumerateObject())
        {
            var byMethod = new Dictionary<SplitMethod, (Totals, Totals)>();
            foreach (var m in p.Value.EnumerateObject())
            {
                if (!Enum.TryParse<SplitMethod>(m.Name, out var method)) continue;
                var a = m.Value.EnumerateArray().Select(Dec).ToArray();
                byMethod[method] = (new Totals(a[0], a[1]), new Totals(a[2], a[3]));
            }
            periods[p.Name] = byMethod;
        }
        Supply? sup = null;
        if (o.TryGetProperty("sup", out var s))
            sup = new Supply(OptStr(s, "name", ""), OptStr(s, "src", "CoinGecko"), OptDec(s, "circ"), OptDec(s, "total"), OptDec(s, "max"));
        return new RankingCoin(
            o.GetProperty("s").GetString()!, o.GetProperty("b").GetString()!,
            OptStr(o, "q", "USDT"), OptStr(o, "m", "SPOT"),
            OptDec(o, "px"), OptLong(o, "first"), OptLong(o, "last"),
            OptDec(o, "mult") ?? 1m, OptDec(o, "usd"), sup, periods);
    }
}

/// <summary>그래프 한 점 (서버 /series): 누적값 + 그 구간 가격 캔들.</summary>
public readonly record struct SeriesPoint(
    long Time, double CumQuoteNet, double CumBaseNet, double CumQuoteAmount,
    double Close, double Open, double High, double Low)
{
    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(Time).UtcDateTime;
    public DateTime TimeLocal => DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime;
}

public sealed record Series(string Step, IReadOnlyList<SeriesPoint> Points)
{
    /// <summary>시간 단위면 1시간, 일 단위면 1일.</summary>
    public TimeSpan BucketSpan => Step == "HOUR" ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

    public static Series Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
        var pts = root.GetProperty("points").EnumerateArray().Select(p =>
        {
            var a = p.EnumerateArray().ToArray();
            double D(int i) => a.Length > i ? a[i].GetDouble() : a[4].GetDouble(); // 예전 서버: 시가·고가·저가 없음 → 종가
            return new SeriesPoint(a[0].GetInt64(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble(), a[4].GetDouble(), D(5), D(6), D(7));
        }).ToList();
        return new Series(root.TryGetProperty("step", out var st) ? st.GetString() ?? "DAY" : "DAY", pts);
    }
}

/// <summary>그룹 전체 누적 순매수 (서버 /group-series).</summary>
public sealed record GroupSeries(IReadOnlyList<(long Time, double Cum)> Points)
{
    public static GroupSeries Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
        return new GroupSeries(root.GetProperty("points").EnumerateArray()
            .Select(p => { var a = p.EnumerateArray().ToArray(); return (a[0].GetInt64(), a[1].GetDouble()); }).ToList());
    }
}
