using System.Windows;
using System.Windows.Threading;

namespace BinanceCumulative.Pc.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 예상 못 한 오류는 앱을 끄지 않고 알림
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BinanceCumulative", "error.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
            System.IO.File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n");
        }
        catch (Exception)
        {
            // 로그 실패는 무시
        }
        MessageBox.Show(e.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
