using System.Globalization;

namespace BinanceCumulative.Pc.Calc;

/// <summary>숫자 표시: 조/억/만 단위, 퍼센트, 가격.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Ko(decimal? v, bool signed = false) => v is { } d ? Ko((double)d, signed) : "-";

    public static string Ko(double v, bool signed = false)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "-";
        var a = Math.Abs(v);
        var sign = v < 0 ? "-" : signed && v > 0 ? "+" : "";
        string s = a switch
        {
            >= 1e12 => (a / 1e12).ToString("0.00", Inv) + "조",
            >= 1e8 => (a / 1e8).ToString("0.00", Inv) + "억",
            >= 1e4 => (a / 1e4).ToString("0.00", Inv) + "만",
            >= 100 => a.ToString("#,0.00", Inv),
            >= 1 => a.ToString("0.0000", Inv),
            0 => "0",
            _ => a.ToString("0.######", Inv) is var t && t != "0" ? t : a.ToString("G4", Inv),
        };
        return sign + s;
    }

    /// <summary>축 눈금용 짧은 표시 (예: 5000만, 1.2억).</summary>
    public static string Axis(double v)
    {
        if (v == 0) return "0";
        var a = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        string T(double x, string u) => sign + x.ToString(Math.Abs(x - Math.Round(x)) < 0.05 ? "0" : "0.#", Inv) + u;
        return a switch
        {
            // 그래프 라이브러리(ScottPlot)가 한글(억·만)을 □ 로 그려서 축은 K·M·B·T 로
            >= 1e12 => T(a / 1e12, "T"),
            >= 1e9 => T(a / 1e9, "B"),
            >= 1e6 => T(a / 1e6, "M"),
            >= 1e4 => T(a / 1e3, "K"),
            >= 1 => sign + a.ToString("0.##", Inv),
            _ => sign + a.ToString("G2", Inv),
        };
    }

    public static string Pct(decimal? v)
    {
        if (v is not { } d) return "-";
        var a = Math.Abs(d);
        var f = a >= 1 ? "0.00" : a >= 0.01m ? "0.0000" : "0.000000";
        return (d > 0 ? "+" : "") + d.ToString(f, Inv) + "%";
    }

    public static string Price(decimal? v) => v is { } d ? Price((double)d) : "-";

    /// <summary>가격: 1 미만은 유효숫자 4자리까지 (BTC 로 환산한 아주 작은 가격도 0 이 되지 않게)</summary>
    public static string Price(double v)
    {
        var a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 1000) return v.ToString("#,0.##", Inv);
        if (a >= 1) return v.ToString("#,0.####", Inv);
        var decimals = Math.Min(15, (int)Math.Ceiling(-Math.Log10(a)) + 3); // 첫 유효숫자 + 3자리
        return v.ToString("0." + new string('#', decimals), Inv);
    }

    public static string Time(long millis) =>
        DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime.ToString("MM-dd HH:mm", Inv);

    public static string Date(long millis) =>
        DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime.ToString("yyyy-MM-dd", Inv);
}
