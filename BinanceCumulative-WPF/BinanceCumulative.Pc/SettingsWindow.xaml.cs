using System.Diagnostics;
using System.Windows;
using BinanceCumulative.Pc.Host;
using BinanceCumulative.Pc.Storage;
using Microsoft.Win32;

namespace BinanceCumulative.Pc.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly LocalDb _db;

    public SettingsWindow(AppSettings settings, LocalDb db)
    {
        InitializeComponent();
        _s = settings;
        _db = db;
        ServerUrl.Text = settings.ServerUrl;
        AutoStart.IsChecked = settings.AutoStartServer;
        DataFolder.Text = settings.DataFolder;
        Months.Text = settings.Months.ToString();
        Futures.IsChecked = settings.IncludeFutures;
        KeepRaw.IsChecked = settings.KeepRaw;
        CmcKey.Text = settings.CmcKey;
        JavaPath.Text = settings.JavaPath;
        // 일별 집계는 항상 자동 (설정 없음)
        DbPath.Text = db.FilePath;
        RefreshDbInfo();
    }

    private void RefreshDbInfo()
    {
        try
        {
            DbInfo.Text = $"크기 {_db.SizeBytes() / 1024.0 / 1024.0:0.0} MB · 일별 집계 {_db.DailySymbolCount:N0}페어 {_db.DailyRowCount:N0}일 (코인별 {_db.CoinDayCount:N0}일) · 그래프 저장본 {_db.SeriesCount:N0}개 · 로그: server.log, aggregate.log";
        }
        catch (Exception e)
        {
            DbInfo.Text = "DB 정보를 읽지 못했습니다: " + e.Message;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "1분봉 저장 폴더", InitialDirectory = DataFolder.Text };
        if (dlg.ShowDialog(this) == true) DataFolder.Text = dlg.FolderName;
    }

    private void OnOpenDbFolder(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.GetDirectoryName(_db.FilePath);
        if (dir != null && System.IO.Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "일별 집계 CSV 내보내기",
            FileName = $"binance-daily-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = _db.ExportDailyCsv(dlg.FileName);
            Error.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            Error.Text = $"{n:N0}줄을 내보냈습니다: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            Error.Foreground = (System.Windows.Media.Brush)FindResource("DownBrush");
            Error.Text = "내보내기 실패: " + ex.Message;
        }
    }

    private void OnExportCoinCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "코인별 합산 CSV 내보내기 (USDT)",
            FileName = $"binance-daily-coin-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = _db.ExportCoinCsv(dlg.FileName);
            Error.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            Error.Text = $"{n:N0}줄을 내보냈습니다: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            Error.Foreground = (System.Windows.Media.Brush)FindResource("DownBrush");
            Error.Text = "내보내기 실패: " + ex.Message;
        }
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "순위·그래프 저장본을 지울까요? (설정과 일별 집계는 남습니다)", "저장본 비우기",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _db.ClearCache();
            RefreshDbInfo();
        }
        catch (Exception ex)
        {
            Error.Text = "비우기 실패: " + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Error.Foreground = (System.Windows.Media.Brush)FindResource("DownBrush");
        if (!Uri.TryCreate(ServerUrl.Text.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        {
            Error.Text = "서버 주소는 http://localhost:8080 처럼 입력하세요.";
            return;
        }
        if (!int.TryParse(Months.Text.Trim(), out var months) || months < 1 || months > AppSettings.MaxMonths)
        {
            Error.Text = $"수집 기간은 1~{AppSettings.MaxMonths} 개월입니다.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DataFolder.Text))
        {
            Error.Text = "저장 폴더를 지정하세요.";
            return;
        }
        if (JavaPath.Text.Trim().Length > 0 && !System.IO.File.Exists(JavaPath.Text.Trim()))
        {
            Error.Text = "java.exe 위치에 파일이 없습니다.";
            return;
        }
        _s.ServerUrl = ServerUrl.Text.Trim().TrimEnd('/');
        _s.AutoStartServer = AutoStart.IsChecked == true;
        _s.DataFolder = DataFolder.Text.Trim();
        _s.Months = months;
        _s.IncludeFutures = Futures.IsChecked == true;
        _s.KeepRaw = KeepRaw.IsChecked == true;
        _s.CmcKey = CmcKey.Text.Trim();
        _s.JavaPath = JavaPath.Text.Trim();
        _s.AutoDailyAggregate = true;
        DialogResult = true;
    }
}
