using System.Net;
using BinanceCumulative.Pc.Models;

namespace BinanceCumulative.Pc.Api;

/// <summary>서버 응답: 해석한 값 + 받은 JSON 원문 (로컬 DB 에 그대로 저장하기 위해).</summary>
public sealed record Fetched<T>(T Value, string Json);

/// <summary>PC 서버(java)와 통신: 순위, 페어 그래프, 그룹 그래프, 검증 상태.</summary>
public sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http;

    public ServerApi(string baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl.TrimEnd('/') + "/";
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public string BaseUrl { get; }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var res = await _http.GetAsync("health", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>서버 상태 한 줄 (예: "ok · 1372개 중 243개 최신화 중 · 243개 페어 · …"). 실패하면 null.</summary>
    public async Task<string?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            return await _http.GetStringAsync("health", cts.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<Fetched<Ranking>> GetRankingAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync("ranking.json", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) throw new ServerBusyException(body);
        res.EnsureSuccessStatusCode();
        return new Fetched<Ranking>(Ranking.Parse(body), body);
    }

    /// <param name="from">기간 시작(ms). 0 이면 저장된 가장 예전 1분봉부터</param>
    public async Task<Fetched<Series>> GetSeriesAsync(string market, string symbol, long from, SplitMethod method, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"series?m={market}&s={Uri.EscapeDataString(symbol)}&from={from}&method={method}", ct);
        return new Fetched<Series>(Series.Parse(json), json);
    }

    public async Task<Fetched<GroupSeries>> GetGroupSeriesAsync(string market, string group, long from, SplitMethod method, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"group-series?m={market}&g={Uri.EscapeDataString(group)}&from={from}&method={method}", ct);
        return new Fetched<GroupSeries>(GroupSeries.Parse(json), json);
    }

    public async Task<string> GetVerifyNoteAsync(CancellationToken ct = default)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync("verify.json", ct));
        return doc.RootElement.GetProperty("note").GetString() ?? "";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>서버가 아직 첫 계산 중 (503).</summary>
public sealed class ServerBusyException(string message) : Exception(message);
