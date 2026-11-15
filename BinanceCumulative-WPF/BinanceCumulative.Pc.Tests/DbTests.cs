using System.Net;
using BinanceCumulative.Pc.Api;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.Storage;
using BinanceCumulative.Pc.ViewModels;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

/// <summary>로컬 DB(SQLite): 설정, 그래프 저장본, 종목별 일별 집계.</summary>
public class DbTests
{
    private static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), $"bc-test-{Guid.NewGuid():N}{ext}");

    private static void Delete(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(f); } catch (IOException) { /* 아직 열려 있음 */ }
    }

    [Fact]
    public void SettingsRoundTripAndLegacyJsonMigration()
    {
        var json = TempFile(".json");
        new AppSettings { ServerUrl = "http://10.0.0.5:9000", Months = 24, Market = "FUTURES" }.SaveJson(json);
        using var db = new LocalDb(LocalDb.InMemory);

        // DB 가 비어 있으면 예전 JSON 에서 가져와 DB 에 저장
        var s = AppSettings.Load(db, json);
        Assert.Equal("http://10.0.0.5:9000", s.ServerUrl);
        Assert.Equal(24, s.Months);
        Assert.Equal("FUTURES", s.Market);
        Assert.True(db.GetSettings().Count > 0);
        Assert.DoesNotContain("IsLocalServer", db.GetSettings().Keys); // 계산 속성은 저장하지 않음

        // 바꿔서 저장 → 다시 읽기 (JSON 은 더 이상 보지 않음)
        s.Months = 120;
        s.AutoDailyAggregate = false;
        s.Save(db);
        File.Delete(json);
        var again = AppSettings.Load(db, json);
        Assert.Equal(120, again.Months);
        Assert.False(again.AutoDailyAggregate);
        Assert.Equal("http://10.0.0.5:9000", again.ServerUrl);

        // 기본값: 전체 기간(120개월) 수집, 자동 집계 켬
        var def = new AppSettings();
        Assert.Equal(AppSettings.MaxMonths, def.Months);
        Assert.True(def.AutoDailyAggregate);
    }

    [Fact]
    public void CachesRankingSeriesAndGroup()
    {
        using var db = new LocalDb(LocalDb.InMemory);
        Assert.Null(db.GetRanking());
        db.PutRanking(CoreTests.Fixture("ranking.json"), now: 1000);
        var r = db.GetRanking();
        Assert.NotNull(r);
        Assert.Equal(1000, r.FetchedAt);
        Assert.Equal(16, Ranking.Parse(r.Json).Coins.Count);

        db.PutSeries("SPOT", "BTCUSDT", "1M", "TAKER", 123, CoreTests.Fixture("series-hour.json"), now: 5000);
        db.PutSeries("SPOT", "BTCUSDT", "1M", "TAKER", 456, CoreTests.Fixture("series-day.json"), now: 6000); // 같은 키 → 덮어씀
        db.PutGroup("SPOT", "USDT", "1M", "TAKER", 123, CoreTests.Fixture("group.json"), now: 7000);
        var s = db.GetSeries("SPOT", "BTCUSDT", "1M", "TAKER");
        Assert.NotNull(s);
        Assert.Equal(456, s.FromMillis);
        Assert.Equal("DAY", Series.Parse(s.Json).Step);
        Assert.Null(db.GetSeries("SPOT", "BTCUSDT", "3M", "TAKER"));
        Assert.NotNull(db.GetGroup("SPOT", "USDT", "1M", "TAKER"));
        Assert.Equal(2, db.SeriesCount);

        Assert.Equal(1, db.Prune(olderThanMillis: 6500)); // 6000 짜리만 지움
        Assert.Null(db.GetSeries("SPOT", "BTCUSDT", "1M", "TAKER"));
        Assert.NotNull(db.GetGroup("SPOT", "USDT", "1M", "TAKER"));

        db.SetMeta("k", "v");
        db.SetMeta("k", "v2");
        Assert.Equal("v2", db.GetMeta("k"));
        Assert.Null(db.GetMeta("none"));

        db.ClearCache();
        Assert.Null(db.GetRanking());
        Assert.Equal(0, db.SeriesCount);
        Assert.Equal("v2", db.GetMeta("k"));
    }

    private static RankingCoin Coin(string symbol = "ETHUSDT", long? first = null, string quote = "USDT") =>
        new(symbol, symbol[..^quote.Length], quote, "SPOT", null, first, null, 1m, 1m, null,
            new Dictionary<string, IReadOnlyDictionary<SplitMethod, (Totals, Totals)>>());

    [Fact]
    public void SplitsCumulativeSeriesIntoUtcDays()
    {
        var day = Series.Parse(CoreTests.Fixture("series-day.json"));
        var last = day.Points[^1];
        var rows = DailyAggregator.ToDays(Coin(), "TAKER", day.Points, from: 0, now: last.Time + DailyRow.DayMillis);
        Assert.Equal(day.Points.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Day % DailyRow.DayMillis));
        Assert.All(rows, r => Assert.True(r.Final));
        // 첫날 = 첫 누적값, 그날 값들의 합 = 마지막 누적값, 매수 + 매도 = 거래대금, 매수 − 매도 = 순매수
        Assert.Equal(day.Points[0].CumQuoteNet, rows[0].NetQuote, 6);
        Assert.Equal(last.CumQuoteNet, rows.Sum(r => r.NetQuote), 3);
        Assert.Equal(last.CumQuoteAmount, rows.Sum(r => r.AmountQuote), 3);
        Assert.Equal(last.CumBaseNet, rows.Sum(r => r.NetBase), 6);
        Assert.All(rows, r =>
        {
            Assert.Equal(r.AmountQuote, r.BuyQuote + r.SellQuote, 6);
            Assert.Equal(r.NetQuote, r.BuyQuote - r.SellQuote, 6);
            Assert.True(r.Low <= r.High);
        });
        Assert.Equal(day.Points[0].Open, rows[0].Open);
        Assert.Equal(last.Close, rows[^1].Close);

        // 시간 단위 응답은 하루로 묶인다 (하루 안 마지막 점의 누적이 그날 끝)
        var hour = Series.Parse(CoreTests.Fixture("series-hour.json"));
        var hRows = DailyAggregator.ToDays(Coin(), "TAKER", hour.Points, 0, hour.Points[^1].Time);
        Assert.True(hRows.Count < hour.Points.Count);
        Assert.Equal(hour.Points.Select(p => DailyRow.DayStart(p.Time)).Distinct().Count(), hRows.Count);
        Assert.Equal(hour.Points[^1].CumQuoteNet, hRows.Sum(r => r.NetQuote), 3);
        Assert.False(hRows[^1].Final); // 오늘은 진행 중
        Assert.Equal(hour.Points.Max(p => p.High), hRows.Max(r => r.High));
        Assert.Equal("2026-04-01", DailyRow.DayText(1775001600000));
    }

    /// <summary>series 요청마다 정해진 JSON 을 주고, 요청한 from 을 기록한다.</summary>
    private sealed class SeriesServer(Func<long, string> body) : HttpMessageHandler
    {
        public readonly List<long> Froms = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var from = long.Parse(q["from"]!);
            lock (Froms) Froms.Add(from);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(from)) });
        }
    }

    [Fact]
    public async Task AggregatesFromEarliestThenContinuesIncrementally()
    {
        var full = Series.Parse(CoreTests.Fixture("series-day.json"));
        var lastTime = full.Points[^1].Time;
        // 서버: from 이후 점만, 누적은 from 부터 다시 시작 (실제 서버와 같은 규칙)
        string Body(long from)
        {
            var pts = full.Points.Where(p => p.Time >= from).ToList();
            var baseNet = from == 0 ? 0 : full.Points.Where(p => p.Time < from).Select(p => p.CumQuoteNet).LastOrDefault();
            var baseBase = from == 0 ? 0 : full.Points.Where(p => p.Time < from).Select(p => p.CumBaseNet).LastOrDefault();
            var baseAmt = from == 0 ? 0 : full.Points.Where(p => p.Time < from).Select(p => p.CumQuoteAmount).LastOrDefault();
            var arr = pts.Select(p => $"[{p.Time},{(p.CumQuoteNet - baseNet).ToString("R")},{(p.CumBaseNet - baseBase).ToString("R")},{(p.CumQuoteAmount - baseAmt).ToString("R")},{p.Close.ToString("R")},{p.Open.ToString("R")},{p.High.ToString("R")},{p.Low.ToString("R")}]");
            return "{\"symbol\":\"ETHUSDT\",\"step\":\"DAY\",\"points\":[" + string.Join(",", arr) + "]}";
        }
        var server = new SeriesServer(Body);
        using var api = new ServerApi("http://fake", server);
        using var db = new LocalDb(LocalDb.InMemory);
        var agg = new DailyAggregator(api, db);
        var coin = Coin(first: full.Points[0].Time);

        // ① 처음: from=0 → 전부. '오늘'(마지막 날)은 아직 진행 중
        var n1 = await agg.AggregateOneAsync(coin, SplitMethod.TAKER, default, nowMillis: lastTime + 3600_000);
        Assert.Equal(full.Points.Count, n1);
        Assert.Equal(0, server.Froms[0]);
        var rows = db.GetDaily("SPOT", "ETHUSDT", "TAKER");
        Assert.Equal(full.Points.Count, rows.Count);
        Assert.False(rows[^1].Final);
        Assert.True(rows[^2].Final);
        Assert.Equal(full.Points[^1].CumQuoteNet, rows[^1].CumNetQuote, 3);
        Assert.Equal(full.Points[^1].CumQuoteAmount, rows[^1].CumAmountQuote, 3);
        // 호가가 USDT → 환율 1, USDT 금액 = 그대로
        Assert.All(rows, r => { Assert.Equal(1.0, r.UsdRate); Assert.Equal(r.NetQuote, r.NetUsd); Assert.Equal(r.CumNetQuote, r.CumNetUsd); });

        // ② 같은 날 다시: 마감된 날 다음(=오늘)부터만 받아 그 줄만 다시 쓴다. 누적은 이어진다
        var n2 = await agg.AggregateOneAsync(coin, SplitMethod.TAKER, default, nowMillis: lastTime + 7200_000);
        Assert.Equal(1, n2);
        Assert.Equal(lastTime, server.Froms[1]);
        var rows2 = db.GetDaily("SPOT", "ETHUSDT", "TAKER");
        Assert.Equal(rows.Count, rows2.Count);
        Assert.Equal(rows[^1].CumNetQuote, rows2[^1].CumNetQuote, 3);
        Assert.Equal(rows[^1].NetQuote, rows2[^1].NetQuote, 3);

        // ③ 다음 날: 어제가 마감되고 오늘 데이터는 아직 없음 → 0줄, 어제는 final
        var n3 = await agg.AggregateOneAsync(coin, SplitMethod.TAKER, default, nowMillis: lastTime + DailyRow.DayMillis + 60_000);
        Assert.Equal(1, n3); // 어제(마지막 날)를 다시 받아 마감 처리
        Assert.True(db.GetDaily("SPOT", "ETHUSDT", "TAKER")[^1].Final);
        var n4 = await agg.AggregateOneAsync(coin, SplitMethod.TAKER, default, nowMillis: lastTime + DailyRow.DayMillis + 120_000);
        Assert.Equal(0, n4);

        // ④ 서버가 더 예전 1분봉을 받아 두었으면(first 가 앞당겨짐) 처음부터 다시
        var earlier = coin with { FirstOpenTime = full.Points[0].Time - 30 * DailyRow.DayMillis };
        await agg.AggregateOneAsync(earlier, SplitMethod.TAKER, default, nowMillis: lastTime + DailyRow.DayMillis + 180_000);
        Assert.Equal(0, server.Froms[^1]);

        // 전체 실행: 종목 × 방식, 진도율. BTC 호가 페어(ADA/BTC)는 ① BTCUSDT 를 먼저 집계한 뒤 ② 그날의 BTCUSDT 종가로 USDT 환산
        var progress = new List<AggregateProgress>();
        agg.Progress += progress.Add;
        var btc = Coin("BTCUSDT", full.Points[0].Time);
        var adaBtc = Coin("ADABTC", full.Points[0].Time, quote: "BTC");
        // (오늘 줄 갱신 간격을 0 으로: 방금 쓴 종목도 다시 받게 해서 9건 전부 도는지 본다)
        var keep = DailyAggregator.RefreshTodayEvery;
        DailyAggregator.RefreshTodayEvery = TimeSpan.Zero;
        long total;
        try { total = await agg.RunAsync([adaBtc, coin, btc], Enum.GetValues<SplitMethod>(), 2, default, lastTime + DailyRow.DayMillis + 240_000); }
        finally { DailyAggregator.RefreshTodayEvery = keep; }
        Assert.True(total >= full.Points.Count * 8); // 새 종목 2개 × 3방식 + 기존 종목 2방식(A·B)
        Assert.True(progress[^1].Finished);
        Assert.Equal(9, progress[^1].Total);
        Assert.Equal(9, progress[^1].Done);
        Assert.Equal(100, MainViewModel.Percent(9, 9));
        Assert.Equal(33.3, MainViewModel.Percent(3, 9));
        Assert.Equal(3, db.DailySymbolCount);
        Assert.Equal(full.Points.Count * 9, db.DailyRowCount);
        var btcRows = db.GetDaily("SPOT", "BTCUSDT", "TAKER");
        var adaRows = db.GetDaily("SPOT", "ADABTC", "TAKER");
        for (var i = 0; i < adaRows.Count; i++)
        {
            Assert.Equal(btcRows[i].Close, adaRows[i].UsdRate); // 날마다 다른 환율
            Assert.Equal(adaRows[i].NetQuote * btcRows[i].Close, adaRows[i].NetUsd!.Value, 6);
            Assert.Equal(adaRows[i].AmountQuote * btcRows[i].Close, adaRows[i].AmountUsd!.Value, 6);
        }
        Assert.Equal(adaRows.Sum(r => r.NetUsd!.Value), adaRows[^1].CumNetUsd!.Value, 3);
        Assert.Equal(adaRows.Sum(r => r.AmountUsd!.Value), adaRows[^1].CumAmountUsd!.Value, 3);
        // 환율을 모르는 호가(XYZ)는 USDT 열이 비고 누적 USDT 는 0 유지
        await agg.AggregateOneAsync(Coin("ADAXYZ", full.Points[0].Time, quote: "XYZ"), SplitMethod.TAKER, default, lastTime + DailyRow.DayMillis + 240_000,
            agg.RateFor(Coin("ADAXYZ", quote: "XYZ"), [btc, coin]));
        var xyz = db.GetDaily("SPOT", "ADAXYZ", "TAKER");
        Assert.All(xyz, r => { Assert.Null(r.UsdRate); Assert.Null(r.NetUsd); Assert.Equal(0, r.CumNetUsd); });
        Assert.All(xyz, r => { Assert.Equal("ADA", r.Base); Assert.Equal("XYZ", r.Quote); Assert.Equal(1, r.BaseMult); });

        // 코인별 합산 뷰: ADA = ADA/BTC + ADA/XYZ (XYZ 는 환율 없어 USDT 금액에 안 들어감), 코인 수는 둘 다 더함
        var ada = db.GetCoinDaily("SPOT", "ADA", "TAKER");
        Assert.Equal(adaRows.Count, ada.Count);
        Assert.All(ada, d => Assert.Equal(2, d.Pairs));
        Assert.Contains("BTC", ada[0].Quotes);
        Assert.Contains("XYZ", ada[0].Quotes);
        for (var i = 0; i < ada.Count; i++)
        {
            Assert.Equal(adaRows[i].NetUsd!.Value, ada[i].NetUsd!.Value, 6);
            Assert.Equal(adaRows[i].NetBase + xyz[i].NetBase, ada[i].NetCoins, 6);
        }
        Assert.Equal(ada.Sum(d => d.NetUsd!.Value), ada[^1].CumNetUsd!.Value, 3);
        Assert.Equal(ada.Sum(d => d.NetCoins), ada[^1].CumNetCoins, 3);
        var ethCoin = db.GetCoinDaily("SPOT", "ETH", "TAKER");
        Assert.All(ethCoin, d => { Assert.Equal(1, d.Pairs); Assert.Equal("USDT", d.Quotes); });
        Assert.True(ethCoin[^1].Final);
        // USDT 가 base 인 페어(USDTTRY)로 TRY 호가 환율 = 1 / 종가
        var usdtTry = Coin("USDTTRY", full.Points[0].Time, quote: "TRY");
        await agg.AggregateOneAsync(usdtTry, SplitMethod.TAKER, default, lastTime + DailyRow.DayMillis + 240_000);
        var tryRate = agg.RateFor(Coin("BTCTRY", quote: "TRY"), [usdtTry, btc]);
        var usdtTryRows = db.GetDaily("SPOT", "USDTTRY", "TAKER");
        Assert.Equal(1.0 / usdtTryRows[0].Close, tryRate(usdtTryRows[0].Day)!.Value, 9);
        Assert.Equal(1.0 / usdtTryRows[^1].Close, tryRate(usdtTryRows[^1].Day + 3 * DailyRow.DayMillis)!.Value, 9); // 며칠 비면 직전 종가
        Assert.Null(tryRate(usdtTryRows[0].Day - 30 * DailyRow.DayMillis));

        var coinCsv = TempFile(".csv");
        try
        {
            Assert.Equal(db.CoinDayCount, db.ExportCoinCsv(coinCsv));
            var cl = File.ReadAllLines(coinCsv);
            Assert.StartsWith("market,coin,method,day,pairs,quotes,net_usd,", cl[0]);
            Assert.Contains(cl, l => l.StartsWith("SPOT,ADA,TAKER,2026-04-01,2,"));
        }
        finally
        {
            File.Delete(coinCsv);
        }
        var csv = TempFile(".csv");
        try
        {
            Assert.Equal(db.DailyRowCount, db.ExportDailyCsv(csv));
            var lines = File.ReadAllLines(csv);
            Assert.StartsWith("market,symbol,method,day,usd_rate,net_usd,", lines[0]);
            // 정렬: 시장, 심볼(ADABTC < ADAXYZ < BTCUSDT …), 방식(CLOSE_POSITION < PRICE_PATH < TAKER), 날
            Assert.StartsWith("SPOT,ADABTC,CLOSE_POSITION,2026-04-01,", lines[1]);
            Assert.Contains(lines, l => l.StartsWith("SPOT,BTCUSDT,TAKER,2026-04-01,1,"));
            Assert.Contains(lines, l => l.StartsWith("SPOT,ADAXYZ,TAKER,2026-04-01,,,")); // 환율 없음 → 빈 칸
        }
        finally
        {
            File.Delete(csv);
        }

        // ---- 환율 보충: 환율 페어가 나중에 수집되면 서버 요청 없이 USDT 열을 채운다 ----
        Assert.Contains(db.PairsMissingUsd(), p => p.Symbol == "ADAXYZ" && p.Quote == "XYZ");
        Assert.Equal(0, agg.BackfillUsd([btc, coin])); // 아직 XYZ 환율 없음 → 아무것도 안 바꿈
        var xyzUsdt = Coin("XYZUSDT", full.Points[0].Time);
        await agg.AggregateOneAsync(xyzUsdt, SplitMethod.TAKER, default, lastTime + DailyRow.DayMillis + 240_000);
        var requestsBefore = server.Froms.Count;
        var filled = agg.BackfillUsd([btc, coin, xyzUsdt]);
        Assert.Equal(requestsBefore, server.Froms.Count); // 서버 요청 없음
        Assert.Equal(xyz.Count, filled);
        var xyz2 = db.GetDaily("SPOT", "ADAXYZ", "TAKER");
        var xyzRate = db.GetDaily("SPOT", "XYZUSDT", "TAKER");
        for (var i = 0; i < xyz2.Count; i++)
        {
            Assert.Equal(xyzRate[i].Close, xyz2[i].UsdRate); // 그날의 XYZUSDT 종가
            Assert.Equal(xyz2[i].NetQuote * xyzRate[i].Close, xyz2[i].NetUsd!.Value, 6);
            Assert.Equal(xyz[i].NetQuote, xyz2[i].NetQuote); // 호가 자산 값은 그대로
            Assert.Equal("ADA", xyz2[i].Base);
            Assert.Equal("XYZ", xyz2[i].Quote);
        }
        Assert.Equal(xyz2.Sum(r => r.NetUsd!.Value), xyz2[^1].CumNetUsd!.Value, 3);
        Assert.DoesNotContain(db.PairsMissingUsd(), p => p.Symbol == "ADAXYZ");
        // 코인별 합산 뷰에도 반영: ADA = ADA/BTC + ADA/XYZ 둘 다 USDT 금액
        var adaAfter = db.GetCoinDaily("SPOT", "ADA", "TAKER");
        for (var i = 0; i < adaAfter.Count; i++)
            Assert.Equal(adaRows[i].NetUsd!.Value + xyz2[i].NetUsd!.Value, adaAfter[i].NetUsd!.Value, 6);
        // 전체 실행 끝에 ③ 단계로 자동 수행되고 LastBackfilled 에 집계된다
        await agg.AggregateOneAsync(Coin("ADAQQQ", full.Points[0].Time, quote: "QQQ"), SplitMethod.TAKER, default, lastTime + DailyRow.DayMillis + 240_000);
        Assert.Contains(db.PairsMissingUsd(), p => p.Symbol == "ADAQQQ");
        var qqqUsdt = Coin("QQQUSDT", full.Points[0].Time);
        await agg.RunAsync([qqqUsdt], [SplitMethod.TAKER], 1, default, lastTime + DailyRow.DayMillis + 300_000);
        Assert.Equal(xyz.Count, agg.LastBackfilled);
        Assert.DoesNotContain(db.PairsMissingUsd(), p => p.Symbol == "ADAQQQ");
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath.Trim('/');
            string body = path switch
            {
                "health" => "ok",
                "ranking.json" => CoreTests.Fixture("ranking.json"),
                "series" => CoreTests.Fixture("series-hour.json"),
                "group-series" => CoreTests.Fixture("group.json"),
                "verify.json" => "{\"note\":\"검증 일치 200\"}",
                _ => "",
            };
            return Task.FromResult(new HttpResponseMessage(body.Length > 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            { Content = new StringContent(body) });
        }
    }

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("연결 거부");
    }

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(50);
        Assert.True(cond());
    }

    [Fact]
    public void DbLocationFileOverridesDefaultPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var loc = Path.Combine(dir, "db-path.txt");
            Assert.Equal(Path.Combine(dir, "app.db"), LocalDb.ResolvePath(loc));   // 파일 없음 → 기본
            File.WriteAllText(loc, "  \r\n");
            Assert.Equal(Path.Combine(dir, "app.db"), LocalDb.ResolvePath(loc));   // 비어 있음 → 기본
            File.WriteAllText(loc, @"E:\BinanceCumulative\app.db" + "\r\n");
            Assert.Equal(@"E:\BinanceCumulative\app.db", LocalDb.ResolvePath(loc));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ShowsCachedDataWhenServerIsDown()
    {
        var path = TempFile(".db");
        try
        {
            // ① 서버가 있을 때: 순위·그래프를 DB 에 저장, 자동 일별 집계가 돌아 DB 에 쌓임
            using (var db = new LocalDb(path))
            {
                var settings = new AppSettings { AutoStartServer = false, Group = "USDT", Period = "1M", Method = "TAKER", AutoDailyAggregate = true, ExcludedCoins = [] };
                using var vm = new MainViewModel(settings, db, new FakeServer()) { SaveOnDispose = true };
                await vm.InitializeAsync(AppContext.BaseDirectory);
                Assert.False(vm.Offline);
                Assert.Equal(11, vm.Rows.Count);
                await Until(() => vm.DetailSeries != null);
                await Until(() => !vm.Aggregating && vm.AggregateStatus.StartsWith("완료"));
                Assert.Equal(100, vm.AggregatePercent);
                Assert.Equal(16, db.DailySymbolCount);
                Assert.Contains("마지막 일별 집계", vm.LastAggregateText);
                Assert.NotNull(db.GetRanking());
                Assert.NotNull(db.GetSeries("SPOT", vm.Selected!.Row.Coin.Symbol, "1M", "TAKER"));
            }

            // ② 서버가 없을 때: 같은 DB 로 다시 켜면 저장본으로 화면을 채운다
            using (var db = new LocalDb(path))
            {
                var settings = AppSettings.Load(db);
                Assert.Equal("USDT", settings.Group); // ①에서 저장된 설정
                settings.AutoStartServer = false;
                using var vm = new MainViewModel(settings, db, new DownHandler()) { SaveOnDispose = false };
                await vm.InitializeAsync(AppContext.BaseDirectory);
                Assert.True(vm.StatusIsError);
                Assert.True(vm.Offline);
                Assert.Contains("저장", vm.CacheNote);
                Assert.Equal(11, vm.Rows.Count);
                Assert.NotNull(vm.DetailSeries);
                Assert.Null(vm.DetailMessage);
                Assert.True(db.CoinDayCount > 0); // 코인별 합산 뷰도 채워짐
                vm.StartDailyAggregate(); // 서버가 없으면 집계는 시작하지 않음
                Assert.False(vm.Aggregating);
            }
        }
        finally
        {
            Delete(path);
        }
    }
}
