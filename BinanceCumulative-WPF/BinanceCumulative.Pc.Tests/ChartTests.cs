using BinanceCumulative.Pc.Charts;
using BinanceCumulative.Pc.Models;
using ScottPlot;
using Xunit;

namespace BinanceCumulative.Pc.Tests;

/// <summary>그래프를 실제로 그려 PNG 로 저장 (CHART_OUT 이 있으면 그 폴더에).</summary>
public class ChartTests
{
    private static string Out(string name)
    {
        var dir = Environment.GetEnvironmentVariable("CHART_OUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void RendersAllCharts()
    {
        // 윈도우 앱은 맑은 고딕, 이 테스트 환경은 설치된 한글 글꼴
        ChartBuilder.FontName = OperatingSystem.IsWindows() ? "Malgun Gothic" : "WenQuanYi Zen Hei";
        var day = Series.Parse(CoreTests.Fixture("series-day.json"));
        var hour = Series.Parse(CoreTests.Fixture("series-hour.json"));
        var group = GroupSeries.Parse(CoreTests.Fixture("group.json"));

        var shots = new (string Name, Action<Plot> Build)[]
        {
            ("price-candles.png", p => ChartBuilder.Price(p, new Series("DAY", day.Points.TakeLast(90).ToList()), "USDT")),
            ("price-line.png", p => ChartBuilder.Price(p, hour, "USDT")),
            ("net-quote.png", p => ChartBuilder.NetQuote(p, hour)),
            ("net-coins.png", p => ChartBuilder.NetCoins(p, hour)),
            ("group.png", p => ChartBuilder.Group(p, group)),
            ("mini.png", p => ChartBuilder.Mini(p, day)),
            ("empty.png", p => ChartBuilder.Empty(p, "이 기간의 저장된 1분봉이 없습니다")),
        };
        foreach (var (name, build) in shots)
        {
            var p = new Plot();
            build(p);
            var path = Out(name);
            p.SavePng(path, name == "mini.png" ? 420 : 1100, name == "mini.png" ? 240 : 330);
            Assert.True(new FileInfo(path).Length > 2000, name);
        }
    }

    [Fact]
    public void DateAxisShowsYearForMonthTicks()
    {
        Assert.Equal("2024-01", ChartBuilder.AxisDate(new DateTime(2024, 1, 1)));
        Assert.Equal("01-15", ChartBuilder.AxisDate(new DateTime(2024, 1, 15)));
        Assert.Equal("01-15 09:30", ChartBuilder.AxisDate(new DateTime(2024, 1, 15, 9, 30, 0)));
    }

    [Fact]
    public void LinkerSyncsXAxis()
    {
        var a = new Plot();
        var b = new Plot();
        var hour = Series.Parse(CoreTests.Fixture("series-hour.json"));
        ChartBuilder.NetQuote(a, hour);
        ChartBuilder.Price(b, hour, "USDT");
        var refreshed = 0;
        var linker = new ChartLinker();
        linker.Add(a, () => { });
        linker.Add(b, () => refreshed++);
        a.Axes.SetLimitsX(1, 2);
        a.RenderInMemory(400, 300); // 그릴 때 AxisLimitsChanged 발생
        Assert.Equal(1, b.Axes.GetLimits().Left, 6);
        Assert.Equal(2, b.Axes.GetLimits().Right, 6);
        Assert.True(refreshed >= 1);
    }

    [Fact]
    public void CursorDescribesNearestPoint()
    {
        var hour = Series.Parse(CoreTests.Fixture("series-hour.json"));
        var p = new Plot();
        ChartBuilder.NetQuote(p, hour);
        var cursor = new ChartCursor();
        cursor.Attach(p);
        var target = hour.Points[10];
        var d = ChartCursor.Describe(hour, target.TimeLocal.AddMinutes(20).ToOADate(), "USDT", "ETH");
        Assert.NotNull(d);
        Assert.Equal(target.Time, d!.Value.Point.Time);
        Assert.Contains("누적 순매수", d.Value.Text);
        Assert.Contains("USDT", d.Value.Text);
        cursor.Show(target.TimeLocal.ToOADate());
        p.RenderInMemory(300, 200);
        cursor.Show(null);
        // 그린 뒤 화면 가운데 픽셀 → 데이터 범위 안의 시각
        var mid = ChartCursor.XAt(p, 150, 100);
        Assert.NotNull(mid);
        Assert.InRange(mid!.Value, hour.Points[0].TimeLocal.ToOADate() - 1, hour.Points[^1].TimeLocal.ToOADate() + 1);
        Assert.Null(ChartCursor.Describe(null, 0, "USDT", "ETH"));
    }
}
