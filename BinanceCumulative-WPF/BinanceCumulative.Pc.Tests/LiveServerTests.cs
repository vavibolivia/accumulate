using BinanceCumulative.Pc.Api;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.Storage;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

/// <summary>
/// 실제로 떠 있는 계산 서버(기본 http://localhost:8080)에 붙어 일별 집계를 한 종목 돌려 본다.
/// 환경변수 BC_LIVE=1 일 때만 실행 (CI·평소 테스트에서는 건너뜀).
/// </summary>
public class LiveServerTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BC_LIVE") == "1";
    private static string Url => Environment.GetEnvironmentVariable("BC_URL") ?? "http://localhost:8080";

    [Fact]
    public async Task AggregatesOneSymbolFromLiveServer()
    {
        if (!Enabled) return;
        using var api = new ServerApi(Url);
        Assert.True(await api.IsAliveAsync(), "서버 없음");
        var ranking = (await api.GetRankingAsync()).Value;
        Assert.NotEmpty(ranking.Coins);
        using var db = new LocalDb(LocalDb.InMemory);
        var agg = new DailyAggregator(api, db);
        var errors = new List<string>();
        agg.Progress += p => { if (p.Error != null) errors.Add(p.Error); };
        var coins = ranking.Coins.Take(4).ToList();
        var rows = await agg.RunAsync(coins, [SplitMethod.TAKER], 2, default);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Distinct()));
        Assert.True(rows > 0, $"0줄 — coins: {string.Join(",", coins.Select(c => c.Symbol))}");
        foreach (var c in coins)
        {
            var d = db.GetDaily(c.Market, c.Symbol, "TAKER");
            Assert.NotEmpty(d);
            Assert.True(d[0].Day <= DailyRow.DayStart(c.FirstOpenTime ?? long.MaxValue) + DailyRow.DayMillis, $"{c.Symbol} first {DailyRow.DayText(d[0].Day)}");
        }
    }
}
