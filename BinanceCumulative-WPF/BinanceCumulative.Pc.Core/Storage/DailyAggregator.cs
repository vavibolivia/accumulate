using System.Collections.Concurrent;
using BinanceCumulative.Pc.Api;
using BinanceCumulative.Pc.Models;

namespace BinanceCumulative.Pc.Storage;

/// <summary>
/// 종목 × 분할 방식 × UTC 하루 한 줄.
/// 그날 값(*Quote): 순매수·매수·매도·거래대금(그 페어의 호가 자산 단위), NetBase 는 순매수 코인 수, 시·고·저·종가.
/// USDT 환산(*Usd): 그날의 환율(<see cref="UsdRate"/> = 호가 자산 1개의 USDT 가격)로 바꾼 금액. 환율을 모르면 null.
/// 누적(Cum*): 저장된 가장 예전 날부터 그날까지 더한 값.
/// <see cref="Final"/> 이 false 면 아직 진행 중인 날(오늘)이라 다음 집계에서 다시 쓴다.
/// </summary>
public sealed record DailyRow(
    string Market, string Symbol, string Method, long Day,
    double NetQuote, double BuyQuote, double SellQuote, double AmountQuote, double NetBase,
    double CumNetQuote, double CumNetBase, double CumAmountQuote,
    double Open, double High, double Low, double Close, bool Final)
{
    public double? UsdRate { get; init; }
    public double? NetUsd { get; init; }
    public double? BuyUsd { get; init; }
    public double? SellUsd { get; init; }
    public double? AmountUsd { get; init; }
    public double? CumNetUsd { get; init; }
    public double? CumAmountUsd { get; init; }
    /// <summary>코인(base 자산), 호가 자산, 코인 수 배수(1000PEPE → 1000) — 코인별 합산(daily_coin)용</summary>
    public string Base { get; init; } = "";
    public string Quote { get; init; } = "";
    public double BaseMult { get; init; } = 1;

    public const long DayMillis = 86_400_000;

    public static long DayStart(long millis) => millis - millis % DayMillis;

    public static string DayText(long day) => DateTimeOffset.FromUnixTimeMilliseconds(day).UtcDateTime.ToString("yyyy-MM-dd");
}

/// <summary>집계 진행 상황.</summary>
public sealed record AggregateProgress(int Done, int Total, string Current, long RowsWritten, bool Finished, string? Error = null);

/// <summary>
/// 서버의 /series (from=0 → 저장된 가장 예전 1분봉부터 오늘까지의 누적) 를 종목별로 받아
/// UTC 하루 단위로 나눠 로컬 DB <c>daily_agg</c> 에 쌓는다. 서버가 켜져 있는 동안 백그라운드에서 돌리는 용도.
/// 모든 페어의 금액을 **USDT 로 환산**해 함께 저장한다: 호가 자산이 USDT 면 그대로, 아니면 그날의 {호가}USDT 종가(또는 USDT{호가} 종가의 역수)를 곱한다.
/// 그래서 ① USDT 페어와 USDT 가 base 인 페어(USDTTRY 등)를 먼저, ② 나머지를 나중에 집계한다.
/// 다시 돌리면 마감된 날은 건너뛰고, 아직 안 끝난 날(오늘)과 그 뒤만 받아 이어 쓴다.
/// 서버가 그 종목의 더 예전 1분봉을 새로 받아 두었으면(first 가 앞당겨짐) 처음부터 다시 받는다.
/// </summary>
public sealed class DailyAggregator
{
    private readonly ServerApi _api;
    private readonly LocalDb _db;
    /// <summary>환율 종목의 날짜별 종가 (한 번 읽어 재사용). 키: 시장|심볼</summary>
    private readonly ConcurrentDictionary<string, Dictionary<long, double>> _closes = new();

    public DailyAggregator(ServerApi api, LocalDb db)
    {
        _api = api;
        _db = db;
    }

    public event Action<AggregateProgress>? Progress;

    /// <summary>
    /// 어제까지 마감 저장이 끝난 종목은 "오늘(진행 중인 날)" 줄만 남는다 — 그 줄은 이 간격에 한 번만 다시 받는다.
    /// (서버가 순위를 낼 때마다 전 종목 × 3방식을 다시 요청하던 것을 막는다. 새 종목·밀린 날이 있는 종목은 바로 받는다.)
    /// </summary>
    public static TimeSpan RefreshTodayEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>마지막 실행에서 받을 게 없어 건너뛴 종목·방식 수</summary>
    public int LastSkipped { get; private set; }

    /// <summary>이번에 서버에서 받아야 하는가: 처음이거나, 마감 안 된 지난 날이 있거나, 오늘 줄을 쓴 지 오래됐을 때.</summary>
    public static bool NeedsFetch((long? LastFinal, long UpdatedAt)? status, long now)
    {
        if (status is not { } s) return true;
        var yesterday = DailyRow.DayStart(now) - DailyRow.DayMillis;
        if (s.LastFinal is not { } lf || lf < yesterday) return true;
        return now - s.UpdatedAt >= RefreshTodayEvery.TotalMilliseconds;
    }

    /// <summary>환율 기준 자산. 이 자산이 호가인 페어는 환율 1.</summary>
    public const string Usd = "USDT";

    /// <summary>① 단계: 환율을 주는 페어 (호가가 USDT 이거나, USDT 가 base 인 USDTTRY 같은 페어).</summary>
    public static bool IsRateSource(RankingCoin c) => c.QuoteAsset == Usd || c.BaseAsset == Usd;

    /// <summary>
    /// 모든 종목을 집계한다. 서버에 동시에 <paramref name="parallel"/>개까지 요청.
    /// </summary>
    /// <returns>새로 쓴 줄 수</returns>
    public async Task<long> RunAsync(IReadOnlyList<RankingCoin> coins, IReadOnlyList<SplitMethod> methods, int parallel, CancellationToken ct,
        long? nowMillis = null)
    {
        // 받을 게 있는 종목·방식만 (상태는 DB 에 한 번만 묻는다)
        var now0 = nowMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var status = _db.DailyStatus();
        bool Needed(RankingCoin c, SplitMethod m) =>
            NeedsFetch(status.TryGetValue($"{c.Market}|{c.Symbol}|{m}", out var s) ? s : null, now0);
        var all = coins.SelectMany(c => methods.Select(m => (Coin: c, Method: m))).ToList();
        var todo = all.Where(j => Needed(j.Coin, j.Method)).ToList();
        LastSkipped = all.Count - todo.Count;
        var stage1 = todo.Where(j => IsRateSource(j.Coin)).ToList();
        var stage2 = todo.Where(j => !IsRateSource(j.Coin)).ToList();
        var total = stage1.Count + stage2.Count;
        var done = 0;
        long rows = 0;
        string? lastError = null;
        _closes.Clear();
        Report(new AggregateProgress(0, total, "", 0, false));
        try
        {
            await RunStageAsync(stage1, coins, parallel, ct, nowMillis).ConfigureAwait(false);
            _closes.Clear(); // ① 에서 새로 쓴 환율을 ② 가 읽도록
            await RunStageAsync(stage2, coins, parallel, ct, nowMillis).ConfigureAwait(false);
            // ③ 환율 없이 마감된 줄에 이제 환율이 있으면(환율 페어가 나중에 수집됨) 서버 요청 없이 다시 계산
            ct.ThrowIfCancellationRequested();
            LastBackfilled = todo.Count > 0 ? BackfillUsd(coins) : 0; // 새로 받은 게 없으면 환율도 그대로
            Interlocked.Add(ref rows, LastBackfilled);
        }
        finally
        {
            Report(new AggregateProgress(done, total, "", Interlocked.Read(ref rows), true, lastError));
        }
        return rows;

        async Task RunStageAsync(List<(RankingCoin Coin, SplitMethod Method)> jobs, IReadOnlyList<RankingCoin> all, int par, CancellationToken token, long? now)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, par));
            await Task.WhenAll(jobs.Select(async j =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var n = await AggregateOneAsync(j.Coin, j.Method, token, now, RateFor(j.Coin, all)).ConfigureAwait(false);
                    Interlocked.Add(ref rows, n);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // 한 종목이 실패해도 나머지는 계속; 마지막 오류만 알린다
                    lastError = $"{j.Coin.Symbol}: {e.Message}";
                }
                finally
                {
                    gate.Release();
                    var d = Interlocked.Increment(ref done);
                    Report(new AggregateProgress(d, total, j.Coin.Symbol, Interlocked.Read(ref rows), false, lastError));
                }
            })).ConfigureAwait(false);
        }
    }

    private void Report(AggregateProgress p) => Progress?.Invoke(p);

    /// <summary>마지막 실행에서 환율 보충으로 다시 쓴 줄 수</summary>
    public long LastBackfilled { get; private set; }

    /// <summary>
    /// usd_rate 가 비어 있는 페어들을 훑어, 지금은 환율을 알 수 있는 날이 하나라도 있으면 그 페어·방식의 전체 줄을
    /// DB 값(호가 자산 금액)만으로 USDT 열과 USDT 누적을 다시 계산해 덮어쓴다. 서버 요청 없음.
    /// 영영 환율이 없는 구간(예: 2017-08-17 BTCUSDT 상장 전)은 그대로 둔다.
    /// </summary>
    /// <returns>다시 쓴 줄 수</returns>
    public long BackfillUsd(IReadOnlyList<RankingCoin> all)
    {
        long n = 0;
        foreach (var (market, symbol, method, quote) in _db.PairsMissingUsd())
        {
            var rate = RateFor(market, quote, all);
            var rows = _db.GetDaily(market, symbol, method);
            if (!rows.Any(r => r.UsdRate == null && rate(r.Day) != null)) continue;
            var cumUsd = 0.0;
            var cumAmtUsd = 0.0;
            var fixedRows = new List<DailyRow>(rows.Count);
            foreach (var r in rows)
            {
                var k = rate(r.Day);
                cumUsd += k is { } k1 ? r.NetQuote * k1 : 0;
                cumAmtUsd += k is { } k2 ? r.AmountQuote * k2 : 0;
                fixedRows.Add(r with
                {
                    UsdRate = k,
                    NetUsd = k is { } a ? r.NetQuote * a : null,
                    BuyUsd = k is { } b ? r.BuyQuote * b : null,
                    SellUsd = k is { } c ? r.SellQuote * c : null,
                    AmountUsd = k is { } d ? r.AmountQuote * d : null,
                    CumNetUsd = cumUsd,
                    CumAmountUsd = cumAmtUsd,
                });
            }
            _db.UpsertDaily(fixedRows);
            n += fixedRows.Count;
        }
        return n;
    }

    /// <summary>
    /// 페어의 호가 자산 → 그날의 USDT 환율 함수. 호가가 USDT 면 1.
    /// {호가}USDT 페어가 있으면 그 종가, 없고 USDT{호가} 페어가 있으면 그 종가의 역수. 같은 시장에 없으면 현물에서 찾는다.
    /// 그날 종가가 없으면 7일 안의 가장 가까운 전날 종가. 그래도 없으면 null (환산 안 함).
    /// </summary>
    public Func<long, double?> RateFor(RankingCoin coin, IReadOnlyList<RankingCoin> all) => RateFor(coin.Market, coin.QuoteAsset, all);

    public Func<long, double?> RateFor(string market, string q, IReadOnlyList<RankingCoin> all)
    {
        if (q == Usd) return _ => 1.0;
        var direct = all.FirstOrDefault(c => c.Symbol == q + Usd && c.Market == market) ?? all.FirstOrDefault(c => c.Symbol == q + Usd);
        var inverse = direct == null ? all.FirstOrDefault(c => c.Symbol == Usd + q && c.Market == market) ?? all.FirstOrDefault(c => c.Symbol == Usd + q) : null;
        var src = direct ?? inverse;
        if (src == null) return _ => null;
        var invert = direct == null;
        Dictionary<long, double>? closes = null;
        return day =>
        {
            closes ??= _closes.GetOrAdd($"{src.Market}|{src.Symbol}", _ => _db.DailyCloses(src.Market, src.Symbol));
            for (var d = day; d >= day - 7 * DailyRow.DayMillis; d -= DailyRow.DayMillis)
                if (closes.TryGetValue(d, out var px) && px > 0) return invert ? 1.0 / px : px;
            return null;
        };
    }

    /// <summary>한 종목·방식: 어디부터 받을지 정하고 → 받아서 → 하루 단위로 나눠 → USDT 환산 → 누적 이어 쓰기.</summary>
    /// <param name="usdRate">날(UTC 시작 ms) → 호가 자산 1개의 USDT 가격. 비우면 호가가 USDT 일 때만 1, 아니면 환산 안 함</param>
    public async Task<int> AggregateOneAsync(RankingCoin coin, SplitMethod method, CancellationToken ct, long? nowMillis = null,
        Func<long, double?>? usdRate = null)
    {
        usdRate ??= coin.QuoteAsset == Usd ? _ => 1.0 : _ => null;
        var now = nowMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var m = method.ToString();
        var from = 0L;
        var lastFinal = _db.LastFinalDay(coin.Market, coin.Symbol, m);
        var first = _db.FirstDay(coin.Market, coin.Symbol, m);
        var rebuild = first is { } f && coin.FirstOpenTime is { } fo && DailyRow.DayStart(fo) < f;
        if (lastFinal is { } lf && !rebuild) from = lf + DailyRow.DayMillis;
        if (from > DailyRow.DayStart(now)) return 0; // 오늘까지 이미 마감돼 있음 (시계가 뒤로 간 경우)

        var fetched = await _api.GetSeriesAsync(coin.Market, coin.Symbol, from, method, ct).ConfigureAwait(false);
        var days = ToDays(coin, m, fetched.Value.Points, from, now);
        if (days.Count == 0) return 0;

        // 누적은 직전 날 값에서 이어 간다 (처음부터면 0)
        var cum = (from == 0 ? null : _db.CumBefore(coin.Market, coin.Symbol, m, from)) ?? new DailyCum(0, 0, 0, 0, 0);
        var rows = new List<DailyRow>(days.Count);
        foreach (var d in days)
        {
            var rate = usdRate(d.Day);
            var x = d with
            {
                CumNetQuote = cum.NetQuote + d.NetQuote,
                CumNetBase = cum.NetBase + d.NetBase,
                CumAmountQuote = cum.AmountQuote + d.AmountQuote,
                UsdRate = rate,
                NetUsd = rate is { } r1 ? d.NetQuote * r1 : null,
                BuyUsd = rate is { } r2 ? d.BuyQuote * r2 : null,
                SellUsd = rate is { } r3 ? d.SellQuote * r3 : null,
                AmountUsd = rate is { } r4 ? d.AmountQuote * r4 : null,
                CumNetUsd = cum.NetUsd + (rate is { } r5 ? d.NetQuote * r5 : 0),
                CumAmountUsd = cum.AmountUsd + (rate is { } r6 ? d.AmountQuote * r6 : 0),
            };
            cum = new DailyCum(x.CumNetQuote, x.CumNetBase, x.CumAmountQuote, x.CumNetUsd!.Value, x.CumAmountUsd!.Value);
            rows.Add(x);
        }
        _db.UpsertDaily(rows, now);
        return rows.Count;
    }

    /// <summary>
    /// 서버 누적 시계열(from 부터의 누적, 시간 또는 일 단위) → UTC 하루 단위 그날 값 (누적·환산 열은 비워 둠).
    /// 하루 안의 마지막 점이 그날 끝 누적이고, 그날 값 = 그날 끝 누적 − 전날 끝 누적. 매수 = (거래대금 + 순매수) / 2.
    /// </summary>
    public static List<DailyRow> ToDays(RankingCoin coin, string method, IReadOnlyList<SeriesPoint> points, long from, long now)
    {
        var result = new List<DailyRow>();
        var today = DailyRow.DayStart(now);
        double prevNet = 0, prevBase = 0, prevAmt = 0;
        var i = 0;
        while (i < points.Count)
        {
            var day = DailyRow.DayStart(points[i].Time);
            var j = i;
            var high = double.MinValue;
            var low = double.MaxValue;
            while (j < points.Count && DailyRow.DayStart(points[j].Time) == day)
            {
                high = Math.Max(high, points[j].High);
                low = Math.Min(low, points[j].Low);
                j++;
            }
            var last = points[j - 1];
            if (day >= from)
            {
                result.Add(new DailyRow(coin.Market, coin.Symbol, method, day,
                    NetQuote: last.CumQuoteNet - prevNet,
                    BuyQuote: (last.CumQuoteAmount - prevAmt + last.CumQuoteNet - prevNet) / 2,
                    SellQuote: (last.CumQuoteAmount - prevAmt - (last.CumQuoteNet - prevNet)) / 2,
                    AmountQuote: last.CumQuoteAmount - prevAmt,
                    NetBase: last.CumBaseNet - prevBase,
                    CumNetQuote: 0, CumNetBase: 0, CumAmountQuote: 0,
                    Open: points[i].Open, High: high, Low: low, Close: last.Close,
                    Final: day < today)
                {
                    Base = coin.BaseAsset, Quote = coin.QuoteAsset, BaseMult = (double)coin.BaseMultiplier,
                });
            }
            prevNet = last.CumQuoteNet;
            prevBase = last.CumBaseNet;
            prevAmt = last.CumQuoteAmount;
            i = j;
        }
        return result;
    }
}
