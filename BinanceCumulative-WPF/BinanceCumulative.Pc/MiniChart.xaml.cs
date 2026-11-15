using System.Windows;
using System.Windows.Controls;
using BinanceCumulative.Pc.Charts;

namespace BinanceCumulative.Pc.App;

/// <summary>작은 그래프: 누적 순매수(영역) + 가격(파란 선, 오른쪽 눈금). 마우스 조작은 끔.</summary>
public partial class MiniChart : UserControl
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(BinanceCumulative.Pc.Models.Series), typeof(MiniChart),
        new PropertyMetadata(null, (d, _) => ((MiniChart)d).Redraw()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(MiniChart),
        new PropertyMetadata("", (d, _) => ((MiniChart)d).Redraw()));

    public BinanceCumulative.Pc.Models.Series? Series
    {
        get => (BinanceCumulative.Pc.Models.Series?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public MiniChart()
    {
        InitializeComponent();
        Chart.UserInputProcessor.Disable();
        Redraw();
    }

    private void Redraw()
    {
        Msg.Text = Message ?? "";
        Msg.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        if (Series is { Points.Count: > 1 } s) ChartBuilder.Mini(Chart.Plot, s);
        else ChartBuilder.Empty(Chart.Plot, "");
        Chart.Refresh();
    }
}
