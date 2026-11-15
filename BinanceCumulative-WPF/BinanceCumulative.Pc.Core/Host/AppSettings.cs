using System.Text.Json;
using System.Text.Json.Serialization;
using BinanceCumulative.Pc.Storage;

namespace BinanceCumulative.Pc.Host;

/// <summary>
/// PC 앱 설정. 로컬 DB(<see cref="LocalDb"/>)의 settings 표에 속성별 한 줄(값은 JSON)로 저장한다.
/// 예전 버전의 %AppData%\BinanceCumulative\settings.json 이 있고 DB 가 비어 있으면 한 번 옮겨 온다.
/// </summary>
public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:8080";
    /// <summary>서버가 꺼져 있으면 이 PC 에서 직접 켠다 (주소가 localhost 일 때)</summary>
    public bool AutoStartServer { get; set; } = true;
    public string DataFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BinanceCumulative", "binance-data");
    public bool IncludeFutures { get; set; }
    /// <summary>수집 기간(개월). 120 = 바이낸스 첫 1분봉(2017-07)부터 전부 (서버 최대)</summary>
    public int Months { get; set; } = MaxMonths;
    public bool KeepRaw { get; set; }
    /// <summary>서버가 동시에 받는 페어 수 (서버 기본 4). 처음 전체 수집이 오래 걸려 기본을 12 로</summary>
    public int Threads { get; set; } = DefaultThreads;
    public const int DefaultThreads = 12;
    /// <summary>표·합계에서 뺄 코인 (순매수 순위에 의미 없는 것: 기준 자산·스테이블·래핑 코인). 데이터·집계는 그대로</summary>
    public List<string> ExcludedCoins { get; set; } = [.. DefaultExcludedCoins];
    public static readonly string[] DefaultExcludedCoins =
    [
        // 기준 자산
        "BTC",
        // 달러 고정 스테이블코인 (바이낸스 거래 중 + 예전에 있던 것)
        "USDT", "USDC", "FDUSD", "TUSD", "USD1", "USDE", "USDS", "RLUSD", "BFUSD", "RWUSD", "XUSD", "U",
        "BUSD", "USDP", "PAX", "DAI", "PYUSD", "UST",
        // 유로 고정·법정화폐
        "EUR", "EURI", "AEUR",
        // 다른 코인을 감싼 래핑·스테이킹 코인 (가격이 원래 코인을 그대로 따라감)
        "WBTC", "WBETH", "BETH", "BNSOL", "WETH", "STETH",
    ];
    /// <summary>주식 토큰(바이낸스 bStocks: AAPLB · NVDAB · SPYB …)을 표·합계에서 뺀다. 데이터·집계는 그대로</summary>
    public bool ExcludeStocks { get; set; } = true;
    public string CmcKey { get; set; } = "";
    public string JavaPath { get; set; } = "";
    /// <summary>앱이 켜져 있는 동안 종목별 일별 집계를 자동으로 이어서 DB 에 쌓는다</summary>
    public bool AutoDailyAggregate { get; set; } = true;

    // 마지막 화면 선택
    public string Market { get; set; } = "SPOT";
    public string Group { get; set; } = "USDT";
    public string Period { get; set; } = "1M";
    /// <summary>매수·매도 나누는 방식. 기본 C(체결: 바이낸스 실제 시장가 매수 체결량)</summary>
    public string Method { get; set; } = "TAKER";
    /// <summary>표 금액 단위: QUOTE(호가 자산 그대로) · USD · BTC …. 기본은 USD(≈USDT)</summary>
    public string DisplayUnit { get; set; } = "USD";
    /// <summary>true: 같은 코인의 모든 페어를 한 줄로 합산(기본). false: 페어별</summary>
    public bool ByCoin { get; set; } = true;

    public const int MaxMonths = 120;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>예전 버전이 쓰던 JSON 설정 파일 (DB 로 옮긴 뒤에는 읽지 않음)</summary>
    public static string LegacyJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BinanceCumulative", "settings.json");

    /// <summary>DB 에서 읽기. DB 에 설정이 없으면 예전 JSON 파일 → 기본값 순으로 가져와 DB 에 저장한다.</summary>
    public static AppSettings Load(LocalDb db, string? legacyJsonPath = null)
    {
        var rows = db.GetSettings();
        if (rows.Count > 0) return FromRows(rows);
        var s = LoadJson(legacyJsonPath ?? LegacyJsonPath);
        s.Save(db);
        return s;
    }

    public void Save(LocalDb db) => db.SetSettings(ToRows());

    public static AppSettings LoadJson(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 손상된 설정은 기본값으로
        }
        return new AppSettings();
    }

    public void SaveJson(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    private IReadOnlyDictionary<string, string> ToRows()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(this));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
    }

    private static AppSettings FromRows(IReadOnlyDictionary<string, string> rows)
    {
        var json = "{" + string.Join(",", rows.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}:{kv.Value}")) + "}";
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    [JsonIgnore]
    public bool IsLocalServer =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    [JsonIgnore]
    public int Port => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) ? u.Port : 8080;

    /// <summary>서버 실행 인자.</summary>
    public IReadOnlyList<string> ServerArgs()
    {
        var args = new List<string> { "--no-browser", "--port", Port.ToString(), "--data", DataFolder, "--months", Months.ToString() };
        if (IncludeFutures) args.AddRange(["--markets", "spot,futures"]);
        if (KeepRaw) args.Add("--keep-raw");
        args.AddRange(["--threads", Math.Clamp(Threads, 1, 32).ToString()]);
        if (!string.IsNullOrWhiteSpace(CmcKey)) args.AddRange(["--cmc-key", CmcKey.Trim()]);
        return args;
    }
}
