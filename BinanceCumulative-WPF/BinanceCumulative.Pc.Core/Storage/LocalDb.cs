using Microsoft.Data.Sqlite;

namespace BinanceCumulative.Pc.Storage;

/// <summary>상장 이후 전체 누적 한 건 (호가 자산 금액 + 그날 환율 USDT 금액).</summary>
public sealed record AllTime(decimal Amount, decimal Buy, decimal NetBase, decimal? AmountUsd, decimal? BuyUsd);

/// <summary>저장본 한 건: 서버가 준 JSON 그대로 + 받은 시각.</summary>
public sealed record CachedJson(string Json, long FromMillis, long FetchedAt);

/// <summary>
/// 앱 로컬 DB (SQLite, 기본 %AppData%\BinanceCumulative\app.db).
/// - settings: 앱 설정 (키/값, 값은 JSON)
/// - ranking_cache: 마지막으로 받은 순위(/ranking.json)
/// - series_cache / group_cache: 페어·그룹 그래프(/series, /group-series) 저장본
/// - daily_agg: 종목별 일별 집계 (USDT 환산 포함)
/// 서버가 꺼져 있어도 마지막 데이터를 보여주고, 다시 고른 페어는 저장본을 먼저 그린다.
/// 1분봉 원본은 계산 서버(java)가 파일로 가지고 있고 여기엔 없다.
/// UI 스레드 밖에서도 불릴 수 있어 모든 접근을 잠근다.
/// </summary>
public sealed class LocalDb : IDisposable
{
    public const string InMemory = ":memory:";
    /// <summary>daily_agg 구조 버전. 바뀌면 표를 새로 만든다 (집계는 다시 돌리면 채워진다)</summary>
    public const int DailySchemaVersion = 3;

    private readonly SqliteConnection _c;
    private readonly object _lock = new();

    /// <summary>
    /// 기본 DB 위치. 설정은 DB 안에 있으므로 DB 위치만은 DB 밖 파일
    /// (%AppData%\BinanceCumulative\db-path.txt, 한 줄짜리 경로)로 바꾼다. 없거나 비어 있으면 %AppData% 아래 app.db.
    /// </summary>
    public static string DefaultPath => ResolvePath(LocationFile);

    public static string LocationFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BinanceCumulative", "db-path.txt");

    public static string ResolvePath(string locationFile)
    {
        try
        {
            if (File.Exists(locationFile) && File.ReadAllText(locationFile).Trim() is { Length: > 0 } p) return p;
        }
        catch (Exception)
        {
            // 읽지 못하면 기본 위치
        }
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(locationFile)!, "app.db");
    }

    public string FilePath { get; }

    /// <param name="path">DB 파일 경로. 비우면 기본 위치, <see cref="InMemory"/> 면 메모리(테스트)</param>
    public LocalDb(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        var memory = FilePath == InMemory;
        if (!memory) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = memory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        _c.Open();
        if (!memory) Exec("PRAGMA journal_mode=WAL");
        Exec("""
            CREATE TABLE IF NOT EXISTS settings(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ranking_cache(
                id INTEGER PRIMARY KEY CHECK (id = 1),
                fetched_at INTEGER NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS series_cache(
                market TEXT NOT NULL, symbol TEXT NOT NULL, period TEXT NOT NULL, method TEXT NOT NULL,
                from_ms INTEGER NOT NULL, fetched_at INTEGER NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY (market, symbol, period, method));
            CREATE TABLE IF NOT EXISTS group_cache(
                market TEXT NOT NULL, grp TEXT NOT NULL, period TEXT NOT NULL, method TEXT NOT NULL,
                from_ms INTEGER NOT NULL, fetched_at INTEGER NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY (market, grp, period, method));
            """);
        // 일별 집계 표: 구조가 예전 버전이면 새로 만든다
        if (GetMeta("daily.schema") != DailySchemaVersion.ToString())
        {
            Exec("DROP VIEW IF EXISTS daily_coin; DROP TABLE IF EXISTS daily_agg;");
            SetMeta("daily.schema", DailySchemaVersion.ToString());
        }
        Exec("""
            -- 종목별 · 분할 방식별 · UTC 하루 한 줄.
            -- *_quote: 그 페어의 호가 자산(USDT·BTC·FDUSD …) 단위 그날 값, *_usd: 그날의 환율(usd_rate = 호가 자산 1개의 USDT 가격)로 바꾼 USDT 금액.
            -- cum_*: 저장된 가장 예전 날부터 그날까지의 누적. 환율을 모르는 날은 *_usd 가 NULL 이고 cum_*_usd 는 전날 값 유지.
            CREATE TABLE IF NOT EXISTS daily_agg(
                market TEXT NOT NULL, symbol TEXT NOT NULL, method TEXT NOT NULL,
                base TEXT NOT NULL DEFAULT '', quote TEXT NOT NULL DEFAULT '', base_mult REAL NOT NULL DEFAULT 1,
                day INTEGER NOT NULL, day_text TEXT NOT NULL,
                net_quote REAL NOT NULL, buy_quote REAL NOT NULL, sell_quote REAL NOT NULL, amount_quote REAL NOT NULL,
                net_base REAL NOT NULL,
                cum_net_quote REAL NOT NULL, cum_net_base REAL NOT NULL, cum_amount_quote REAL NOT NULL,
                usd_rate REAL, net_usd REAL, buy_usd REAL, sell_usd REAL, amount_usd REAL,
                cum_net_usd REAL, cum_amount_usd REAL,
                open REAL NOT NULL, high REAL NOT NULL, low REAL NOT NULL, close REAL NOT NULL,
                final INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                PRIMARY KEY (market, symbol, method, day));
            CREATE INDEX IF NOT EXISTS daily_agg_day ON daily_agg(day);
            -- 페어 상장일: 바이낸스 첫 1분봉 시각 (한 번 받으면 바뀌지 않음)
            CREATE TABLE IF NOT EXISTS pair_listing(
                market TEXT NOT NULL, symbol TEXT NOT NULL, first_ms INTEGER NOT NULL,
                PRIMARY KEY (market, symbol));
            CREATE INDEX IF NOT EXISTS daily_agg_base ON daily_agg(market, base, method, day);
            -- 코인별 합산: 같은 코인(base)의 모든 페어를 USDT 금액으로 더한 하루 한 줄 (BNB/USDT + BNB/BTC + BNB/ETH → BNB).
            -- net_coins 는 배수(1000PEPE 등)를 풀어 실제 코인 수. cum_* 은 날짜순 누적(윈도 합).
            CREATE VIEW IF NOT EXISTS daily_coin AS
            SELECT market, base, method, day, day_text,
                   COUNT(*) AS pairs, GROUP_CONCAT(quote, '+') AS quotes,
                   SUM(net_usd) AS net_usd, SUM(buy_usd) AS buy_usd, SUM(sell_usd) AS sell_usd, SUM(amount_usd) AS amount_usd,
                   SUM(net_base * base_mult) AS net_coins,
                   SUM(SUM(net_usd)) OVER w AS cum_net_usd,
                   SUM(SUM(amount_usd)) OVER w AS cum_amount_usd,
                   SUM(SUM(net_base * base_mult)) OVER w AS cum_net_coins,
                   MIN(final) AS final
            FROM daily_agg
            GROUP BY market, base, method, day
            WINDOW w AS (PARTITION BY market, base, method ORDER BY day ROWS UNBOUNDED PRECEDING);
            """);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 페어 상장일 ----

    /// <summary>"SPOT:BTCUSDT" → 첫 1분봉 시각(ms)</summary>
    public Dictionary<string, long> GetListings()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, long>();
            using var cmd = Cmd("SELECT market, symbol, first_ms FROM pair_listing");
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[$"{r.GetString(0)}:{r.GetString(1)}"] = r.GetInt64(2);
            return d;
        }
    }

    public void PutListing(string market, string symbol, long firstMs) =>
        Exec("INSERT INTO pair_listing(market, symbol, first_ms) VALUES ($m, $s, $f) ON CONFLICT(market, symbol) DO UPDATE SET first_ms = excluded.first_ms",
            ("$m", market), ("$s", symbol), ("$f", firstMs));

    /// <summary>
    /// 상장 이후 전체 누적 (일별 집계 합): "SPOT:BTCUSDT" → 방식별 (호가 금액·매수 합, 순매수 코인 수, 그날 환율로 바꾼 USDT 금액·매수 합).
    /// USDT 합은 환율을 모르는 날(BTCUSDT 상장 전 등)을 빼고 더한 값 — 없으면 null. 일별 집계에 아직 없는 페어는 빠진다.
    /// </summary>
    public Dictionary<string, Dictionary<string, AllTime>> GetAllTimeTotals()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, Dictionary<string, AllTime>>();
            using var cmd = Cmd("""
                SELECT market, symbol, method, SUM(amount_quote), SUM(buy_quote), SUM(net_base), SUM(amount_usd), SUM(buy_usd)
                FROM daily_agg GROUP BY market, symbol, method
                """);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = $"{r.GetString(0)}:{r.GetString(1)}";
                if (!d.TryGetValue(key, out var m)) d[key] = m = new Dictionary<string, AllTime>();
                decimal? Opt(int i) => r.IsDBNull(i) ? null : (decimal)r.GetDouble(i);
                m[r.GetString(2)] = new AllTime((decimal)r.GetDouble(3), (decimal)r.GetDouble(4), (decimal)r.GetDouble(5), Opt(6), Opt(7));
            }
            return d;
        }
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = Cmd(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    private T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = Cmd(sql, args);
            var v = cmd.ExecuteScalar();
            if (v is null or DBNull) return default;
            return (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
    }

    // ---- 설정 ----

    public IReadOnlyDictionary<string, string> GetSettings()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, string>();
            using var cmd = Cmd("SELECT key, value FROM settings");
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetString(1);
            return d;
        }
    }

    public void SetSettings(IReadOnlyDictionary<string, string> rows)
    {
        lock (_lock)
        {
            using var tx = _c.BeginTransaction();
            foreach (var (k, v) in rows)
            {
                using var cmd = Cmd("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                    ("$k", k), ("$v", v));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---- 메타 (마지막 집계 시각 등) ----

    public string? GetMeta(string key) => Scalar<string>("SELECT value FROM meta WHERE key = $k", ("$k", key));

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    // ---- 순위 저장본 ----

    public void PutRanking(string json, long? now = null) =>
        Exec("INSERT INTO ranking_cache(id, fetched_at, json) VALUES (1, $t, $j) ON CONFLICT(id) DO UPDATE SET fetched_at = excluded.fetched_at, json = excluded.json",
            ("$t", now ?? Now()), ("$j", json));

    public CachedJson? GetRanking()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT json, fetched_at FROM ranking_cache WHERE id = 1");
            using var r = cmd.ExecuteReader();
            return r.Read() ? new CachedJson(r.GetString(0), 0, r.GetInt64(1)) : null;
        }
    }

    // ---- 그래프 저장본 ----

    public void PutSeries(string market, string symbol, string period, string method, long from, string json, long? now = null) =>
        Exec("""
            INSERT INTO series_cache(market, symbol, period, method, from_ms, fetched_at, json)
            VALUES ($m, $s, $p, $me, $f, $t, $j)
            ON CONFLICT(market, symbol, period, method) DO UPDATE SET from_ms = excluded.from_ms, fetched_at = excluded.fetched_at, json = excluded.json
            """, ("$m", market), ("$s", symbol), ("$p", period), ("$me", method), ("$f", from), ("$t", now ?? Now()), ("$j", json));

    public CachedJson? GetSeries(string market, string symbol, string period, string method) =>
        Get("SELECT json, from_ms, fetched_at FROM series_cache WHERE market = $m AND symbol = $k AND period = $p AND method = $me",
            ("$m", market), ("$k", symbol), ("$p", period), ("$me", method));

    public void PutGroup(string market, string group, string period, string method, long from, string json, long? now = null) =>
        Exec("""
            INSERT INTO group_cache(market, grp, period, method, from_ms, fetched_at, json)
            VALUES ($m, $g, $p, $me, $f, $t, $j)
            ON CONFLICT(market, grp, period, method) DO UPDATE SET from_ms = excluded.from_ms, fetched_at = excluded.fetched_at, json = excluded.json
            """, ("$m", market), ("$g", group), ("$p", period), ("$me", method), ("$f", from), ("$t", now ?? Now()), ("$j", json));

    public CachedJson? GetGroup(string market, string group, string period, string method) =>
        Get("SELECT json, from_ms, fetched_at FROM group_cache WHERE market = $m AND grp = $k AND period = $p AND method = $me",
            ("$m", market), ("$k", group), ("$p", period), ("$me", method));

    private CachedJson? Get(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new CachedJson(r.GetString(0), r.GetInt64(1), r.GetInt64(2)) : null;
        }
    }

    /// <summary>오래 안 쓴 그래프 저장본 삭제. 지운 줄 수를 돌려준다.</summary>
    public int Prune(long olderThanMillis)
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var t in new[] { "series_cache", "group_cache" })
            {
                using var cmd = Cmd($"DELETE FROM {t} WHERE fetched_at < $t", ("$t", olderThanMillis));
                n += cmd.ExecuteNonQuery();
            }
            return n;
        }
    }

    /// <summary>순위·그래프 저장본 모두 삭제 (설정·일별 집계는 유지).</summary>
    public void ClearCache()
    {
        Exec("DELETE FROM series_cache; DELETE FROM group_cache; DELETE FROM ranking_cache;");
        if (FilePath != InMemory) Exec("VACUUM");
    }

    public int SeriesCount => Scalar<int>("SELECT COUNT(*) FROM series_cache") + Scalar<int>("SELECT COUNT(*) FROM group_cache");

    /// <summary>DB 파일 크기 (WAL 포함). 메모리 DB 는 0.</summary>
    public long SizeBytes()
    {
        if (FilePath == InMemory) return 0;
        long n = 0;
        foreach (var f in new[] { FilePath, FilePath + "-wal" })
            if (File.Exists(f)) n += new FileInfo(f).Length;
        return n;
    }

    // ---- 종목별 일별 집계 ----

    /// <summary>마감된(final) 마지막 날의 UTC 시작(ms). 없으면 null.</summary>
    public long? LastFinalDay(string market, string symbol, string method) =>
        Scalar<long?>("SELECT MAX(day) FROM daily_agg WHERE market = $m AND symbol = $s AND method = $me AND final = 1",
            ("$m", market), ("$s", symbol), ("$me", method));

    /// <summary>
    /// 모든 종목·방식의 집계 상태를 한 번에: "시장|심볼|방식" → (마감된 마지막 날, 마지막으로 쓴 시각).
    /// 집계가 종목마다 따로 묻지 않고, 받을 게 없는 종목을 미리 걸러내는 데 쓴다.
    /// </summary>
    public Dictionary<string, (long? LastFinal, long UpdatedAt)> DailyStatus()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, (long?, long)>();
            using var cmd = Cmd("SELECT market, symbol, method, MAX(CASE WHEN final = 1 THEN day END), MAX(updated_at) FROM daily_agg GROUP BY market, symbol, method");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                d[$"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}"] = (r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4));
            return d;
        }
    }

    /// <summary>저장된 가장 예전 날. 없으면 null.</summary>
    public long? FirstDay(string market, string symbol, string method) =>
        Scalar<long?>("SELECT MIN(day) FROM daily_agg WHERE market = $m AND symbol = $s AND method = $me",
            ("$m", market), ("$s", symbol), ("$me", method));

    /// <summary>어느 날 직전까지의 누적 (그 날 이전 마지막 줄). 없으면 null.</summary>
    public DailyCum? CumBefore(string market, string symbol, string method, long day)
    {
        lock (_lock)
        {
            using var cmd = Cmd("""
                SELECT cum_net_quote, cum_net_base, cum_amount_quote, cum_net_usd, cum_amount_usd FROM daily_agg
                WHERE market = $m AND symbol = $s AND method = $me AND day < $d ORDER BY day DESC LIMIT 1
                """, ("$m", market), ("$s", symbol), ("$me", method), ("$d", day));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new DailyCum(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.IsDBNull(3) ? 0 : r.GetDouble(3), r.IsDBNull(4) ? 0 : r.GetDouble(4))
                : null;
        }
    }

    /// <summary>한 종목의 날짜별 종가 (환율용). 방식에 상관없이 가격은 같아 아무 방식이나 하나.</summary>
    public Dictionary<long, double> DailyCloses(string market, string symbol)
    {
        lock (_lock)
        {
            var d = new Dictionary<long, double>();
            using var cmd = Cmd("SELECT day, close FROM daily_agg WHERE market = $m AND symbol = $s AND method = (SELECT MIN(method) FROM daily_agg WHERE market = $m AND symbol = $s)",
                ("$m", market), ("$s", symbol));
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetInt64(0)] = r.GetDouble(1);
            return d;
        }
    }

    public void UpsertDaily(IReadOnlyList<DailyRow> rows, long? now = null)
    {
        if (rows.Count == 0) return;
        var t = now ?? Now();
        lock (_lock)
        {
            using var tx = _c.BeginTransaction();
            using var cmd = Cmd("""
                INSERT INTO daily_agg(market, symbol, method, day, day_text,
                    net_quote, buy_quote, sell_quote, amount_quote, net_base,
                    cum_net_quote, cum_net_base, cum_amount_quote,
                    usd_rate, net_usd, buy_usd, sell_usd, amount_usd, cum_net_usd, cum_amount_usd,
                    open, high, low, close, final, updated_at, base, quote, base_mult)
                VALUES ($m, $s, $me, $d, $dt, $n, $b, $sl, $a, $nb, $cn, $cb, $ca, $r, $un, $ub, $us, $ua, $ucn, $uca, $o, $h, $l, $c, $f, $t, $ba, $q, $bm)
                ON CONFLICT(market, symbol, method, day) DO UPDATE SET
                    base = excluded.base, quote = excluded.quote, base_mult = excluded.base_mult,
                    net_quote = excluded.net_quote, buy_quote = excluded.buy_quote, sell_quote = excluded.sell_quote,
                    amount_quote = excluded.amount_quote, net_base = excluded.net_base,
                    cum_net_quote = excluded.cum_net_quote, cum_net_base = excluded.cum_net_base, cum_amount_quote = excluded.cum_amount_quote,
                    usd_rate = excluded.usd_rate, net_usd = excluded.net_usd, buy_usd = excluded.buy_usd, sell_usd = excluded.sell_usd,
                    amount_usd = excluded.amount_usd, cum_net_usd = excluded.cum_net_usd, cum_amount_usd = excluded.cum_amount_usd,
                    open = excluded.open, high = excluded.high, low = excluded.low, close = excluded.close,
                    final = excluded.final, updated_at = excluded.updated_at
                """);
            cmd.Transaction = tx;
            var names = new[] { "$m", "$s", "$me", "$d", "$dt", "$n", "$b", "$sl", "$a", "$nb", "$cn", "$cb", "$ca", "$r", "$un", "$ub", "$us", "$ua", "$ucn", "$uca", "$o", "$h", "$l", "$c", "$f", "$t", "$ba", "$q", "$bm" };
            var p = names.Select(n => cmd.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var x in rows)
            {
                object?[] v =
                [
                    x.Market, x.Symbol, x.Method, x.Day, DailyRow.DayText(x.Day),
                    x.NetQuote, x.BuyQuote, x.SellQuote, x.AmountQuote, x.NetBase,
                    x.CumNetQuote, x.CumNetBase, x.CumAmountQuote,
                    x.UsdRate, x.NetUsd, x.BuyUsd, x.SellUsd, x.AmountUsd, x.CumNetUsd, x.CumAmountUsd,
                    x.Open, x.High, x.Low, x.Close, x.Final ? 1 : 0, t, x.Base, x.Quote, x.BaseMult,
                ];
                for (var i = 0; i < v.Length; i++) p[i].Value = v[i] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<DailyRow> GetDaily(string market, string symbol, string method, long fromDay = 0, long toDay = long.MaxValue)
    {
        lock (_lock)
        {
            using var cmd = Cmd("""
                SELECT day, net_quote, buy_quote, sell_quote, amount_quote, net_base, cum_net_quote, cum_net_base, cum_amount_quote,
                       open, high, low, close, final,
                       usd_rate, net_usd, buy_usd, sell_usd, amount_usd, cum_net_usd, cum_amount_usd,
                       base, quote, base_mult
                FROM daily_agg WHERE market = $m AND symbol = $s AND method = $me AND day >= $f AND day <= $t ORDER BY day
                """, ("$m", market), ("$s", symbol), ("$me", method), ("$f", fromDay), ("$t", toDay));
            using var r = cmd.ExecuteReader();
            var list = new List<DailyRow>();
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read())
                list.Add(new DailyRow(market, symbol, method, r.GetInt64(0),
                    r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5),
                    r.GetDouble(6), r.GetDouble(7), r.GetDouble(8),
                    r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetInt64(13) == 1)
                {
                    UsdRate = D(14), NetUsd = D(15), BuyUsd = D(16), SellUsd = D(17), AmountUsd = D(18), CumNetUsd = D(19), CumAmountUsd = D(20),
                    Base = r.GetString(21), Quote = r.GetString(22), BaseMult = r.GetDouble(23),
                });
            return list;
        }
    }

    /// <summary>코인별 합산 하루 한 줄 (daily_coin 뷰).</summary>
    public IReadOnlyList<CoinDay> GetCoinDaily(string market, string baseAsset, string method)
    {
        lock (_lock)
        {
            using var cmd = Cmd("""
                SELECT day, pairs, quotes, net_usd, buy_usd, sell_usd, amount_usd, net_coins, cum_net_usd, cum_amount_usd, cum_net_coins, final
                FROM daily_coin WHERE market = $m AND base = $b AND method = $me ORDER BY day
                """, ("$m", market), ("$b", baseAsset), ("$me", method));
            using var r = cmd.ExecuteReader();
            var list = new List<CoinDay>();
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read())
                list.Add(new CoinDay(market, baseAsset, method, r.GetInt64(0), (int)r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    D(3), D(4), D(5), D(6), r.GetDouble(7), D(8), D(9), r.GetDouble(10), r.GetInt64(11) == 1));
            return list;
        }
    }

    /// <summary>코인별 합산(daily_coin)을 CSV 로. 기본 단위 USDT.</summary>
    public long ExportCoinCsv(string path)
    {
        lock (_lock)
        {
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("market,coin,method,day,pairs,quotes,net_usd,buy_usd,sell_usd,amount_usd,net_coins,cum_net_usd,cum_amount_usd,cum_net_coins,final");
            using var cmd = Cmd("""
                SELECT market, base, method, day_text, pairs, quotes, net_usd, buy_usd, sell_usd, amount_usd, net_coins,
                       cum_net_usd, cum_amount_usd, cum_net_coins, final
                FROM daily_coin ORDER BY market, base, method, day
                """);
            using var r = cmd.ExecuteReader();
            long n = 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            while (r.Read())
            {
                var cells = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    cells[i] = r.IsDBNull(i) ? "" : r.GetFieldType(i) == typeof(double) ? r.GetDouble(i).ToString("R", inv) : r.GetValue(i).ToString() ?? "";
                w.WriteLine(string.Join(',', cells));
                n++;
            }
            return n;
        }
    }

    /// <summary>USDT 환율이 비어 있는 줄이 있는 (시장, 심볼, 방식, 호가) 목록 — 환율 페어가 나중에 수집되면 보충 대상.</summary>
    public IReadOnlyList<(string Market, string Symbol, string Method, string Quote)> PairsMissingUsd()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT DISTINCT market, symbol, method, quote FROM daily_agg WHERE usd_rate IS NULL ORDER BY market, symbol, method");
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    public long CoinDayCount => Scalar<long>("SELECT COUNT(*) FROM daily_coin");
    public long DailyRowCount => Scalar<long>("SELECT COUNT(*) FROM daily_agg");
    public long DailySymbolCount => Scalar<long>("SELECT COUNT(*) FROM (SELECT DISTINCT market, symbol FROM daily_agg)");

    /// <summary>daily_agg 전체를 CSV 로 (추후 엑셀·파이썬 등에서 쓰기 위해). USDT 환산 열이 먼저, 호가 자산 원래 값이 뒤.</summary>
    public long ExportDailyCsv(string path)
    {
        lock (_lock)
        {
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("market,symbol,method,day,usd_rate,net_usd,buy_usd,sell_usd,amount_usd,cum_net_usd,cum_amount_usd," +
                        "net_quote,buy_quote,sell_quote,amount_quote,net_base,cum_net_quote,cum_net_base,cum_amount_quote,open,high,low,close,final");
            using var cmd = Cmd("""
                SELECT market, symbol, method, day_text, usd_rate, net_usd, buy_usd, sell_usd, amount_usd, cum_net_usd, cum_amount_usd,
                       net_quote, buy_quote, sell_quote, amount_quote, net_base, cum_net_quote, cum_net_base, cum_amount_quote,
                       open, high, low, close, final
                FROM daily_agg ORDER BY market, symbol, method, day
                """);
            using var r = cmd.ExecuteReader();
            long n = 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            while (r.Read())
            {
                var cells = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    cells[i] = r.IsDBNull(i) ? "" : r.GetFieldType(i) == typeof(double) ? r.GetDouble(i).ToString("R", inv) : r.GetValue(i).ToString() ?? "";
                w.WriteLine(string.Join(',', cells));
                n++;
            }
            return n;
        }
    }

    public void Dispose()
    {
        lock (_lock) _c.Dispose();
    }
}

/// <summary>어느 날 직전까지의 누적값.</summary>
public readonly record struct DailyCum(double NetQuote, double NetBase, double AmountQuote, double NetUsd, double AmountUsd);

/// <summary>코인별 합산 하루 (daily_coin 뷰 한 줄). 금액은 USDT, NetCoins 는 실제 코인 수.</summary>
public sealed record CoinDay(string Market, string Coin, string Method, long Day, int Pairs, string Quotes,
    double? NetUsd, double? BuyUsd, double? SellUsd, double? AmountUsd, double NetCoins,
    double? CumNetUsd, double? CumAmountUsd, double CumNetCoins, bool Final);
