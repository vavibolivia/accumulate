using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Models;
using ScottPlot;

namespace BinanceCumulative.Pc.Charts;

/// <summary>대시보드 색 (어두운 거래 화면).</summary>
public static class Theme
{
    public static readonly Color Figure = Color.FromHex("#11151B");
    public static readonly Color Data = Color.FromHex("#11151B");
    public static readonly Color Grid = Color.FromHex("#1E242D");
    public static readonly Color Text = Color.FromHex("#E9ECEF");
    public static readonly Color Muted = Color.FromHex("#8A93A0");
    /// <summary>그래프 축 눈금 글자 (어두운 배경 위에서 읽히도록 Muted 보다 밝게)</summary>
    public static readonly Color AxisText = Color.FromHex("#AEB6C2");
    public static readonly Color Accent = Color.FromHex("#F0B90B");
    public static readonly Color Up = Color.FromHex("#0ECB81");
    public static readonly Color Down = Color.FromHex("#F6465D");
    public static readonly Color Price = Color.FromHex("#7B93FF");
}

/// <summary>
/// ScottPlot 그래프 구성. WPF 의 WpfPlot.Plot 과 테스트의 Plot 에 똑같이 쓴다.
/// x 축은 현지 시각(OADate).
/// </summary>
public static class ChartBuilder
{
    /// <summary>한글 표시 가능한 글꼴 (윈도우: 맑은 고딕)</summary>
    private static string? _fontName;

    private const string KoreanFont = "KoreanUI";

    /// <summary>
    /// ScottPlot(Skia)은 글꼴 이름("Malgun Gothic")으로는 한글을 □ 로 그린다 — 글꼴 파일을 직접 등록해야 한다.
    /// 맑은 고딕 파일이 있으면 "KoreanUI" 로 등록해 그 이름을 쓰고, 없으면 받은 이름 그대로.
    /// </summary>
    private static string? Register(string? name)
    {
        if (name == null) return null;
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var regular = Path.Combine(dir, "malgun.ttf");
            if (!File.Exists(regular)) return name;
            ScottPlot.Fonts.AddFontFile(KoreanFont, regular, bold: false, italic: false);
            // 굵은 글꼴을 같은 이름으로 또 등록하면 보통 글자도 □ 가 된다 (ScottPlot 5.0.55) — 보통 하나만
            return KoreanFont;
        }
        catch (Exception)
        {
            return name;
        }
    }

    /// <summary>그래프 글꼴 (한글 단위 억·만·조 표시용). ScottPlot 기본 글꼴도 함께 바꾼다.</summary>
    public static string? FontName
    {
        get => _fontName;
        set
        {
            _fontName = Register(value);

        }
    }

    /// <summary>날짜 눈금 글자: 매월 1일 0시면 "2024-01", 0시면 "01-15", 아니면 "01-15 09:30".</summary>
    public static string AxisDate(DateTime d) =>
        d.TimeOfDay != TimeSpan.Zero ? d.ToString("MM-dd HH:mm")
        : d.Day == 1 ? d.ToString("yyyy-MM")
        : d.ToString("MM-dd");

    public static void Style(Plot p)
    {
        p.FigureBackground.Color = Theme.Figure;
        p.DataBackground.Color = Theme.Data;
        p.Axes.Color(Theme.Muted);
        p.Grid.MajorLineColor = Theme.Grid;
        // 날짜 축: 월 단위 눈금은 연-월, 일 단위는 월-일, 시간 단위로 확대하면 월-일 시:분
        p.Axes.DateTimeTicksBottom();
        p.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic { LabelFormatter = AxisDate };
        foreach (var axis in new IAxis[] { p.Axes.Left, p.Axes.Right, p.Axes.Top, p.Axes.Bottom })
            axis.FrameLineStyle.Color = Theme.Grid;
        var fmt = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = Fmt.Axis };
        p.Axes.Left.TickGenerator = fmt;
        // 눈금 글자는 어두운 배경에서 잘 보이게 밝게
        foreach (var axis in new IAxis[] { p.Axes.Left, p.Axes.Bottom })
        {
            axis.TickLabelStyle.FontSize = 12;
            axis.TickLabelStyle.ForeColor = Theme.AxisText;
        }
        if (FontName != null)
        {
            p.Font.Set(FontName);
            // 눈금 글자(억·만·조)에 한글 글꼴을 직접 지정 — 안 하면 한글이 네모(□)로 나온다
            foreach (var axis in new IAxis[] { p.Axes.Left, p.Axes.Right, p.Axes.Bottom, p.Axes.Top })
                axis.TickLabelStyle.FontName = FontName;
        }
    }

    private static double[] X(IReadOnlyList<SeriesPoint> pts) => pts.Select(x => x.TimeLocal.ToOADate()).ToArray();

    public static void Empty(Plot p, string message)
    {
        p.Clear();
        Style(p);
        var t = p.Add.Text(message, 0, 0);
        t.LabelFontColor = Theme.Muted;
        t.LabelFontSize = 15;
        t.LabelAlignment = Alignment.MiddleCenter;
        if (FontName != null) t.LabelFontName = FontName;
        p.Axes.SetLimits(-1, 1, -1, 1);
        p.Axes.NumericTicksBottom(); // 날짜 축 → 숫자 축으로 바꾼 뒤 눈금 없앰
        p.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
        p.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual();
    }

    /// <summary>가격 캔들 (구간이 많으면 종가 선).</summary>
    public static void Price(Plot p, Series s, string quote)
    {
        p.Clear();
        Style(p);
        p.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = Fmt.Price };
        var pts = s.Points;
        if (pts.Count > 400)
        {
            var line = p.Add.Scatter(X(pts), pts.Select(x => x.Close).ToArray());
            line.Color = Theme.Price;
            line.LineWidth = 1.5f;
            line.MarkerSize = 0;
        }
        else
        {
            var ohlc = pts.Select(x => new OHLC(x.Open, x.High, x.Low, x.Close, x.TimeLocal, s.BucketSpan)).ToList();
            var c = p.Add.Candlestick(ohlc);
            c.RisingColor = Theme.Up;
            c.FallingColor = Theme.Down;
        }
        p.Axes.AutoScale();
    }

    private static void NetArea(Plot p, double[] xs, double[] ys, Color color)
    {
        var area = p.Add.FillY(xs, ys, new double[ys.Length]);
        area.FillColor = color.WithAlpha(.18);
        area.LineColor = Colors.Transparent;
        var line = p.Add.Scatter(xs, ys);
        line.Color = color;
        line.LineWidth = 2;
        line.MarkerSize = 0;
        var zero = p.Add.HorizontalLine(0);
        zero.Color = Theme.Muted.WithAlpha(.6);
        zero.LineWidth = 1;
        zero.LinePattern = LinePattern.Dashed;
    }

    /// <summary>누적 순매수 금액 (마지막 값이 플러스면 초록, 마이너스면 빨강).</summary>
    public static void NetQuote(Plot p, Series s)
    {
        p.Clear();
        Style(p);
        var ys = s.Points.Select(x => x.CumQuoteNet).ToArray();
        NetArea(p, X(s.Points), ys, ys.LastOrDefault() >= 0 ? Theme.Up : Theme.Down);
        p.Axes.AutoScale();
    }

    /// <summary>누적 순매수 코인 수.</summary>
    public static void NetCoins(Plot p, Series s)
    {
        p.Clear();
        Style(p);
        NetArea(p, X(s.Points), s.Points.Select(x => x.CumBaseNet).ToArray(), Theme.Accent);
        p.Axes.AutoScale();
    }

    /// <summary>그룹(페어 묶음) 전체 누적 순매수.</summary>
    public static void Group(Plot p, GroupSeries g)
    {
        p.Clear();
        Style(p);
        var xs = g.Points.Select(x => DateTimeOffset.FromUnixTimeMilliseconds(x.Time).LocalDateTime.ToOADate()).ToArray();
        var ys = g.Points.Select(x => x.Cum).ToArray();
        NetArea(p, xs, ys, Theme.Accent);
        p.Axes.AutoScale();
    }

    /// <summary>작은 그래프: 누적 순매수(왼쪽, 영역) + 가격(오른쪽, 파란 선).</summary>
    public static void Mini(Plot p, Series s)
    {
        p.Clear();
        Style(p);
        var xs = X(s.Points);
        var ys = s.Points.Select(x => x.CumQuoteNet).ToArray();
        NetArea(p, xs, ys, ys.LastOrDefault() >= 0 ? Theme.Up : Theme.Down);
        var price = p.Add.Scatter(xs, s.Points.Select(x => x.Close).ToArray());
        price.Color = Theme.Price;
        price.LineWidth = 1.2f;
        price.MarkerSize = 0;
        price.Axes.YAxis = p.Axes.Right;
        p.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = Fmt.Price };
        p.Axes.Right.TickLabelStyle.FontSize = 10;
        p.Axes.Left.TickLabelStyle.FontSize = 10;
        p.Axes.Bottom.TickLabelStyle.FontSize = 10;
        p.Axes.AutoScale();
    }
}

/// <summary>여러 그래프의 x 축(기간)을 같이 움직이게 한다 (확대·이동 동기화).</summary>
public sealed class ChartLinker
{
    private readonly List<(Plot Plot, Action Refresh)> _plots = [];
    private bool _busy;

    public void Add(Plot plot, Action refresh)
    {
        _plots.Add((plot, refresh));
        plot.RenderManager.AxisLimitsChanged += (_, _) => Sync(plot);
    }

    private void Sync(Plot source)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var lim = source.Axes.GetLimits();
            foreach (var (plot, refresh) in _plots)
            {
                if (ReferenceEquals(plot, source)) continue;
                var other = plot.Axes.GetLimits();
                if (Math.Abs(other.Left - lim.Left) < 1e-9 && Math.Abs(other.Right - lim.Right) < 1e-9) continue;
                plot.Axes.SetLimitsX(lim.Left, lim.Right);
                refresh();
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
