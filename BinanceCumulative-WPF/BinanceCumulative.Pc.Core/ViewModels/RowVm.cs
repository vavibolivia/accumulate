using BinanceCumulative.Pc.Calc;
using CommunityToolkit.Mvvm.ComponentModel;
using BinanceCumulative.Pc.Models;

namespace BinanceCumulative.Pc.ViewModels;

/// <summary>순위 표 한 줄 (표시용 문자열 + 색 구분 부호). 코인별 합산 줄이면 페어 수를 함께 보여준다.</summary>
public sealed class RowVm(RankRow row, int rank)
{
    public RankRow Row { get; } = row;
    public string Id => Row.Id;
    public int Rank { get; } = rank;
    public string Base => Row.Coin.BaseAsset;
    /// <summary>페어 보기: "/USDT", 코인 보기: " ·3 페어"</summary>
    public string Quote => Row.IsCoin ? (Row.Pairs.Count == 1 ? $"/{Row.Coin.QuoteAsset}" : $"{Row.Pairs.Count}개 페어") : "/" + Row.Coin.QuoteAsset;
    public string PairsTip => Row.IsCoin ? $"{Base}: {Row.PairList} 페어를 {MainViewModel.Shown(Row.Unit)} 로 환산해 합산" : $"{Base}/{Row.Coin.QuoteAsset}";
    public string NetText => Fmt.Ko(Row.Quote.Net, signed: true);
    public int NetSign => Math.Sign(Row.Quote.Net);
    public string PctText => Fmt.Pct(Row.NetPctOfSupply);
    public int PctSign => Row.NetPctOfSupply is { } p ? Math.Sign(p) : 0;
    public string CoinsText => Fmt.Ko(Row.Base.Net, signed: true);
    public int CoinsSign => Math.Sign(Row.Base.Net);
    public string VolumeText => Fmt.Ko(Row.Quote.Amount);
    /// <summary>현재가: 고른 가격 단위(USDT·BTC)로. PriceUnitUsd = 그 단위 1개의 USDT 가격</summary>
    public string PriceText => Row.IsCoin
        ? Fmt.Price(Row.PriceUsd is { } p && PriceUnitUsd > 0 ? p / PriceUnitUsd : Row.PriceUsd)
        : Fmt.Price(Row.Coin.LastPrice);
    public decimal PriceUnitUsd { get; init; } = 1m;
    public string PriceUnitName { get; init; } = "USDT";
    public decimal NetValue => Row.Quote.Net;
    /// <summary>시작일 (바이낸스 첫 1분봉, 합산 줄은 가장 이른 페어)</summary>
    public string StartText { get; init; } = "-";
    /// <summary>수집률 (상장 월~이번 달 중 서버에 받아 둔 달의 비율)</summary>
    public string CollectText { get; init; } = "-";
    /// <summary>수집률 숫자 (0~100, 모르면 0) — 표 안 작은 막대용</summary>
    public double CollectValue => double.TryParse(CollectText.TrimEnd('%'), out var v) ? v : 0;
    /// <summary>수집이 끝나지 않은 줄이면 true (막대를 노란색으로)</summary>
    public bool CollectIncomplete => CollectValue < 100;
}

/// <summary>여러 페어를 한눈에: 작은 그래프 한 칸 (가격 + 누적 순매수).</summary>
public sealed partial class TileVm(RowVm row) : ObservableObject
{
    public RowVm Row { get; } = row;
    public string Title => $"{Row.Rank}. {Row.Base}{Row.Quote}";
    public string NetText => $"{Row.NetText} {MainViewModel.Shown(Row.Row.Unit)}";
    public int NetSign => Row.NetSign;

    [ObservableProperty] private Series? _series;
    [ObservableProperty] private string _message = "불러오는 중…";
}

public sealed record Option(string Key, string Label)
{
    public override string ToString() => Label;
}
