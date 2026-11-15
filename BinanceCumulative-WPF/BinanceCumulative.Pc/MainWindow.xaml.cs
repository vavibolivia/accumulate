using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BinanceCumulative.Pc.Calc;
using BinanceCumulative.Pc.Charts;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Models;
using BinanceCumulative.Pc.Storage;
using BinanceCumulative.Pc.ViewModels;
using ScottPlot.WPF;
using WF = System.Windows.Forms;

namespace BinanceCumulative.Pc.App;

/// <summary>
/// 전체화면 대시보드. 상태는 <see cref="MainViewModel"/> 에 있고, 여기서는 그래프 그리기·키보드·창 모드·트레이만 다룬다.
/// "백그라운드 집계": 창을 숨기고 트레이 아이콘만 남긴다. 계산 서버와 일별 집계는 그대로 돌아간다.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LocalDb _db;
    private readonly MainViewModel _vm;
    private readonly ChartCursor _cursor = new();
    private readonly ChartLinker _linker = new();
    private bool _fullscreen;
    private WF.NotifyIcon? _tray;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        ChartBuilder.FontName = "Malgun Gothic";
        _db = new LocalDb();
        _vm = new MainViewModel(AppSettings.Load(_db), _db);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmChanged;
        _vm.ServerLogAppended += line => Dispatcher.InvokeAsync(() => AppendLog(line));

        foreach (var p in DetailPlots)
        {
            _linker.Add(p.Plot, p.Refresh);
            p.MouseMove += OnPlotMouseMove;
            p.MouseLeave += OnPlotMouseLeave;
            ChartBuilder.Empty(p.Plot, "");
            Align(p.Plot);
            p.Refresh();
        }

        Loaded += async (_, _) =>
        {
            SetFullscreen(true);
            foreach (var line in _vm.RecentServerLog) AppendLog(line);
            await _vm.InitializeAsync(AppContext.BaseDirectory);
        };
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 제목 표시줄을 앱과 같은 어두운 색으로 (Windows 10 20H1+ / 11, 실패해도 무시)</summary>
    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = 0x11 << 16 | 0x0D << 8 | 0x0A;                    // #0A0D11 를 COLORREF(0x00BBGGRR) 로
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR (Windows 11)
        }
        catch (Exception)
        {
            // 지원 안 하는 Windows — 기본 제목 표시줄
        }
    }

    private IEnumerable<WpfPlot> DetailPlots => [PricePlot, NetPlot, CoinPlot, GroupPlot];

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.DetailSeries):
                DrawDetail();
                break;
            case nameof(MainViewModel.GroupSeries):
                DrawGroup();
                break;
            case nameof(MainViewModel.Rows):
                UpdateSortGlyphs();
                break;
            case nameof(MainViewModel.BtcSeries):
            case nameof(MainViewModel.DisplayUnit):
                DrawDetail(); // 가격 단위(USDT·BTC)가 바뀌면 가격 그래프도
                break;
            case nameof(MainViewModel.PriceHeader):
                foreach (var col in Table.Columns)
                    if (col.SortMemberPath == "Price") col.Header = _vm.PriceHeader;
                break;
            case nameof(MainViewModel.AggregateStatus):
            case nameof(MainViewModel.Status):
                UpdateTrayText();
                break;
        }
    }

    // ---- 그래프 ----
    private Series? _drawn; // 그린 데이터 (금액 기준으로 환산된 것) — 마우스 위치 값 표시용

    private void DrawDetail()
    {
        // 누적 순매수·거래대금은 항상 USDT. 가격만 고른 단위(USDT·BTC) — BTC 는 그 시점 BTC 가격으로 나눔
        var c = _vm.Selected?.Row.Coin;
        var coin = c?.BaseAsset ?? "";
        var (kUsd, usdName) = _vm.ChartScale(RankingCalc.UsdUnit);
        var netUnit = MainViewModel.Shown(usdName);
        var raw = _vm.DetailSeries;
        var s = _drawn = raw is null ? null : MainViewModel.Scale(raw, kUsd, kUsd);
        Series? price = s;
        var quote = netUnit;
        if (raw != null && c != null && _vm.PriceInBtc)
        {
            if (c.QuoteAsset == "BTC") { price = raw; quote = "BTC"; }
            else if (_vm.BtcSeries is { } btc) { price = MainViewModel.PriceInBtcSeries(raw, kUsd, btc); quote = "BTC"; }
            else quote = $"{netUnit} · BTC 가격 불러오는 중";
        }
        PriceUnit.Text = quote.Length > 0 ? $"  ({quote} · 캔들, 구간이 많으면 종가 선)" : "";
        NetUnit.Text = netUnit.Length > 0 ? $"  ({netUnit})" : "";
        CoinUnit.Text = coin.Length > 0 ? $"  ({coin})" : "";
        if (s == null || s.Points.Count == 0)
        {
            ChartBuilder.Empty(PricePlot.Plot, "");
            ChartBuilder.Empty(NetPlot.Plot, "");
            ChartBuilder.Empty(CoinPlot.Plot, "");
        }
        else
        {
            if (price is { Points.Count: > 0 }) ChartBuilder.Price(PricePlot.Plot, price, quote);
            else ChartBuilder.Empty(PricePlot.Plot, "");
            ChartBuilder.NetQuote(NetPlot.Plot, s);
            ChartBuilder.NetCoins(CoinPlot.Plot, s);
        }
        foreach (var p in new[] { PricePlot, NetPlot, CoinPlot })
        {
            Align(p.Plot);
            _cursor.Attach(p.Plot);
            p.Refresh();
        }
    }

    /// <summary>세로로 늘어선 그래프들의 왼쪽(값 축) 폭을 고정해 그림 영역 왼쪽 끝을 맞춘다.</summary>
    private static void Align(ScottPlot.Plot plot) => plot.Layout.Fixed(new ScottPlot.PixelPadding(left: 92, right: 14, bottom: 28, top: 6));

    private void DrawGroup()
    {
        var g = _vm.GroupSeries;
        if (g == null || g.Points.Count == 0) ChartBuilder.Empty(GroupPlot.Plot, "");
        else ChartBuilder.Group(GroupPlot.Plot, g);
        Align(GroupPlot.Plot);
        _cursor.Attach(GroupPlot.Plot);
        GroupPlot.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not WpfPlot wp) return;
        var px = wp.GetPlotPixelPosition(e);
        var x = ChartCursor.XAt(wp.Plot, px.X, px.Y);
        _cursor.Show(x);
        foreach (var p in DetailPlots) p.Refresh();
        var coin = _vm.Selected?.Row.Coin.BaseAsset ?? "";
        if (x is { } v && ChartCursor.Describe(_drawn, v, "USDT", coin) is { } d) CursorText.Text = d.Text;
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e)
    {
        _cursor.Show(null);
        foreach (var p in DetailPlots) p.Refresh();
    }

    // ---- 표 ----
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // 정렬은 ViewModel 이 (값 없는 행은 항상 뒤)
        if (Enum.TryParse<SortKey>(e.Column.SortMemberPath, out var key)) _vm.SortBy(key);
        UpdateSortGlyphs();
    }

    private void UpdateSortGlyphs()
    {
        foreach (var c in Table.Columns)
        {
            c.SortDirection = c.SortMemberPath == _vm.Sort.ToString()
                ? _vm.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Table.SelectedItem != null) Table.ScrollIntoView(Table.SelectedItem);
    }

    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TileVm t) return;
        _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Id == t.Row.Id) ?? _vm.Selected;
        TabDetail.IsChecked = true;
    }

    private void OnGridTabChecked(object sender, RoutedEventArgs e)
    {
        // 작은 그래프들은 표가 바뀔 때 이미 불러와 둔다
    }

    // ---- 일별 집계 · 백그라운드 ----

    /// <summary>창만 트레이로 숨긴다 (선택). 서버·집계는 계속.</summary>
    private void OnHideToTray(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        _tray ??= CreateTray();
        _tray.Visible = true;
        _vm.IsHidden = true;
        Hide();
        UpdateTrayText();
        _tray.ShowBalloonTip(4000, "트레이에서 계속 실행 중",
            _vm.ServerStartedHere
                ? "계산 서버와 일별 집계가 계속 돌아갑니다. 아이콘을 두 번 누르면 창이 다시 열리고, 완전히 끄려면 오른쪽 메뉴에서 종료."
                : "일별 집계가 계속 돌아갑니다. 아이콘을 두 번 누르면 창이 다시 열립니다.",
            WF.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        if (_tray != null) _tray.Visible = false;
        Show();
        SetFullscreen(_fullscreen);
        Activate();
        _vm.IsHidden = false;
    }

    private WF.NotifyIcon CreateTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("종료 (서버·집계도 끔)", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _exitRequested = true;
            Close();
        }));
        var icon = new WF.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "바이낸스 누적 순매수",
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return icon;
    }

    private void UpdateTrayText()
    {
        if (_tray is not { Visible: true }) return;
        var text = _vm.Aggregating ? _vm.AggregateStatus : "일별 집계 대기 · " + _vm.Status;
        if (text.Length > 60) text = text[..60] + "…"; // NotifyIcon.Text 는 63자까지
        _tray.Text = text;
    }

    /// <summary>초록/빨강 두 칸 아이콘 (앱 표시와 같은 색).</summary>
    private static System.Drawing.Icon MakeTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            using var up = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2E, 0xBD, 0x85));
            using var down = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF6, 0x46, 0x5D));
            g.FillRectangle(up, 2, 6, 13, 24);
            g.FillRectangle(down, 17, 2, 13, 28);
        }
        var h = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // ---- 창 ----
    private void SetFullscreen(bool on)
    {
        _fullscreen = on;
        if (on)
        {
            WindowState = WindowState.Normal; // 작업 표시줄까지 덮으려면 테두리를 없앤 뒤 최대화
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Maximized;
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 집계가 돌고 있는데 닫으면: 백그라운드로 계속할지 묻는다 (트레이 메뉴의 종료는 바로 끔)
        if (!_exitRequested && _vm.Aggregating && IsVisible)
        {
            var r = MessageBox.Show(this,
                "종목별 일별 집계가 진행 중입니다.\n\n[예] 창만 숨기고 백그라운드에서 계속\n[아니요] 집계와 서버를 끄고 종료",
                "백그라운드 집계", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) { e.Cancel = true; HideToTray(); return; }
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _vm.Dispose();
        _db.Dispose();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var inText = Keyboard.FocusedElement is TextBox;
        switch (e.Key)
        {
            case Key.F11:
                SetFullscreen(!_fullscreen);
                e.Handled = true;
                break;
            case Key.Escape when _fullscreen && !inText:
                SetFullscreen(false);
                e.Handled = true;
                break;
            case Key.Escape when inText:
                Table.Focus();
                e.Handled = true;
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.B when Keyboard.Modifiers == ModifierKeys.Control:
                HideToTray();
                e.Handled = true;
                break;
            case Key.F5:
                _vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up or Key.Down when !inText && !Table.IsKeyboardFocusWithin:
                var i = _vm.Selected == null ? -1 : _vm.Rows.ToList().IndexOf(_vm.Selected);
                var next = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, _vm.Rows.Count - 1));
                if (_vm.Rows.Count > 0) _vm.Selected = _vm.Rows[next];
                e.Handled = true;
                break;
        }
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var before = string.Join(' ', _vm.Settings.ServerArgs()) + _vm.Settings.ServerUrl + _vm.Settings.JavaPath + _vm.Settings.AutoStartServer;
        var dlg = new SettingsWindow(_vm.Settings, _db) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var after = string.Join(' ', _vm.Settings.ServerArgs()) + _vm.Settings.ServerUrl + _vm.Settings.JavaPath + _vm.Settings.AutoStartServer;
        await _vm.ApplySettingsAsync(AppContext.BaseDirectory, restartServer: before != after);
    }

    private void OnOpenJavaPage(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://adoptium.net/temurin/releases/?version=21") { UseShellExecute = true });

    // ---- 서버 로그 ----
    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        var show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "서버 로그 닫기 ▾" : "서버 로그 보기 ▴";
        if (show) LogBox.ScrollToEnd();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[^100_000..];
        if (LogBox.Visibility == Visibility.Visible) LogBox.ScrollToEnd();
    }
}
