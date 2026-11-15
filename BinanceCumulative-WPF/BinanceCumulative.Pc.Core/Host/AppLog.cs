namespace BinanceCumulative.Pc.Host;

/// <summary>
/// 간단한 파일 로그 (%AppData%\BinanceCumulative\server.log · aggregate.log). 앱이 꺼져도 남아 무인 운영 점검에 쓴다.
/// <see cref="Directory"/> 가 null 이면 끔 (테스트). 5MB 넘으면 .1 로 밀어낸다.
/// </summary>
public static class AppLog
{
    public static string? Directory { get; set; }
    private static readonly object Lock = new();

    public static void Write(string file, string line)
    {
        var dir = Directory;
        if (dir == null) return;
        try
        {
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, file);
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 로그 실패는 무시
        }
    }
}
