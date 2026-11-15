using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Models;
using ScottPlot;

namespace BinanceCumulative.Pc.Charts;

/// <summary>
/// 여러 그래프에 같은 시각의 세로선을 그리고, 그 시각의 값(가격·누적 순매수·코인)을 글로 알려준다.
/// 그래프를 새로 그린 뒤에는 <see cref="Attach"/> 를 다시 불러야 한다 (Clear 가 선을 지움).
/// </summary>
public sealed class ChartCursor
{
    private readonly Dictionary<Plot, ScottPlot.Plottables.VerticalLine> _lines = new();

    public void Attach(Plot plot)
    {
        var line = plot.Add.VerticalLine(0);
        line.Color = Theme.Muted.WithAlpha(.8);
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dotted;
        line.IsVisible = false;
        _lines[plot] = line;
    }

    /// <summary>x(OADate) 위치에 세로선 표시. null 이면 숨김.</summary>
    public void Show(double? x)
    {
        foreach (var line in _lines.Values)
        {
            line.IsVisible = x.HasValue;
            if (x is { } v) line.X = v;
        }
    }

    /// <summary>그래프 화면 위치(픽셀) → 시각(OADate). 데이터 영역 밖이면 null.</summary>
    public static double? XAt(Plot plot, float pixelX, float pixelY)
    {
        var c = plot.GetCoordinates(new Pixel(pixelX, pixelY));
        var lim = plot.Axes.GetLimits();
        return c.X >= lim.Left && c.X <= lim.Right ? c.X : null;
    }

    /// <summary>x 에 가장 가까운 점과 설명 글.</summary>
    public static (SeriesPoint Point, string Text)? Describe(Series? s, double x, string quote, string coin)
    {
        if (s == null || s.Points.Count == 0) return null;
        var t = DateTime.FromOADate(x);
        var best = s.Points.MinBy(p => Math.Abs((p.TimeLocal - t).Ticks));
        var when = best.TimeLocal.ToString(s.Step == "HOUR" ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd");
        var text = $"{when}  ·  가격 {Fmt.Price(best.Close)} {quote} (시 {Fmt.Price(best.Open)} 고 {Fmt.Price(best.High)} 저 {Fmt.Price(best.Low)})" +
                   $"  ·  누적 순매수 {Fmt.Ko(best.CumQuoteNet, true)} {quote}  ·  {Fmt.Ko(best.CumBaseNet, true)} {coin}";
        return (best, text);
    }
}
