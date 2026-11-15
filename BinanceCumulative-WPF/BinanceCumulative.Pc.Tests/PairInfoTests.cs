using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Models;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

public class PairInfoTests
{
    [Fact]
    public void CollectedCountsMonthFilesFromListingMonth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var listed = new DateTimeOffset(2026, 8, 17, 4, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc); // 8·9·10·11월 = 4달
            Assert.Equal((0, 4), PairInfo.Count(dir, listed, now));
            File.WriteAllText(Path.Combine(dir, "2026-08.days.json"), "[]");
            File.WriteAllText(Path.Combine(dir, "2026-08.bin.gz"), "");
            File.WriteAllText(Path.Combine(dir, "2026-09.bin.gz.tmp"), ""); // 받는 중 → 안 셈
            File.WriteAllText(Path.Combine(dir, "2026-11.bin"), "");
            File.WriteAllText(Path.Combine(dir, "meta.json"), "{}");
            Assert.Equal((2, 4), PairInfo.Count(dir, listed, now));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StockTokensComeFromBinanceTag()
    {
        // 바이낸스 상품 목록: tags 에 bStocks 가 있는 것만 주식
        const string json = """
            {"code":"000000","data":[
              {"s":"AAPLBUSDT","b":"AAPLB","q":"USDT","tags":["bStocks"]},
              {"s":"NEWCOBUSDT","b":"NEWCOB","q":"USDT","tags":["bStocks","newListing"]},
              {"s":"ORCAUSDT","b":"ORCA","q":"USDT","tags":["defi","Seed","Solana"]},
              {"s":"PAXGUSDT","b":"PAXG","q":"USDT","tags":["tCommodities"]},
              {"s":"BNBUSDT","b":"BNB","q":"USDT"}
            ]}
            """;
        Assert.Equal(new[] { "AAPLB", "NEWCOB" }, PairInfo.ParseStockBases(json));
        Assert.Null(PairInfo.ParseStockBases("""{"code":"1","data":null}"""));
        // 기본 목록: 주식은 들어 있고, B 로 끝나는 보통 코인은 들어 있지 않다
        Assert.Contains("NVDAB", PairInfo.DefaultStockBases);
        Assert.Contains("SPYB", PairInfo.DefaultStockBases);
        Assert.DoesNotContain("BNB", PairInfo.DefaultStockBases);
        Assert.DoesNotContain("BB", PairInfo.DefaultStockBases);
        Assert.DoesNotContain("SHIB", PairInfo.DefaultStockBases);
        Assert.True(new AppSettings().ExcludeStocks);
    }

    [Fact]
    public void AllTimePeriodComesFromDailyTotals()
    {
        var coin = new RankingCoin("BTCUSDT", "BTC", "USDT", "SPOT", 1m, null, null, 1m, 1m, null,
            new Dictionary<string, IReadOnlyDictionary<SplitMethod, (Totals Quote, Totals Base)>>());
        var r = new Ranking(1, "", [new RankPeriod("5Y", "5년", 60, 0)], [coin]);
        var totals = new Dictionary<string, Dictionary<string, Storage.AllTime>>
        {
            ["SPOT:BTCUSDT"] = new() { ["TAKER"] = new Storage.AllTime(100m, 60m, 3m, null, null) },
        };
        var all = RankingCalc.WithAllTime(r, totals);
        Assert.Equal(RankingCalc.AllTimePeriod, all.Periods[^1].Key);
        var (q, b) = all.Coins[0].Periods[RankingCalc.AllTimePeriod][SplitMethod.TAKER];
        Assert.Equal(20m, q.Net);   // 매수 60 − 매도 40
        Assert.Equal(3m, b.Net);
        Assert.Same(all, RankingCalc.WithAllTime(all, totals)); // 두 번 붙이지 않음
    }

    [Fact]
    public void ParsesServerUpdateProgress()
    {
        var (pct, text) = ViewModels.MainViewModel.ParseUpdateProgress("ok · 1372개 중 343개 최신화 중 · 343개 페어 · 검증 대기 중");
        Assert.Equal(25.0, pct);
        Assert.Contains("343/1,372", text);
        Assert.Equal(0, ViewModels.MainViewModel.ParseUpdateProgress("ok · 시작 중 · 13개 페어").Percent);
    }

    [Fact]
    public void AllTimeUsesDailyUsdtNotTodaysRate()
    {
        // ETH/BTC: 호가 금액 10 BTC 지만 그날 환율로는 300 USDT (매수 200). 지금 BTC = 100 USDT 라면 10×100=1000 으로 부풀리면 안 된다
        var coin = new RankingCoin("ETHBTC", "ETH", "BTC", "SPOT", 1m, null, null, 1m, 100m, null,
            new Dictionary<string, IReadOnlyDictionary<SplitMethod, (Totals Quote, Totals Base)>>());
        var r = new Ranking(1, "", [], [coin]);
        var totals = new Dictionary<string, Dictionary<string, Storage.AllTime>>
        {
            ["SPOT:ETHBTC"] = new() { ["TAKER"] = new Storage.AllTime(10m, 6m, 1m, 300m, 200m) },
        };
        var all = RankingCalc.WithAllTime(r, totals);
        var rows = RankingCalc.Rows(all, "SPOT", RankingCalc.AllUsd, RankingCalc.AllTimePeriod, SplitMethod.TAKER, SortKey.Net, true, "", RankingCalc.UsdUnit, byCoin: true);
        Assert.Equal(300m, rows[0].Quote.Amount);
        Assert.Equal(100m, rows[0].Quote.Net); // 매수 200 − 매도 100
    }

    [Fact]
    public void AggregatorSkipsPairsThatAreUpToDate()
    {
        var day = Storage.DailyRow.DayMillis;
        var now = 100 * day + 5 * 3600_000L;            // 100일째 05:00
        var yesterday = 99 * day;
        var hour = (long)Storage.DailyAggregator.RefreshTodayEvery.TotalMilliseconds;
        Assert.True(Storage.DailyAggregator.NeedsFetch(null, now));                              // 처음 보는 종목
        Assert.True(Storage.DailyAggregator.NeedsFetch((null, now), now));                       // 마감된 날이 없음
        Assert.True(Storage.DailyAggregator.NeedsFetch((yesterday - day, now), now));            // 어제가 아직 마감 저장 안 됨
        Assert.False(Storage.DailyAggregator.NeedsFetch((yesterday, now - 60_000), now));        // 어제까지 끝났고 방금 갱신함 → 건너뜀
        Assert.True(Storage.DailyAggregator.NeedsFetch((yesterday, now - hour), now));           // 오늘 줄을 쓴 지 1시간 → 다시
    }
}
