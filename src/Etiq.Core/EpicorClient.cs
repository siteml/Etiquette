using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Etiq.Core;

/// <summary>
/// Epicor Kinetic (Public Cloud) REST v2 config. Mirrors the labelprint
/// reference app's `epicor` config section (reference/labelprint/config.json).
/// </summary>
public sealed class EpicorConfig
{
    public string BaseUrl { get; set; } = "";
    public string Company { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string BaqId { get; set; } = "";
    /// <summary>"filter" (OData $filter on a display column) or "param" (named BAQ parameter).</summary>
    public string QueryMode { get; set; } = "filter";
    public string JobParam { get; set; } = "JobNum";
    public Dictionary<string, string> FieldMap { get; set; } = new();
}

/// <summary>
/// Minimal Epicor Kinetic REST v2 client: BAQ rows + Kinetic Function calls.
/// C# port of reference/labelprint/epicor.go. HttpMessageHandler is injectable
/// for testing; no packages required.
/// </summary>
public sealed class EpicorClient : IDisposable
{
    private readonly EpicorConfig _cfg;
    private readonly HttpClient _http;

    public EpicorClient(EpicorConfig cfg, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (cfg.ApiKey != "")
            _http.DefaultRequestHeaders.Add("x-api-key", cfg.ApiKey);
        if (cfg.Username != "")
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Password}")));
    }

    private string Base => _cfg.BaseUrl.TrimEnd('/');

    /// <summary>
    /// Fetch the first BAQ result row for a job number.
    /// Guard: an empty job would drop the filter and pull the ENTIRE BAQ.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> FetchBaqRowAsync(string job, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(job))
            throw new ArgumentException("job number is empty; refusing to query the whole BAQ", nameof(job));

        string url = $"{Base}/api/v2/odata/{Uri.EscapeDataString(_cfg.Company)}/BaqSvc/{Uri.EscapeDataString(_cfg.BaqId)}/Data";
        string query = _cfg.QueryMode.Equals("param", StringComparison.OrdinalIgnoreCase)
            ? $"{Uri.EscapeDataString(_cfg.JobParam)}={Uri.EscapeDataString(job)}"
            : BuildFilter(job);
        url += "?" + query;

        using var resp = await _http.GetAsync(url, ct);
        string body = await ReadCappedAsync(resp, ct);
        if ((int)resp.StatusCode != 200)
            throw new EpicorException($"Epicor returned HTTP {(int)resp.StatusCode}: {Trim(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw new EpicorException($"Invalid Job Number: {job}");

        var row = new Dictionary<string, JsonElement>();
        foreach (var p in value[0].EnumerateObject())
            row[p.Name] = p.Value.Clone();
        return row;
    }

    private string BuildFilter(string job)
    {
        if (!_cfg.FieldMap.TryGetValue("JobNum", out var col) || col == "")
            throw new EpicorException("fieldMap.JobNum must be set for filter mode");
        string escaped = job.Replace("'", "''");
        return "$filter=" + Uri.EscapeDataString($"{col} eq '{escaped}'");
    }

    /// <summary>
    /// Call a Kinetic Function: POST /api/v2/efx/{Company}/{library}/{function}.
    /// This is the transport for the serialization counter provider (roadmap
    /// Phase 3): the Function does an atomic UD-table read-increment server-side.
    /// </summary>
    public async Task<JsonElement> CallFunctionAsync(string library, string function,
        object? payload = null, CancellationToken ct = default)
    {
        string url = $"{Base}/api/v2/efx/{Uri.EscapeDataString(_cfg.Company)}/{Uri.EscapeDataString(library)}/{Uri.EscapeDataString(function)}";
        using var content = new StringContent(JsonSerializer.Serialize(payload ?? new { }),
                                              Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);
        string body = await ReadCappedAsync(resp, ct);
        if ((int)resp.StatusCode is not (200 or 201 or 204))
            throw new EpicorException($"Epicor function {library}/{function} returned HTTP {(int)resp.StatusCode}: {Trim(body, 300)}");
        return body.Length == 0
            ? default
            : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        const int cap = 4 << 20;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (ms.Length < cap)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, cap - ms.Length)), ct);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "..." : s;

    public void Dispose() => _http.Dispose();
}

public sealed class EpicorException : Exception
{
    public EpicorException(string message) : base(message) { }
}
