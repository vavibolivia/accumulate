using System.Diagnostics;

namespace BinanceCumulative.Pc.Host;

/// <summary>
/// 계산 서버(binance-cumulative-server.jar)를 창 없이 실행·종료하고 로그를 모은다.
/// 이 앱이 켠 서버만 이 앱이 끈다.
/// </summary>
public sealed class ServerHost : IDisposable
{
    private Process? _process;
    private readonly object _logLock = new();
    private readonly LinkedList<string> _log = new();

    public const string JarName = "binance-cumulative-server.jar";

    /// <summary>새 로그 줄 (다른 스레드에서 올 수 있음)</summary>
    public event Action<string>? LogLine;
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public IReadOnlyList<string> RecentLog()
    {
        lock (_logLock) return _log.ToList();
    }

    /// <summary>서버 jar 가 요구하는 최소 Java 메이저 버전</summary>
    public const int MinJavaMajor = 17;

    /// <summary>
    /// java 실행 파일 찾기: 설정 → JAVA_HOME → PATH → 흔한 설치 위치. 버전이 17 미만인 것(예: PATH 의 Java 8)은 건너뛴다.
    /// </summary>
    public static string? FindJava(string? configured = null)
    {
        var candidates = JavaCandidates(configured).Where(p => p != null && File.Exists(p)).Distinct().ToList();
        // 사용자가 지정한 경로는 버전과 상관없이 그대로 (오류는 서버 로그에 나타난다)
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return candidates.FirstOrDefault(p => JavaMajor(p!) >= MinJavaMajor);
    }

    /// <summary>java 메이저 버전 (1.8.0_231 → 8, 17.0.12 → 17). 알 수 없으면 0.</summary>
    public static int JavaMajor(string exe)
    {
        try
        {
            // JDK 폴더의 release 파일이 가장 빠르다: JAVA_VERSION="17.0.12"
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe));
            var release = home == null ? null : Path.Combine(home, "release");
            if (release != null && File.Exists(release))
            {
                var line = File.ReadLines(release).FirstOrDefault(l => l.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
                if (line != null && ParseJavaMajor(line[(line.IndexOf('=') + 1)..].Trim('"')) is var m and > 0) return m;
            }
            // 없으면 (javapath 런처 등) 실행해서 본다: java version "1.8.0_231"
            var psi = new ProcessStartInfo(exe, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var q1 = text.IndexOf('"');
            var q2 = q1 < 0 ? -1 : text.IndexOf('"', q1 + 1);
            return q1 >= 0 && q2 > q1 ? ParseJavaMajor(text[(q1 + 1)..q2]) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static int ParseJavaMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], out var a)) return 0;
        if (a == 1 && parts.Length > 1 && int.TryParse(parts[1], out var b)) return b; // 1.8 → 8
        return a;
    }

    private static IEnumerable<string?> JavaCandidates(string? configured)
    {
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        yield return string.IsNullOrWhiteSpace(configured) ? null : configured;
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "bin", exe);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) yield return Path.Combine(dir.Trim('"'), exe);
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" }.Distinct())
            foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" })
            {
                var d = Path.Combine(root, vendor);
                if (!Directory.Exists(d)) continue;
                foreach (var jdk in Directory.GetDirectories(d).OrderByDescending(x => x))
                    yield return Path.Combine(jdk, "bin", exe);
            }
        }
    }

    /// <summary>앱과 같은 폴더(또는 server 하위 폴더)의 서버 jar.</summary>
    public static string? FindJar(string baseDir) =>
        new[] { Path.Combine(baseDir, JarName), Path.Combine(baseDir, "server", JarName) }.FirstOrDefault(File.Exists);

    public void Start(string java, string jar, IEnumerable<string> args)
    {
        if (IsRunning) return;
        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(jar)!,
        };
        // 콘솔이 없으면 서버가 UTF-8 로 출력한다
        foreach (var a in new[] { "-Xmx3g", "-Dfile.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-jar", jar }.Concat(args))
            psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Add(e.Data); };
        p.Exited += (_, _) =>
        {
            Add($"[서버 종료 코드 {p.ExitCode}]");
            Exited?.Invoke(p.ExitCode);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        Add($"[서버 시작: {java} -jar {Path.GetFileName(jar)} {string.Join(' ', args)}]");
    }

    private void Add(string line)
    {
        lock (_logLock)
        {
            _log.AddLast(line);
            while (_log.Count > 500) _log.RemoveFirst();
        }
        AppLog.Write("server.log", line);
        LogLine?.Invoke(line);
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // 이미 종료됨
        }
        p.Dispose();
    }

    public void Dispose() => Stop();
}
