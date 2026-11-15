using System.Collections.ObjectModel;
using BinanceCumulative.Pc.Api;
using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BinanceCumulative.Pc.ViewModels;

/// <summary>
/// PC 대시보드 상태. UI 스레드에서 만들고 UI 스레드에서 호출한다
/// (await 뒤에도 UI 스레드로 돌아오므로 속성 변경이 안전).
/// 서버 응답은 로컬 DB(<see cref="LocalDb"/>)에 저장해 두고, 서버가 없으면 저장본을 보여준다.
/// 종목별 일별 집계(<see cref="DailyAggregator"/>)는 백그라운드로 돌려 DB 에 쌓는다.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const int GridSize = 12;
    /// <summary>일별 집계 시 서버에 동시에 보내는 요청 수 (화면 그래프 요청과 서버를 나눠 쓰므로 적게)</summary>
    public const int AggregateParallel = 2;
    /// <summary>서버 순위가 바뀌었는지 보는 간격 (바뀐 때만 다시 그림)</summary>
    public static readonly TimeSpan AutoRefreshEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheKeep = TimeSpan.FromDays(30);
    private const string MetaLastAggregate = "daily.lastRun";
    private const string MetaLastCoins = "daily.lastCoins";
    private const string MetaLastRows = "daily.lastRows";
    /// <summary>마지막으로 집계를 시작한 순위의 서버 계산 시각. 서버가 새 순위를 내면 그때마다 다시 집계한다</summary>
    private long _aggregatedRankingAt;
    private readonly PairInfo _pairs;
    private Dictionary<string, Dictionary<string, AllTime>> _allTime = new();

    private readonly ServerHost _host = new();
    private readonly LocalDb _db;
    private readonly bool _ownsDb;
    private ServerApi _api;
    private Ranking? _ranking;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _gridCts;
    private CancellationTokenSource? _aggCts;
    private readonly CancellationTokenSource _life = new();
    private bool _initialized;
    private string _tilesKey = "";
    /// <summary>코드에서 선택을 바꿀 때 연쇄 반응을 막는다</summary>
    private bool _suppress;

    public AppSettings Settings { get; }
    public LocalDb Db => _db;

    private readonly HttpMessageHandler? _handler;

    /// <param name="db">로컬 DB. 비우면 메모리 DB (테스트)</param>
    /// <param name="handler">테스트용 가짜 HTTP (보통 null)</param>
    public MainViewModel(AppSettings settings, LocalDb? db = null, HttpMessageHandler? handler = null)
    {
        Settings = settings;
        _db = db ?? new LocalDb(LocalDb.InMemory);
        _ownsDb = db == null;
        AppLog.Directory = _db.FilePath == LocalDb.InMemory ? null : Path.GetDirectoryName(_db.FilePath);
        _handler = handler;
        _api = new ServerApi(settings.ServerUrl, handler);
        _pairs = new PairInfo(_db, handler);
        _market = settings.Market;
        _group = settings.Group;
        _period = settings.Period;
        _method = Enum.TryParse<SplitMethod>(settings.Method, out var m) ? m : SplitMethod.TAKER;
        _displayUnit = string.IsNullOrWhiteSpace(settings.DisplayUnit) ? RankingCalc.UsdUnit : settings.DisplayUnit;
        _host.LogLine += line => ServerLogAppended?.Invoke(line);
        _lastAggregateText = DescribeLastAggregate();
    }

    /// <summary>서버 로그 새 줄 (다른 스레드에서 옴 — 화면에서 UI 스레드로 넘길 것)</summary>
    public event Action<string>? ServerLogAppended;

    public IReadOnlyList<string> RecentServerLog => _host.RecentLog();

    // ---- 선택 ----
    [ObservableProperty] private IReadOnlyList<Option> _markets = [];
    [ObservableProperty] private IReadOnlyList<Option> _groups = [];
    [ObservableProperty] private IReadOnlyList<Option> _periods = [];
    public IReadOnlyList<Option> Methods { get; } =
        Enum.GetValues<SplitMethod>().Select(m => new Option(m.ToString(), SplitMethodInfo.Label(m))).ToList();

    [ObservableProperty] private string _market;
    [ObservableProperty] private string _group;
    [ObservableProperty] private string _period;
    [ObservableProperty] private SplitMethod _method;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SortKey _sort = SortKey.Net;
    [ObservableProperty] private bool _descending = true;
    /// <summary>표·합계 금액 단위 (QUOTE = 호가 자산 그대로, USD, BTC …). 그래프는 호가 자산 그대로</summary>
    [ObservableProperty] private IReadOnlyList<Option> _units = [];
    [ObservableProperty] private string _displayUnit;

    partial void OnDisplayUnitChanged(string value)
    {
        if (_suppress || _ranking == null) return;
        Settings.DisplayUnit = value;
        _ = LoadBtcSeriesAsync();       // 가격 그래프만: 그 시점 BTC 가격으로 나눠 BTC 에 끌려간 움직임을 뺀다
    }

    /// <summary>가격 단위 선택지 이름 (금액은 언제나 USDT, 이 선택은 가격에만 적용)</summary>
    public static string UnitLabel(string unit) => unit switch
    {
        RankingCalc.QuoteUnit => "호가 자산 그대로",
        RankingCalc.UsdUnit => "USDT",
        _ => unit,
    };

    /// <summary>화면에 보이는 금액 단위 이름: 내부 "USD"(=USDT 환산) → "USDT"</summary>
    public static string Shown(string unit) => unit == RankingCalc.UsdUnit ? "USDT" : unit;

    /// <summary>가격을 BTC 로 볼 때 true</summary>
    public bool PriceInBtc => DisplayUnit == "BTC" && _ranking != null && RankingCalc.UnitUsdPrice(_ranking, "BTC") is > 0;

    /// <summary>표·지표의 현재가 단위 이름과 1 단위의 USDT 가격</summary>
    private static (string Name, decimal UsdPx) PriceUnit() => ("USDT", 1m); // 가격 단위(BTC) 선택은 가격 그래프에만 적용

    /// <summary>가격 그래프를 BTC 로 그릴 때 쓰는 BTCUSDT 그래프 (같은 기간·방식)</summary>
    [ObservableProperty] private Series? _btcSeries;
    private CancellationTokenSource? _btcCts;

    private async Task LoadBtcSeriesAsync()
    {
        _btcCts?.Cancel();
        var cts = _btcCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        if (!PriceInBtc || Selected?.Row.Coin is not { } c || c.QuoteAsset == "BTC")
        {
            BtcSeries = null;
            return;
        }
        if (Offline) return;
        try
        {
            var f = await _api.GetSeriesAsync(c.Market, "BTCUSDT", PeriodFrom(), Method, cts.Token);
            if (!cts.IsCancellationRequested) BtcSeries = f.Value;
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested) BtcSeries = null;
        }
    }

    /// <summary>
    /// 가격을 BTC 로: 각 시점 가격(USDT 환산)을 같은 시점 BTCUSDT 종가로 나눈다 — 지금 BTC 가격 하나로 나누면 추이가 틀어진다.
    /// 같은 시점 BTC 값이 없는 점은 뺀다. <paramref name="kUsd"/> 는 호가 자산 → USDT 배율 (USDT 페어면 1).
    /// </summary>
    public static Series PriceInBtcSeries(Series s, double kUsd, Series btc)
    {
        var byTime = btc.Points.Where(p => p.Close > 0).ToDictionary(p => p.Time, p => p.Close);
        return s with
        {
            Points = s.Points.Where(p => byTime.ContainsKey(p.Time)).Select(p =>
            {
                var b = byTime[p.Time];
                return p with { Close = p.Close * kUsd / b, Open = p.Open * kUsd / b, High = p.High * kUsd / b, Low = p.Low * kUsd / b };
            }).ToList(),
        };
    }

    /// <summary>항상 코인별 합산 (BNB/USDT + BNB/BTC + BNB/ETH … → BNB 한 줄). 페어별 보기는 쓰지 않는다</summary>
    public const bool ByCoin = true;

    partial void OnMarketChanged(string value) => SelectionChanged();
    partial void OnGroupChanged(string value) => SelectionChanged();
    partial void OnPeriodChanged(string value) => SelectionChanged();
    // (각 핸들러는 _suppress 중이면 SelectionChanged 안에서 무시된다)
    partial void OnMethodChanged(SplitMethod value)
    {
        OnPropertyChanged(nameof(MethodKey));
        OnPropertyChanged(nameof(MethodDescription));
        OnPropertyChanged(nameof(MethodIsEstimate));
        OnPropertyChanged(nameof(MethodWarning));
        SelectionChanged();
    }
    partial void OnQueryChanged(string value) => Recompute(reloadCharts: false);

    /// <summary>화면 선택 목록과 묶기 위한 문자열 키</summary>
    public string MethodKey
    {
        get => Method.ToString();
        set
        {
            if (Enum.TryParse<SplitMethod>(value, out var m)) Method = m;
        }
    }

    public string MethodDescription => SplitMethodInfo.Description(Method);
    /// <summary>A·B 는 가격에서 추정한 값 → 화면에 주의 표시</summary>
    public bool MethodIsEstimate => Method != SplitMethod.TAKER;

    public string MethodWarning => MethodIsEstimate
        ? $"⚠ {MethodDescription} — 가격이 오르면 순매수가 커지도록 정의되어 가격 그래프와 비슷하게 움직입니다. 가격과 독립된 정보는 C 체결입니다."
        : "";

    // ---- 표 ----
    [ObservableProperty] private IReadOnlyList<RowVm> _rows = [];
    [ObservableProperty] private RowVm? _selected;
    [ObservableProperty] private string _totalNet = "-";
    [ObservableProperty] private int _totalSign;
    [ObservableProperty] private string _totalCounts = "-";
    [ObservableProperty] private string _totalVolume = "-";
    [ObservableProperty] private string _unit = "";
    /// <summary>표 현재가 열 제목 ("현재가 (USDT)" / "현재가 (BTC)")</summary>
    [ObservableProperty] private string _priceHeader = "현재가 (USDT)";

    partial void OnSelectedChanged(RowVm? value)
    {
        if (_suppress) return;
        UpdateDetailHeader();
        _ = LoadDetailAsync();
    }

    // ---- 상세 ----
    [ObservableProperty] private string _detailTitle = "페어를 고르세요";
    [ObservableProperty] private string _detailSub = "";
    [ObservableProperty] private string _kpiNet = "-";
    [ObservableProperty] private int _kpiNetSign;
    [ObservableProperty] private string _kpiCoins = "-";
    [ObservableProperty] private int _kpiCoinsSign;
    [ObservableProperty] private string _kpiPct = "-";
    [ObservableProperty] private int _kpiPctSign;
    [ObservableProperty] private string _kpiBuy = "-";
    [ObservableProperty] private string _kpiSell = "-";
    [ObservableProperty] private string _kpiPrice = "-";
    [ObservableProperty] private Series? _detailSeries;
    [ObservableProperty] private string? _detailMessage = "페어를 고르세요";
    [ObservableProperty] private GroupSeries? _groupSeries;
    [ObservableProperty] private string? _groupMessage = "불러오는 중…";
    [ObservableProperty] private string _groupTitle = "";
    public ObservableCollection<TileVm> Tiles { get; } = [];

    // ---- 상태 ----
    [ObservableProperty] private string _status = "시작 중…";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _verifyNote = "";
    [ObservableProperty] private bool _needsJava;
    [ObservableProperty] private bool _busy;
    /// <summary>서버 응답이 없어 DB 저장본을 보여주는 중</summary>
    [ObservableProperty] private bool _offline;
    /// <summary>저장본을 쓰고 있을 때 안내 (언제 받은 것인지)</summary>
    [ObservableProperty] private string _cacheNote = "";
    /// <summary>창이 트레이로 숨겨진 상태: 그래프는 안 받고 순위·집계만 이어 간다</summary>
    [ObservableProperty] private bool _isHidden;

    partial void OnIsHiddenChanged(bool value)
    {
        if (value || _ranking == null) return;
        _tilesKey = "";
        _ = LoadDetailAsync();
    }

    // ---- 일별 집계 (백그라운드) ----
    [ObservableProperty] private bool _aggregating;
    [ObservableProperty] private string _aggregateStatus = "";
    /// <summary>전체 1분봉 수집 진행률 (거래 중 모든 페어 × 상장 월~이번 달)</summary>
    [ObservableProperty] private double _collectPercent;
    /// <summary>서버의 이번 최신화 회차 진행 (페어 단위)</summary>
    [ObservableProperty] private double _updatePercent;
    [ObservableProperty] private string _updateStatus = "서버 최신화 확인 중…";
    [ObservableProperty] private string _collectStatus = "1분봉 수집 진행률 계산 중…";
    /// <summary>진도율 0~100 (%)</summary>
    [ObservableProperty] private double _aggregatePercent;
    /// <summary>표를 가리는 "불러오는 중" 표시: 새로 받을 종목이 많을 때만 (평소의 짧은 갱신은 가리지 않음)</summary>
    [ObservableProperty] private bool _loadingVisible;
    public const int LoadingMinJobs = 60;
    [ObservableProperty] private string _lastAggregateText;

    public static double Percent(int done, int total) => total <= 0 ? 0 : Math.Round(100.0 * done / total, 1);

    /// <summary>이 앱이 켠 계산 서버가 돌고 있는지 (트레이 안내용)</summary>
    public bool ServerStartedHere => _host.IsRunning;

    /// <summary>앱 시작: 서버 확인(필요하면 직접 실행) → 순위 → 1분마다 새로고침 → 자동 일별 집계.</summary>
    public async Task InitializeAsync(string appDir)
    {
        if (_initialized) return;
        _initialized = true;
        try { _db.Prune(DateTimeOffset.UtcNow.Subtract(CacheKeep).ToUnixTimeMilliseconds()); } catch (Exception) { /* 무시 */ }
        ShowLastRankingWhileStarting(); // 서버가 뜨는 동안에도 표가 비지 않게
        if (await EnsureServerAsync(appDir)) await RefreshAsync();
        else ShowCachedRanking();
        _ = LoopAsync(AutoRefreshEvery, AutoRefreshAsync); // 페어가 최신화돼 순위가 바뀌면 바로 반영
        _ = LoopAsync(TimeSpan.FromSeconds(60), RefreshVerifyAsync);
        _ = LoopAsync(TimeSpan.FromMinutes(1), AutoAggregateTickAsync);
        _ = LoopAsync(TimeSpan.FromSeconds(30), UpdatePairInfoAsync); // 수집 진행률
        _ = UpdatePairInfoAsync();
        await RefreshVerifyAsync();
        // 시작 직후에도 1분 주기와 같은 조건으로 판단 — 주기 틱이 먼저 돌았으면 같은 집계를 두 번 하지 않는다
        await AutoAggregateTickAsync();
    }

    private async Task LoopAsync(TimeSpan every, Func<Task> work)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token)) await work();
        }
        catch (OperationCanceledException)
        {
            // 앱 종료
        }
    }

    /// <returns>서버와 연결되면 true</returns>
    private async Task<bool> EnsureServerAsync(string appDir)
    {
        Status = "서버 확인 중…";
        if (await _api.IsAliveAsync(_life.Token)) return true;
        if (!Settings.AutoStartServer || !Settings.IsLocalServer)
        {
            SetError($"서버({Settings.ServerUrl})에 연결할 수 없습니다. 서버가 켜져 있는지, 설정의 주소가 맞는지 확인하세요.");
            return false;
        }
        var jar = ServerHost.FindJar(appDir);
        if (jar == null)
        {
            SetError($"{ServerHost.JarName} 을 찾지 못했습니다. 앱 폴더에 함께 두세요.");
            return false;
        }
        var java = ServerHost.FindJava(Settings.JavaPath);
        if (java == null)
        {
            NeedsJava = true;
            SetError("Java 17 이상이 필요합니다. 설치한 뒤 다시 실행하세요.");
            return false;
        }
        Directory.CreateDirectory(Settings.DataFolder);
        _host.Start(java, jar, Settings.ServerArgs());
        Status = "계산 서버를 켜는 중…";
        for (var i = 0; i < 60 && !_life.IsCancellationRequested; i++)
        {
            if (await _api.IsAliveAsync(_life.Token)) return true;
            if (!_host.IsRunning)
            {
                SetError("계산 서버가 시작하자마자 멈췄습니다. 아래 서버 로그를 확인하세요.");
                return false;
            }
            await Task.Delay(1000, _life.Token);
        }
        SetError("계산 서버가 1분 안에 응답하지 않습니다. 아래 서버 로그를 확인하세요.");
        return false;
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(onlyIfChanged: false);

    /// <summary>자동 새로고침: 서버가 페어를 최신화해 새 순위를 냈을 때만 화면을 다시 그리고 일별 집계를 잇는다.</summary>
    private async Task AutoRefreshAsync()
    {
        await UpdateServerProgressAsync();
        await RefreshCoreAsync(onlyIfChanged: true);
    }

    /// <summary>서버 최신화 진행 ("1372개 중 243개 최신화 중") → 막대 %.</summary>
    private async Task UpdateServerProgressAsync()
    {
        var h = await _api.GetHealthAsync(_life.Token);
        if (h == null) return;
        var (pct, text) = ParseUpdateProgress(h);
        UpdatePercent = pct;
        UpdateStatus = text;
    }

    public static (double Percent, string Text) ParseUpdateProgress(string health)
    {
        var m = System.Text.RegularExpressions.Regex.Match(health, @"([\d,]+)개 중 ([\d,]+)개 최신화");
        if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var total) && int.TryParse(m.Groups[2].Value.Replace(",", ""), out var done) && total > 0)
            return (Percent(done, total), $"{done:N0}/{total:N0} 페어");
        var note = health.StartsWith("ok · ") ? health[5..] : health;
        var first = note.Split(" · ")[0];
        return (first.Contains("시작") ? 0 : 100, first);
    }

    private async Task RefreshCoreAsync(bool onlyIfChanged)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var f = await _api.GetRankingAsync(_life.Token);
            var r = f.Value;
            if (r.Coins.Count == 0 && _ranking is { Coins.Count: > 0 })
            {
                // 서버가 막 켜져 아직 순위를 다시 계산하기 전 — 빈 순위로 화면을 지우지 않고 이전 순위를 그대로 둔다
                Status = "서버 시작 중 · 이전 순위를 보여 주는 중";
                return;
            }
            var first = _ranking == null;
            var wasOffline = Offline;
            if (onlyIfChanged && !first && !wasOffline && r.GeneratedAt == _ranking!.GeneratedAt && r.Coins.Count == _ranking.Coins.Count)
                return; // 서버 순위가 그대로 — 다시 그릴 것 없음
            _ranking = RankingCalc.WithAllTime(r, _allTime);
            if (r.Coins.Count > 0) try { _db.PutRanking(f.Json); } catch (Exception) { /* 저장 실패는 화면에 영향 없음 */ }
            Offline = false;
            CacheNote = "";
            Status = $"서버 계산 {Fmt.Time(r.GeneratedAt)} · 순위 {r.Coins.Count:N0}개 페어";
            StatusIsError = false;
            RebuildOptions();
            Recompute(reloadCharts: first || wasOffline);
            _ = UpdatePairInfoAsync(); // 전체 기간 합계·시작일·수집률
            await AutoAggregateTickAsync(); // 새 순위 → 일별 집계도 바로 이어서
        }
        catch (ServerBusyException e)
        {
            Status = e.Message;
            StatusIsError = false;
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
        catch (HttpRequestException e)
        {
            SetError($"서버({Settings.ServerUrl})에 연결할 수 없습니다 — 서버가 켜져 있는지, 설정의 주소가 맞는지 확인하세요 ({e.Message})");
            if (_ranking == null) ShowCachedRanking();
            else
            {
                Offline = true;
                CacheNote = "서버 응답 없음 · 마지막으로 받은 순위와 저장된 그래프를 보여줍니다";
            }
        }
        catch (Exception e)
        {
            SetError("순위를 받지 못했습니다: " + e.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>서버가 없을 때: DB 에 저장된 마지막 순위로 화면을 채운다.</summary>
    /// <returns>저장본이 있어 보여주면 true</returns>
    /// <summary>앱 시작 직후: DB 에 저장된 마지막 순위(페어가 있을 때만)를 바로 보여 준다. 서버 순위가 오면 바뀐다.</summary>
    private void ShowLastRankingWhileStarting()
    {
        try
        {
            if (_db.GetRanking() is not { } c) return;
            var r = Ranking.Parse(c.Json);
            if (r.Coins.Count == 0) return;
            _ranking = RankingCalc.WithAllTime(r, _allTime);
            Status = $"서버 시작 중 · {Fmt.Time(c.FetchedAt)} 에 받은 순위를 보여 주는 중";
            RebuildOptions();
            Recompute(reloadCharts: true);
        }
        catch (Exception)
        {
            // 저장본이 없거나 깨졌으면 서버 순위를 기다린다
        }
    }

    private bool ShowCachedRanking()
    {
        CachedJson? c;
        try { c = _db.GetRanking(); } catch (Exception) { return false; }
        if (c == null) return false;
        Ranking r;
        try { r = Ranking.Parse(c.Json); } catch (Exception) { return false; }
        _ranking = RankingCalc.WithAllTime(r, _allTime);
        Offline = true;
        CacheNote = $"서버 응답 없음 · {Fmt.Time(c.FetchedAt)} 에 받아 둔 순위와 저장된 그래프를 보여줍니다";
        RebuildOptions();
        Recompute(reloadCharts: true);
        return true;
    }

    private async Task RefreshVerifyAsync()
    {
        if (Offline) return;
        try
        {
            VerifyNote = await _api.GetVerifyNoteAsync(_life.Token);
        }
        catch (Exception)
        {
            // 예전 서버 또는 아직 연결 전
        }
    }

    private void SetError(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    private void RebuildOptions()
    {
        var r = _ranking!;
        _suppress = true;
        try
        {
            // 목록을 바꾸면 콤보박스가 선택을 잠깐 비우므로, 바꾼 뒤 선택을 다시 알린다
            var market = Market;
            Markets = RankingCalc.Markets(r).Select(m => new Option(m, m == "SPOT" ? "현물" : "USDⓈ-M 선물")).ToList();
            Market = Markets.Any(o => o.Key == market) || Markets.Count == 0 ? market : Markets[0].Key;

            var group = Group;
            var groups = RankingCalc.QuoteGroups(r, Market);
            var total = groups.Sum(g => g.Count);
            Groups = new[] { new Option(RankingCalc.AllUsd, $"전체 {total:N0}개 (USD 환산·근사)") }
                .Concat(groups.Select(g => new Option(g.Quote, $"{g.Quote} 페어 {g.Count:N0}개"))).ToList();
            Group = RankingCalc.AllUsd; // 코인별 합산은 모든 호가 페어를 합치므로 그룹은 항상 전체
            _ = group;

            var period = Period;
            Periods = r.Periods.Select(p => new Option(p.Key, p.Label.Replace("최근 ", ""))).ToList();
            Period = Periods.Any(o => o.Key == period) || Periods.Count == 0 ? period : Periods[0].Key;

            var unit = DisplayUnit;
            Units = RankingCalc.UnitOptions(r).Select(u => new Option(u, UnitLabel(u))).ToList();
            DisplayUnit = Units.Any(o => o.Key == unit) ? unit : RankingCalc.UsdUnit;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SelectionChanged()
    {
        if (_ranking == null || _suppress) return;
        Settings.Market = Market;
        Settings.Group = Group;
        Settings.Period = Period;
        Settings.Method = Method.ToString();
        RebuildOptions();
        Recompute(reloadCharts: true);
    }

    /// <summary>표 머리글을 누르면: 같은 열이면 순서 뒤집기, 다른 열이면 그 열로 큰 순 (이름은 가나다순).</summary>
    public void SortBy(SortKey key)
    {
        if (Sort == key) Descending = !Descending;
        else
        {
            Sort = key;
            Descending = key != SortKey.Name;
        }
        Recompute(reloadCharts: false);
    }

    /// <summary>시작일: 페어(들) 중 가장 이른 바이낸스 첫 1분봉 날짜</summary>
    private string StartText(RankRow x)
    {
        var firsts = x.Pairs.Select(_pairs.ListedAt).Where(v => v != null).Select(v => v!.Value).ToList();
        return firsts.Count == 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(firsts.Min()).UtcDateTime.ToString("yyyy-MM-dd");
    }

    /// <summary>수집률: 상장 월부터 이번 달까지 서버 데이터 폴더에 있는 달의 비율 (합산 줄은 페어 합)</summary>
    private string CollectText(RankRow x)
    {
        var cs = x.Pairs.Select(_pairs.Collected).Where(v => v != null).Select(v => v!.Value).ToList();
        if (cs.Count == 0) return "-";
        var need = cs.Sum(v => v.Need);
        return need == 0 ? "-" : $"{100.0 * cs.Sum(v => v.Have) / need:0}%";
    }

    private int _pairInfoBusy;

    /// <summary>
    /// 시작일(모르는 페어만 바이낸스에서) · 수집률(데이터 폴더) · 전체 기간 합계(일별 집계 DB)를 새로 구해 표와 수집 진행률에 반영.
    /// 30초마다, 그리고 새 순위·집계 완료 때.
    /// </summary>
    private async Task UpdatePairInfoAsync()
    {
        if (Interlocked.Exchange(ref _pairInfoBusy, 1) == 1) return;
        try
        {
            var ranked = _ranking?.Coins ?? [];
            var folder = Settings.IsLocalServer ? Settings.DataFolder : null;
            var progress = await _pairs.UpdateAsync(ranked, folder, _life.Token);
            if (progress is { } p)
            {
                CollectPercent = p.Percent;
                CollectStatus = $"다 받은 페어 {p.DonePairs:N0}/{p.TotalPairs:N0} · 받은 달 {p.HaveMonths:N0}/{p.NeedMonths:N0}"
                                + (p.KnownPairs < p.TotalPairs ? $" (상장일 확인 {p.KnownPairs:N0}/{p.TotalPairs:N0})" : "");
            }
            else if (folder == null) CollectStatus = "1분봉 수집률: 원격 서버라 데이터 폴더를 볼 수 없음";
            _allTime = await Task.Run(() =>
            {
                try { return _db.GetAllTimeTotals(); } catch (Exception) { return _allTime; }
            });
            if (_ranking == null) return;
            _ranking = RankingCalc.WithAllTime(_ranking with { Periods = _ranking.Periods.Where(x => x.Key != RankingCalc.AllTimePeriod).ToList() }, _allTime);
            Recompute(reloadCharts: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 부가 정보 — 실패해도 표는 그대로 */ }
        finally { Interlocked.Exchange(ref _pairInfoBusy, 0); }
    }

    /// <summary>
    /// 그래프 페어의 호가 자산을 <paramref name="unit"/>(USD·BTC) 로 바꾸는 배율과 단위 (표와 같이 지금 가격 기준).
    /// 환산할 수 없으면 호가 자산 그대로(배율 1).
    /// </summary>
    public (double Scale, string Unit) ChartScale(string? unit = null)
    {
        var c = Selected?.Row.Coin;
        if (c == null || _ranking == null) return (1, c?.QuoteAsset ?? "");
        var eff = RankingCalc.EffectiveUnit(_ranking, RankingCalc.AllUsd, unit ?? DisplayUnit, byCoin: true);
        var unitPx = RankingCalc.UnitUsdPrice(_ranking, eff);
        if (c.UsdRate is not { } rate || unitPx is not > 0) return (1, c.QuoteAsset);
        return ((double)(rate / unitPx.Value), eff);
    }

    /// <summary>가격은 <paramref name="kPrice"/>, 금액 누적(순매수·거래대금)은 <paramref name="kAmount"/> 배. 코인 수는 그대로.</summary>
    public static Series Scale(Series s, double kPrice, double kAmount) => kPrice == 1 && kAmount == 1 ? s : s with
    {
        Points = s.Points.Select(p => p with
        {
            CumQuoteNet = p.CumQuoteNet * kAmount, CumQuoteAmount = p.CumQuoteAmount * kAmount,
            Close = p.Close * kPrice, Open = p.Open * kPrice, High = p.High * kPrice, Low = p.Low * kPrice,
        }).ToList(),
    };

    private void Recompute(bool reloadCharts)
    {
        if (_ranking == null) return;
        var keepId = Selected?.Id;
        var hadSeries = DetailSeries != null;
        // 금액은 언제나 USDT 환산. 가격 단위(USDT·BTC) 선택은 현재가에만 적용
        var excluded = new HashSet<string>(Settings.ExcludedCoins ?? [], StringComparer.OrdinalIgnoreCase);
        var rows = RankingCalc.Rows(_ranking, Market, Group, Period, Method, Sort, Descending, Query, RankingCalc.UsdUnit, ByCoin)
            .Where(x => !excluded.Contains(x.Coin.BaseAsset)) // BTC·USDT·WBTC 등은 표·합계에서 뺌
            .Where(x => !Settings.ExcludeStocks || !_pairs.IsStock(x.Coin.BaseAsset)).ToList(); // 주식 토큰(bStocks)도 뺌
        var (priceUnit, priceUsd) = PriceUnit();
        // 표(DataGrid)는 목록이 바뀌는 순간 선택을 null 로 되돌려 보낸다 → 그 연쇄(상세 비우기)를 막고 아래에서 선택을 다시 정한다
        RowVm? next;
        _suppress = true;
        try
        {
            Rows = rows.Select((x, i) => new RowVm(x, i + 1) { StartText = StartText(x), CollectText = CollectText(x), PriceUnitUsd = priceUsd, PriceUnitName = priceUnit }).ToList();
            // 고른 페어가 목록에 남아 있으면 유지, 없으면 1위
            next = Rows.FirstOrDefault(x => x.Id == keepId) ?? Rows.FirstOrDefault();
            Selected = next;
        }
        finally
        {
            _suppress = false;
        }
        var (net, vol, up, down) = RankingCalc.Totals(rows);
        Unit = Shown(RankingCalc.EffectiveUnit(_ranking, Group, RankingCalc.UsdUnit, ByCoin));
        PriceHeader = $"현재가 ({priceUnit})";
        TotalNet = $"{Fmt.Ko(net, true)} {Unit}";
        TotalSign = Math.Sign(net);
        TotalCounts = $"순매수 {up:N0} · 순매도 {down:N0} ({rows.Sum(x => x.Pairs.Count):N0} 페어)";
        TotalVolume = $"{Fmt.Ko(vol)} {Unit}";

        UpdateDetailHeader();
        // 선택이 바뀌었거나, 전체 다시 그리기이거나, 어떤 이유로든 상세 그래프가 비어 있으면 다시 불러온다
        if (next?.Id != keepId || reloadCharts || (next != null && (!hadSeries || DetailSeries == null))) _ = LoadDetailAsync();
    }

    private long PeriodFrom() =>
        _ranking?.Periods.FirstOrDefault(p => p.Key == Period)?.FromMillis ?? DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

    private void UpdateDetailHeader()
    {
        var x = Selected;
        if (x == null)
        {
            DetailTitle = "표시할 페어가 없습니다";
            DetailSub = "";
            KpiNet = KpiCoins = KpiPct = KpiBuy = KpiSell = KpiPrice = "-";
            return;
        }
        var c = x.Row.Coin;
        var coinRow = x.Row.IsCoin;
        DetailTitle = coinRow ? c.BaseAsset : $"{c.BaseAsset} / {c.QuoteAsset}";
        var sup = x.Row.Pairs.Select(p => p.Supply).FirstOrDefault(s => s?.Circulating is > 0) ?? c.Supply;
        // 데이터 시작: 상장일(바이낸스 첫 1분봉). 서버의 first 는 5년 제한이 걸린 값이라 쓰지 않는다
        var firstOpen = x.Row.Pairs.Select(_pairs.ListedAt).Where(f => f != null).DefaultIfEmpty(null).Min();
        DetailSub = string.Join(" · ", new[]
        {
            c.Market == "SPOT" ? "현물" : "USDⓈ-M 선물",
            coinRow ? $"{x.Row.Pairs.Count} 페어 합산 ({x.Row.PairList}) → {Shown(x.Row.Unit)}" : "",
            SplitMethodInfo.Label(Method),
            sup?.Circulating is { } circ ? $"유통량 {Fmt.Ko(circ)} ({sup.Name}, {sup.Source})" : "유통량 정보 없음",
            firstOpen is { } f ? $"상장 {Fmt.Date(f)}" : "",
            coinRow ? $"그래프: {c.BaseAsset}/{c.QuoteAsset} (거래대금 최대 페어)" : "",
        }.Where(s => s.Length > 0));
        KpiNet = $"{x.NetText} {Shown(x.Row.Unit)}";
        KpiNetSign = x.NetSign;
        KpiCoins = $"{x.CoinsText} {c.BaseAsset}";
        KpiCoinsSign = x.CoinsSign;
        KpiPct = x.PctText;
        KpiPctSign = x.PctSign;
        KpiBuy = $"{Fmt.Ko(x.Row.Quote.Buy)} {Shown(x.Row.Unit)}";
        KpiSell = $"{Fmt.Ko(x.Row.Quote.Sell)} {Shown(x.Row.Unit)}";
        KpiPrice = $"{x.PriceText} {x.PriceUnitName}";
    }

    // ---- 그래프: DB 저장본 먼저 → 서버에서 새로 받아 바꾸고 저장 ----

    private Series? CachedSeries(RankingCoin c, out CachedJson? cached)
    {
        cached = null;
        try
        {
            cached = _db.GetSeries(c.Market, c.Symbol, Period, Method.ToString());
            return cached == null ? null : Series.Parse(cached.Json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task LoadDetailAsync()
    {
        _detailCts?.Cancel();
        var cts = _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var x = Selected;
        if (x == null)
        {
            DetailSeries = null;
            DetailMessage = "표시할 페어가 없습니다";
            return;
        }
        var c = x.Row.Coin;
        _ = LoadBtcSeriesAsync();
        var cachedSeries = CachedSeries(c, out var cached);
        if (cachedSeries != null)
        {
            DetailSeries = cachedSeries;
            DetailMessage = cachedSeries.Points.Count == 0 ? "이 기간의 저장된 1분봉이 없습니다" : null;
        }
        else
        {
            DetailSeries = null;
            DetailMessage = Offline ? "서버 응답 없음 · 저장된 그래프가 없습니다" : "그래프 불러오는 중…";
        }
        if (Offline || IsHidden) return;
        try
        {
            var f = await _api.GetSeriesAsync(c.Market, c.Symbol, PeriodFrom(), Method, cts.Token);
            if (cts.IsCancellationRequested) return;
            DetailSeries = f.Value;
            DetailMessage = f.Value.Points.Count == 0 ? "이 기간의 저장된 1분봉이 없습니다" : null;
            try { _db.PutSeries(c.Market, c.Symbol, Period, Method.ToString(), PeriodFrom(), f.Json); } catch (Exception) { /* 무시 */ }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            if (cachedSeries == null)
            {
                DetailSeries = null;
                DetailMessage = "그래프를 불러오지 못했습니다: " + e.Message;
            }
            else CacheNote = $"그래프: {Fmt.Time(cached!.FetchedAt)} 저장본 표시 (서버 응답 없음: {e.Message})";
        }
    }

    private async Task LoadGroupAsync()
    {
        var label = Group == RankingCalc.AllUsd ? "모든 페어 · USD 환산" : $"{Group} 페어 · {Group}";
        GroupTitle = $"그룹 전체 누적 순매수 ({(Market == "SPOT" ? "현물" : "선물")} · {label})";
        var market = Market;
        var group = Group;
        var period = Period;
        var method = Method.ToString();
        GroupSeries? cachedGroup = null;
        try
        {
            if (_db.GetGroup(market, group, period, method) is { } cg) cachedGroup = Models.GroupSeries.Parse(cg.Json);
        }
        catch (Exception)
        {
            // 저장본 손상 → 서버에서
        }
        if (cachedGroup != null)
        {
            GroupSeries = cachedGroup;
            GroupMessage = cachedGroup.Points.Count == 0 ? "그룹 그래프는 서버의 첫 수집이 끝나면 나타납니다" : null;
        }
        else if (Offline)
        {
            GroupSeries = null;
            GroupMessage = "서버 응답 없음 · 저장된 그룹 그래프가 없습니다";
        }
        if (Offline || IsHidden) return;
        try
        {
            var f = await _api.GetGroupSeriesAsync(market, group, PeriodFrom(), Method, _life.Token);
            if (market != Market || group != Group || period != Period || method != Method.ToString()) return; // 그 사이 선택이 바뀜
            GroupSeries = f.Value;
            GroupMessage = f.Value.Points.Count == 0 ? "그룹 그래프는 서버의 첫 수집이 끝나면 나타납니다" : null;
            try { _db.PutGroup(market, group, period, method, PeriodFrom(), f.Json); } catch (Exception) { /* 무시 */ }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (cachedGroup != null) return;
            GroupSeries = null;
            GroupMessage = "그룹 그래프를 불러오지 못했습니다: " + e.Message;
        }
    }

    /// <summary>표 위쪽 페어들의 작은 그래프 (가격 + 누적 순매수).</summary>
    private async Task LoadTilesAsync()
    {
        _gridCts?.Cancel();
        var cts = _gridCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var top = Rows.Take(GridSize).ToList();
        var key = $"{Market}|{Group}|{Period}|{Method}|{DisplayUnit}|{Offline}|{string.Join(',', top.Select(r => r.Id))}";
        if (key == _tilesKey && Tiles.All(t => t.Series != null)) return;
        _tilesKey = key;
        Tiles.Clear();
        foreach (var r in top)
        {
            var t = new TileVm(r);
            if (CachedSeries(r.Row.Coin, out _) is { } s)
            {
                t.Series = s;
                t.Message = s.Points.Count == 0 ? "데이터 없음" : "";
            }
            else if (Offline) t.Message = "서버 응답 없음";
            Tiles.Add(t);
        }
        if (Offline || IsHidden) return;
        var from = PeriodFrom();
        var method = Method;
        var period = Period;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(Tiles.ToList().Select(async t =>
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(true);
            try
            {
                var c = t.Row.Row.Coin;
                var f = await _api.GetSeriesAsync(c.Market, c.Symbol, from, method, cts.Token);
                if (cts.IsCancellationRequested) return;
                t.Series = f.Value;
                t.Message = f.Value.Points.Count == 0 ? "데이터 없음" : "";
                try { _db.PutSeries(c.Market, c.Symbol, period, method.ToString(), from, f.Json); } catch (Exception) { /* 무시 */ }
            }
            catch (Exception e) when (!cts.IsCancellationRequested)
            {
                if (t.Series == null) t.Message = "불러오기 실패: " + e.Message;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        })).ContinueWith(_ => { }, TaskScheduler.Current);
    }

    // ---- 종목별 일별 집계 → DB ----

    /// <summary>
    /// 순위에 있는 모든 종목(모든 시장) × 세 분할 방식을, 저장된 가장 예전 날부터 오늘까지 하루 단위로 DB 에 쌓는다.
    /// 이미 돌고 있으면 무시. 서버와 함께 백그라운드에서 돌아가고, 다 끝나면 6시간 뒤 다시 이어서 돈다.
    /// </summary>
    public void StartDailyAggregate()
    {
        if (Aggregating || _ranking == null || Offline) return;
        var coins = _ranking.Coins;
        if (coins.Count == 0) return; // 서버가 막 켜져 순위가 비어 있음 — 종목이 생기면 자동 집계가 곧 시작한다
        var cts = _aggCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        Aggregating = true;
        AggregatePercent = 0;
        AggregateStatus = $"준비 중 · {coins.Count:N0}개 종목 × 3 방식";
        _ = RunAggregateAsync(coins, cts);
    }

    public void StopDailyAggregate()
    {
        _aggCts?.Cancel();
    }

    private async Task RunAggregateAsync(IReadOnlyList<RankingCoin> coins, CancellationTokenSource cts)
    {
        var agg = new DailyAggregator(_api, _db);
        var sync = SynchronizationContext.Current;
        string? lastErr = null;
        var started = DateTimeOffset.UtcNow;
        AppLog.Write("aggregate.log", $"시작: {coins.Count}개 종목 × 3 방식, 서버 {Settings.ServerUrl}");
        void Post(Action a)
        {
            if (sync != null) sync.Post(_ => a(), null);
            else a();
        }
        agg.Progress += p => Post(() =>
        {
            if (p.Error != null && p.Error != lastErr)
            {
                lastErr = p.Error;
                AppLog.Write("aggregate.log", $"오류 ({p.Done}/{p.Total}): {p.Error}");
            }
            if (p.Finished) return;
            LoadingVisible = p.Total >= LoadingMinJobs;
            AggregatePercent = Percent(p.Done, p.Total);
            AggregateStatus = $"{p.Done:N0}/{p.Total:N0} · {p.Current} · 저장 {p.RowsWritten:N0}일"
                              + (p.Error != null ? $" · 오류: {p.Error}" : "");
        });
        var methods = Enum.GetValues<SplitMethod>();
        try
        {
            var rows = await agg.RunAsync(coins, methods, AggregateParallel, cts.Token);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                _db.SetMeta(MetaLastAggregate, now.ToString());
                _db.SetMeta(MetaLastCoins, coins.Count.ToString());
                _db.SetMeta(MetaLastRows, rows.ToString());
            }
            catch (Exception) { /* 무시 */ }
            LastAggregateText = DescribeLastAggregate();
            AggregatePercent = 100;
            _ = UpdatePairInfoAsync(); // 전체 기간 합계 갱신
            AggregateStatus = $"완료 {Fmt.Time(now)} · 새로 저장 {rows:N0}일 · DB {_db.DailySymbolCount:N0}페어 {_db.DailyRowCount:N0}일"
                              + (lastErr != null ? $" · 일부 오류: {lastErr}" : "");
            AppLog.Write("aggregate.log", $"완료: {rows:N0}일 저장(환율 보충 {agg.LastBackfilled:N0}일 포함, 건너뜀 {agg.LastSkipped:N0}), {(DateTimeOffset.UtcNow - started).TotalSeconds:0}초, DB {_db.DailySymbolCount}페어 {_db.DailyRowCount}일" + (lastErr != null ? $", 일부 오류: {lastErr}" : ""));
        }
        catch (OperationCanceledException)
        {
            AggregateStatus = _life.IsCancellationRequested ? "" : "중지됨 (다음에 이어서 진행)";
            AppLog.Write("aggregate.log", "중지됨");
        }
        catch (Exception e)
        {
            AggregateStatus = "실패: " + e.Message;
            AppLog.Write("aggregate.log", "실패: " + e);
        }
        finally
        {
            Aggregating = false;
            LoadingVisible = false;
            if (_aggCts == cts) _aggCts = null;
            cts.Dispose();
        }
    }

    private string DescribeLastAggregate()
    {
        try
        {
            if (_db.GetMeta(MetaLastAggregate) is { } s && long.TryParse(s, out var ms))
                return $"마지막 일별 집계 {Fmt.Time(ms)} · DB {_db.DailySymbolCount:N0}종목 {_db.DailyRowCount:N0}일";
        }
        catch (Exception)
        {
            // 무시
        }
        return "일별 집계 기록 없음";
    }

    /// <summary>
    /// 1분마다: 서버가 새 순위를 냈으면(계산 시각이 바뀜) 그 순위의 모든 종목으로 일별 집계를 이어서 돈다.
    /// 이미 저장된 마감일은 건너뛰므로 매번 돌아도 새 날·새 페어만 쌓인다.
    /// </summary>
    private Task AutoAggregateTickAsync()
    {
        if (!Settings.AutoDailyAggregate || Aggregating || Offline || _ranking is not { Coins.Count: > 0 } r) return Task.CompletedTask;
        if (r.GeneratedAt == _aggregatedRankingAt) return Task.CompletedTask;
        _aggregatedRankingAt = r.GeneratedAt;
        StartDailyAggregate();
        return Task.CompletedTask;
    }

    /// <summary>설정 저장 후 다시 연결 (서버 주소·옵션이 바뀌면 서버 재시작).</summary>
    public async Task ApplySettingsAsync(string appDir, bool restartServer)
    {
        Settings.Save(_db);
        _api.Dispose();
        _api = new ServerApi(Settings.ServerUrl, _handler);
        if (restartServer)
        {
            StopDailyAggregate();
            _host.Stop();
        }
        NeedsJava = false;
        if (await EnsureServerAsync(appDir)) await RefreshAsync();
    }

    /// <summary>창을 닫을 때 설정을 저장할지 (테스트는 false)</summary>
    public bool SaveOnDispose { get; set; } = true;

    public void Dispose()
    {
        _life.Cancel();
        if (SaveOnDispose)
        {
            try { Settings.Save(_db); } catch (Exception) { /* 무시 */ }
        }
        _host.Dispose();
        _api.Dispose();
        if (_ownsDb) _db.Dispose();
    }
}
