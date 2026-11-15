using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Models;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

public class CoreTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesServerRanking()
    {
        var r = Ranking.Parse(Fixture("ranking.json"));
        Assert.Equal(16, r.Coins.Count);
        Assert.Contains(r.Periods, p => p.Key == "6M");
        var eth = r.Coins.Single(c => c.Symbol == "ETHBTC");
        Assert.Equal("BTC", eth.QuoteAsset);
        Assert.Equal("SPOT:ETHBTC", eth.Id);
        Assert.NotNull(eth.UsdRate); // BTCUSDT 가격으로 환산
        var t = eth.Periods["1M"][SplitMethod.TAKER];
        Assert.Equal(t.Quote.Buy * 2 - t.Quote.Amount, t.Quote.Net);
    }

    [Fact]
    public void RowsFilterSortAndUsd()
    {
        var r = Ranking.Parse(Fixture("ranking.json"));
        var usdt = RankingCalc.Rows(r, "SPOT", "USDT", "1M", SplitMethod.TAKER, SortKey.Net, descending: true);
        Assert.Equal(10, usdt.Count);
        Assert.All(usdt, x => Assert.Equal("USDT", x.Coin.QuoteAsset));
        Assert.True(usdt.Zip(usdt.Skip(1)).All(p => p.First.Quote.Net >= p.Second.Quote.Net));

        var asc = RankingCalc.Rows(r, "SPOT", "USDT", "1M", SplitMethod.TAKER, SortKey.Net, descending: false);
        Assert.Equal(usdt.Select(x => x.Id).Reverse(), asc.Select(x => x.Id));

        // USD 환산 전체: 16개 모두 환율이 있음 (FDUSD 는 1달러, TRY 는 USDTTRY 역수)
        var all = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Net, true);
        Assert.Equal(16, all.Count);
        var ethBtc = all.Single(x => x.Coin.Symbol == "ETHBTC");
        var raw = r.Coins.Single(c => c.Symbol == "ETHBTC").Periods["1M"][SplitMethod.TAKER].Quote.Net;
        Assert.Equal(raw * ethBtc.Coin.UsdRate!.Value, ethBtc.Quote.Net);
        Assert.Equal("USD", ethBtc.Unit);

        var search = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Name, false, "eth");
        Assert.Equal(new[] { "ETH/BTC", "ETH/FDUSD", "ETH/USDT" }, search.Select(x => x.Pair));

        var groups = RankingCalc.QuoteGroups(r, "SPOT");
        Assert.Equal(("USDT", 10), groups[0]);

        // 단위 환산: USDT 그룹을 BTC 단위로 → 순매수 ÷ BTCUSDT 현재가. 단위 후보에 BTC 가 있음
        var btcPx = r.Coins.Single(c => c.Symbol == "BTCUSDT").LastPrice!.Value;
        Assert.Equal(btcPx, RankingCalc.UnitUsdPrice(r, "BTC"));
        Assert.Contains("BTC", RankingCalc.UnitOptions(r));
        var inBtc = RankingCalc.Rows(r, "SPOT", "USDT", "1M", SplitMethod.TAKER, SortKey.Net, true, "", "BTC");
        var ethUsdt = usdt.Single(x => x.Coin.Symbol == "ETHUSDT");
        Assert.Equal(ethUsdt.Quote.Net / btcPx, inBtc.Single(x => x.Coin.Symbol == "ETHUSDT").Quote.Net, 6);
        Assert.All(inBtc, x => Assert.Equal("BTC", x.Unit));
        Assert.Equal("BTC", RankingCalc.EffectiveUnit(r, "USDT", "BTC"));
        Assert.Equal("USDT", RankingCalc.EffectiveUnit(r, "USDT", RankingCalc.QuoteUnit));
        Assert.Equal("USD", RankingCalc.EffectiveUnit(r, RankingCalc.AllUsd, RankingCalc.QuoteUnit));
        Assert.Equal("USD", RankingCalc.EffectiveUnit(r, "USDT", "XYZ")); // 가격 모르는 자산은 USD 로
        // BTC 그룹(ETH/BTC)을 USD 단위로 = 전체 USD 보기와 같은 값
        var btcGroupUsd = RankingCalc.Rows(r, "SPOT", "BTC", "1M", SplitMethod.TAKER, SortKey.Net, true, "", "USD");
        Assert.Equal(ethBtc.Quote.Net, btcGroupUsd.Single(x => x.Coin.Symbol == "ETHBTC").Quote.Net);
    }

    [Fact]
    public void MergesPairsByCoin()
    {
        var r = Ranking.Parse(Fixture("ranking.json"));
        var pairs = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Net, true); // 16 페어, USD
        var coins = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Net, true, "", RankingCalc.UsdUnit, byCoin: true);
        Assert.Equal(16, pairs.Count);
        Assert.Equal(11, coins.Count);
        Assert.True(coins.Zip(coins.Skip(1)).All(p => p.First.Quote.Net >= p.Second.Quote.Net));

        var eth = coins.Single(x => x.Coin.BaseAsset == "ETH");
        var ethPairs = pairs.Where(x => x.Coin.BaseAsset == "ETH").ToList();
        Assert.Equal(3, ethPairs.Count);
        Assert.Equal(3, eth.Pairs.Count);
        Assert.True(eth.IsCoin);
        Assert.Equal("COIN:SPOT:ETH", eth.Id);
        Assert.Equal("ETH", eth.Pair);
        Assert.Equal("USD", eth.Unit);
        Assert.Equal(ethPairs.Sum(x => x.Quote.Net), eth.Quote.Net, 6);
        Assert.Equal(ethPairs.Sum(x => x.Quote.Buy), eth.Quote.Buy, 6);
        Assert.Equal(ethPairs.Sum(x => x.Quote.Amount), eth.Quote.Amount, 6);
        Assert.Equal(ethPairs.Sum(x => x.Base.Net * x.Coin.BaseMultiplier), eth.Base.Net, 6);
        Assert.Equal(ethPairs.OrderByDescending(x => x.Quote.Amount).First().Coin.Symbol, eth.Coin.Symbol); // 대표 = 거래대금 최대
        Assert.Equal(r.Coins.Single(c => c.Symbol == "ETHUSDT").LastPrice, eth.PriceUsd);
        Assert.Equal("BTC, FDUSD, USDT".Split(", ").OrderBy(x => x), eth.PairList.Split(", ").OrderBy(x => x));

        // 한 페어뿐인 코인도 코인 줄 (Id 가 COIN:)
        var single = coins.Single(x => x.Coin.BaseAsset == "ADA");
        Assert.True(single.IsCoin);
        Assert.Single(single.Pairs);

        // 합계 보존: 코인 줄 합 = 페어 줄 합
        Assert.Equal(pairs.Sum(x => x.Quote.Net), coins.Sum(x => x.Quote.Net), 6);
        Assert.Equal(pairs.Sum(x => x.Quote.Amount), coins.Sum(x => x.Quote.Amount), 6);

        // BTC 기준: 모든 값 ÷ BTCUSDT 현재가
        var btcPx = r.Coins.Single(c => c.Symbol == "BTCUSDT").LastPrice!.Value;
        var inBtc = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Net, true, "", "BTC", byCoin: true);
        Assert.Equal(eth.Quote.Net / btcPx, inBtc.Single(x => x.Coin.BaseAsset == "ETH").Quote.Net, 6);
        Assert.All(inBtc, x => Assert.Equal("BTC", x.Unit));
        Assert.Equal(new[] { "USD", "BTC" }, RankingCalc.UnitOptions(r));
        Assert.Equal("USD", RankingCalc.EffectiveUnit(r, RankingCalc.AllUsd, RankingCalc.QuoteUnit, byCoin: true));

        // 검색은 페어 심볼·코인 이름으로, 합친 뒤에도 적용
        var search = RankingCalc.Rows(r, "SPOT", RankingCalc.AllUsd, "1M", SplitMethod.TAKER, SortKey.Name, false, "eth", RankingCalc.UsdUnit, byCoin: true);
        Assert.Single(search);
        Assert.Equal("ETH", search[0].Coin.BaseAsset);
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("+1.23억", Fmt.Ko(123_456_789m, true));
        Assert.Equal("-4.57만", Fmt.Ko(-45_678m, true));
        Assert.Equal("1,234.50", Fmt.Ko(1234.5));
        Assert.Equal("0", Fmt.Ko(0.0));
        Assert.Equal("0.000012", Fmt.Ko(0.000012));
        Assert.Equal("50M", Fmt.Axis(5e7));
        Assert.Equal("120M", Fmt.Axis(1.2e8));
        Assert.Equal("+0.005000%", Fmt.Pct(0.005m));
        Assert.Equal("-", Fmt.Pct(null));
        Assert.Equal("68,123.5", Fmt.Price(68123.5));
        Assert.Equal("0.0000000014", Fmt.Price(1.4e-9));   // BTC 로 본 아주 싼 코인도 0 이 아님
        Assert.Equal("0.01356", Fmt.Price(0.013555));
        Assert.Equal("0.3125", Fmt.Price(0.3125));
    }

    [Fact]
    public void ParsesSeriesWithCandles()
    {
        var day = Series.Parse(Fixture("series-day.json"));
        Assert.Equal("DAY", day.Step);
        Assert.Equal(TimeSpan.FromDays(1), day.BucketSpan);
        Assert.True(day.Points.Count > 150);
        Assert.All(day.Points, p => Assert.True(p.Low <= p.Open && p.Open <= p.High && p.Low <= p.Close && p.Close <= p.High));
        Assert.Contains(day.Points, p => p.High > p.Low); // 캔들 폭이 있음

        var hour = Series.Parse(Fixture("series-hour.json"));
        Assert.Equal(TimeSpan.FromHours(1), hour.BucketSpan);
        var g = GroupSeries.Parse(Fixture("group.json"));
        Assert.NotEmpty(g.Points);
        Assert.Throws<InvalidOperationException>(() => Series.Parse("{\"error\":\"s(심볼) 필요\"}"));
    }

    [Fact]
    public void JavaVersionParsing()
    {
        Assert.Equal(8, Host.ServerHost.ParseJavaMajor("1.8.0_231"));
        Assert.Equal(17, Host.ServerHost.ParseJavaMajor("17.0.12"));
        Assert.Equal(21, Host.ServerHost.ParseJavaMajor("21"));
        Assert.Equal(11, Host.ServerHost.ParseJavaMajor("11.0.2+9"));
        Assert.Equal(0, Host.ServerHost.ParseJavaMajor("abc"));
        Assert.Equal(0, Host.ServerHost.JavaMajor(Path.Combine(Path.GetTempPath(), "no-such-java.exe")));
    }

    [Fact]
    public void ServerArgsFromSettings()
    {
        var s = new Host.AppSettings { ServerUrl = "http://localhost:9090", IncludeFutures = true, Months = 12, KeepRaw = true, CmcKey = " k " };
        var a = s.ServerArgs();
        Assert.Equal(new[] { "--no-browser", "--port", "9090", "--data", s.DataFolder, "--months", "12", "--markets", "spot,futures", "--keep-raw", "--threads", "12", "--cmc-key", "k" }, a);
        Assert.True(s.IsLocalServer);
        Assert.False(new Host.AppSettings { ServerUrl = "http://192.168.0.10:8080" }.IsLocalServer);
    }
}
