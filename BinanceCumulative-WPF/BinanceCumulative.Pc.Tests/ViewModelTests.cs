using System.Net;
using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.ViewModels;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

/// <summary>가짜 서버로 화면 상태 흐름 전체를 확인한다 (WPF 없이).</summary>
public class ViewModelTests
{
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath.Trim('/');
            lock (Requests) Requests.Add(req.RequestUri.PathAndQuery);
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

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(50);
        Assert.True(cond());
    }

    [Fact]
    public async Task LoadsRankingDetailGroupAndTiles()
    {
        var fake = new FakeServer();
        var settings = new AppSettings { AutoStartServer = false, Group = "USDT", Period = "1M", Method = "TAKER", ExcludedCoins = [] };
        using var vm = new MainViewModel(settings, null, fake) { SaveOnDispose = false };
        await vm.InitializeAsync(AppContext.BaseDirectory);

        Assert.False(vm.StatusIsError, vm.Status);
        Assert.Equal(11, vm.Rows.Count); // 현물 16 페어 → 코인 11개로 합산
        Assert.Equal(RankingCalc.AllUsd, vm.Group);
        Assert.Contains(vm.Rows, x => x.Row.IsCoin && x.Row.Pairs.Count == 3 && x.Base == "ETH");
        Assert.Contains("(16 페어)", vm.TotalCounts);
        Assert.Equal(vm.Rows[0], vm.Selected);
        Assert.Contains("USDT 페어 10개", vm.Groups.Select(g => g.Label));
        Assert.Equal("검증 일치 200", vm.VerifyNote);
        // 금액은 언제나 USDT, 가격 단위(USDT·BTC)는 현재가에만
        Assert.EndsWith("USDT", vm.TotalNet);
        Assert.Equal("USDT", vm.Unit);
        Assert.Contains(vm.Units, u => u.Key == "BTC");
        var totalUsdt = vm.TotalNet;
        var priceUsdt = vm.Rows[0].PriceText;
        vm.DisplayUnit = "BTC";
        Assert.Equal("USDT", vm.Unit);              // 금액 단위는 그대로
        Assert.Equal(totalUsdt, vm.TotalNet);       // 합계도 그대로
        // 가격 단위(BTC)는 가격 그래프에만 적용 — 표·지표의 현재가는 그대로 USDT
        Assert.True(vm.PriceInBtc);
        Assert.Equal("현재가 (USDT)", vm.PriceHeader);
        Assert.Equal("USDT", vm.Rows[0].PriceUnitName);
        Assert.Equal(priceUsdt, vm.Rows[0].PriceText);
        Assert.EndsWith(" USDT", vm.KpiPrice);
        Assert.EndsWith(" USDT", vm.KpiNet);
        Assert.Equal("BTC", vm.Settings.DisplayUnit);
        vm.DisplayUnit = RankingCalc.UsdUnit;
        Assert.Equal("현재가 (USDT)", vm.PriceHeader);

        await Until(() => vm.DetailSeries != null);
        Assert.Null(vm.DetailMessage);
        Assert.Null(vm.GroupSeries);   // 그룹 전체 그래프·여러 페어 타일은 없앰
        Assert.Empty(vm.Tiles);

        // 정렬: 같은 열 → 뒤집기
        var first = vm.Rows[0].Id;
        vm.SortBy(SortKey.Net);
        Assert.Equal(first, vm.Rows[^1].Id);
        Assert.Equal(first, vm.Selected!.Id); // 선택은 유지

        // 그룹은 항상 전체(USD 환산) — 코인 11개
        vm.Group = RankingCalc.AllUsd;
        Assert.Equal(11, vm.Rows.Count);
        Assert.Equal("USDT", vm.Unit);
        var selectedCoin = vm.Selected!;
        Assert.Contains(selectedCoin.Base, vm.DetailTitle);
        if (selectedCoin.Row.Pairs.Count > 1) Assert.Contains("페어 합산", vm.DetailSub);

        // 분할 방식 (화면 목록은 문자열 키)
        vm.MethodKey = "PRICE_PATH";
        Assert.Equal(SplitMethod.PRICE_PATH, vm.Method);
        Assert.True(vm.MethodIsEstimate);

        // 검색
        vm.Query = "btc";
        Assert.All(vm.Rows, r => Assert.Contains("BTC", r.Row.Coin.Symbol));

        // 다른 페어 선택 → 그 페어 그래프 요청
        vm.Selected = vm.Rows[^1];
        await Until(() => fake.Requests.Any(q => q.Contains($"s={vm.Rows[^1].Row.Coin.Symbol}")));
        Assert.Contains(vm.Rows[^1].Row.Coin.BaseAsset, vm.DetailTitle);
    }

    /// <summary>
    /// WPF DataGrid 는 ItemsSource(Rows)가 바뀌는 순간 SelectedItem 을 null 로 되돌려 보낸다.
    /// 그때 상세 그래프가 비워진 채 남지 않아야 한다 (1분마다 새로고침할 때마다 그래프가 사라지던 버그).
    /// </summary>
    [Fact]
    public async Task KeepsDetailWhenGridResetsSelectionOnRefresh()
    {
        var fake = new FakeServer();
        var settings = new AppSettings { AutoStartServer = false, Period = "1M", Method = "TAKER", AutoDailyAggregate = false, ExcludedCoins = [] };
        using var vm = new MainViewModel(settings, null, fake) { SaveOnDispose = false };
        // 표 흉내: Rows 가 바뀌면 곧바로 선택을 null 로
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Rows)) vm.Selected = null; };
        await vm.InitializeAsync(AppContext.BaseDirectory);
        await Until(() => vm.DetailSeries != null);
        var selectedId = vm.Selected!.Id;
        Assert.NotEqual("표시할 페어가 없습니다", vm.DetailTitle);

        await vm.RefreshAsync(); // 1분 새로고침과 같은 경로
        Assert.NotNull(vm.Selected);
        Assert.Equal(selectedId, vm.Selected!.Id);
        Assert.NotEqual("표시할 페어가 없습니다", vm.DetailTitle);
        Assert.NotEqual("표시할 페어가 없습니다", vm.DetailMessage);
        await Until(() => vm.DetailSeries != null && vm.DetailMessage == null);
        Assert.NotEqual("-", vm.KpiNet);
        // 상세 머리글의 모든 지표가 선택한 줄과 같다
        var row = vm.Selected!;
        Assert.Equal(row.PctText, vm.KpiPct);
        Assert.Equal(row.PctSign, vm.KpiPctSign);
        Assert.StartsWith(row.NetText, vm.KpiNet);
        Assert.StartsWith(row.CoinsText, vm.KpiCoins);
        if (row.Row.NetPctOfSupply != null) Assert.NotEqual("-", vm.KpiPct);
    }

    [Fact]
    public async Task ReportsMissingServer()
    {
        var settings = new AppSettings { ServerUrl = "http://192.168.250.250:1", AutoStartServer = true };
        using var vm = new MainViewModel(settings, null, new DownHandler()) { SaveOnDispose = false };
        await vm.InitializeAsync(AppContext.BaseDirectory);
        Assert.True(vm.StatusIsError);
        Assert.Contains("연결할 수 없습니다", vm.Status);
    }

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("연결 거부");
    }

    [Fact]
    public void PriceInBtcUsesBtcPriceAtEachTime()
    {
        static SeriesPoint P(long t, double close) => new(t, 0, 0, 0, close, close, close, close);
        var coin = new Series("DAY", [P(1, 10), P(2, 20), P(3, 30)]);
        var btc = new Series("DAY", [P(1, 100), P(2, 400)]);   // 3 시점 BTC 없음 → 그 점은 뺌
        var r = MainViewModel.PriceInBtcSeries(coin, 1, btc);
        Assert.Equal(new[] { 0.1, 0.05 }, r.Points.Select(p => p.Close));
        // 지금 BTC 가격 하나로 나누면 0.025·0.05 가 되어 추이가 틀어진다 — 시점별로 나눠야 0.1 → 0.05
    }

    [Fact]
    public void ExcludedCoinsAreHiddenFromTableAndTotals()
    {
        Assert.Contains("BTC", AppSettings.DefaultExcludedCoins);
        Assert.Contains("USDT", AppSettings.DefaultExcludedCoins);
        Assert.Contains("WBTC", AppSettings.DefaultExcludedCoins);
        Assert.Contains("USDC", AppSettings.DefaultExcludedCoins);
        Assert.DoesNotContain("ETH", AppSettings.DefaultExcludedCoins);
        Assert.Equal(AppSettings.DefaultExcludedCoins, new AppSettings().ExcludedCoins);
    }

    [Fact]
    public async Task DefaultExclusionRemovesBtcFromRowsAndTotals()
    {
        var all = new AppSettings { AutoStartServer = false, Period = "1M", Method = "TAKER", AutoDailyAggregate = false, ExcludedCoins = [] };
        using var a = new MainViewModel(all, null, new FakeServer()) { SaveOnDispose = false };
        await a.InitializeAsync(AppContext.BaseDirectory);
        Assert.Contains(a.Rows, r => r.Base == "BTC"); // 시험 데이터에 BTC 가 있다

        var def = new AppSettings { AutoStartServer = false, Period = "1M", Method = "TAKER", AutoDailyAggregate = false };
        using var b = new MainViewModel(def, null, new FakeServer()) { SaveOnDispose = false };
        await b.InitializeAsync(AppContext.BaseDirectory);
        Assert.DoesNotContain(b.Rows, r => AppSettings.DefaultExcludedCoins.Contains(r.Base));
        Assert.True(b.Rows.Count < a.Rows.Count);
        Assert.NotEqual(a.TotalCounts, b.TotalCounts); // 합계(페어 수)에서도 빠짐
    }
}
