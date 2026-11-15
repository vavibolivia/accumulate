using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BinanceCumulative.Pc.App;

/// <summary>부호(+1/0/-1) → 초록/기본/빨강 글자색.</summary>
public sealed class SignBrushConverter : IValueConverter
{
    public Brush Up { get; set; } = Brushes.LimeGreen;
    public Brush Down { get; set; } = Brushes.IndianRed;
    public Brush Zero { get; set; } = Brushes.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int s ? s > 0 ? Up : s < 0 ? Down : Zero : Zero;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>코인 이름 → 고정된 색 (아이콘 동그라미 바탕). 같은 이름은 항상 같은 색.</summary>
public sealed class CoinBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    [
        Make("#F0B90B"), Make("#7B93FF"), Make("#0ECB81"), Make("#F6465D"), Make("#B983FF"),
        Make("#29B6F6"), Make("#FF8A3D"), Make("#26C6DA"), Make("#EC6FA8"), Make("#9CCC65"),
    ];

    private static Brush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        return Palette[(int)((uint)h % Palette.Length)];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>코인 이름 → 아이콘 글자 (숫자로 시작하면 숫자 건너뜀: 1000PEPE → P).</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return s.Length == 0 ? "?" : s[..1];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>값이 있으면(빈 문자열 아님) 보이기. parameter="invert" 면 반대.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            string s => s.Length > 0,
            int n => n > 0,
            _ => value != null,
        };
        if (parameter is "invert" or "zero") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
