using System.Text;
using System.Text.Json;

namespace Etiq.Core;

/// <summary>GLPI REST API config (connection type "glpi"): baseUrl is the
/// apirest.php endpoint; App-Token + user token authenticate.</summary>
public sealed class GlpiConfig
{
    /// <summary>e.g. https://glpi.example.local/apirest.php (with or
    /// without the trailing slash).</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>API client token (Setup → General → API → API clients).</summary>
    public string AppToken { get; set; } = "";
    /// <summary>Personal token of the service user (user preferences →
    /// Remote access keys → API token).</summary>
    public string UserToken { get; set; } = "";
}

/// <summary>
/// Minimal GLPI REST client for declared etiq:query fetches: one item row
/// per label from any itemtype (Computer, Monitor, NetworkEquipment,
/// Peripheral, Phone, Printer, …). Session dance: GET initSession with
/// App-Token + "Authorization: user_token …" → Session-Token on every call,
/// GET killSession on dispose. Dropdown foreign keys (locations_id,
/// states_id, manufacturers_id, computermodels_id, users_id, …) come back
/// EXPANDED to their display names so a label can print "Building A"
/// without a second lookup. HttpMessageHandler injectable for tests; no
/// packages.
/// </summary>
public sealed class GlpiClient : IDisposable
{
    private readonly GlpiConfig _cfg;
    private readonly HttpClient _http;
    private string? _session;

    public GlpiClient(GlpiConfig cfg, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        if (cfg.AppToken != "")
            _http.DefaultRequestHeaders.Add("App-Token", cfg.AppToken);
    }

    private string Base => _cfg.BaseUrl.TrimEnd('/');

    /// <summary>GLPI insists on a Content-Type on EVERY call (GET included —
    /// its docs say so, and GLPI 11 answers HTTP 500 "unexpected error"
    /// without one). An empty JSON body is the cleanest way to carry it.</summary>
    private static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        return req;
    }

    /// <summary>Open (and cache) the session. Explicit call = "Test" in
    /// the Connections dialog; fetches call it implicitly.</summary>
    public async Task<string> InitSessionAsync(CancellationToken ct = default)
    {
        if (_session is not null) return _session;
        if (Base == "") throw new GlpiException("baseUrl is empty");
        if (_cfg.AppToken == "") throw new GlpiException("appToken is empty");
        if (_cfg.UserToken == "") throw new GlpiException("userToken is empty");
        using var req = Get($"{Base}/initSession");
        req.Headers.TryAddWithoutValidation("Authorization", $"user_token {_cfg.UserToken}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await ReadCappedAsync(resp, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode != 200)
            throw new GlpiException($"initSession returned HTTP {(int)resp.StatusCode}: {Trim(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        _session = doc.RootElement.TryGetProperty("session_token", out var tok) ? tok.GetString() : null;
        if (string.IsNullOrEmpty(_session))
            throw new GlpiException("initSession returned no session_token");
        return _session;
    }

    /// <summary>Best-effort: GLPI sessions expire on their own, but a tidy
    /// client releases them. Never throws.</summary>
    public async Task KillSessionAsync(CancellationToken ct = default)
    {
        if (_session is null) return;
        try
        {
            using var req = Get($"{Base}/killSession");
            req.Headers.TryAddWithoutValidation("Session-Token", _session ?? "");
            using var _ = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { /* releasing a session is not worth failing a print */ }
        finally { _session = null; }
    }

    /// <summary>
    /// Fetch ONE item as a flat column bag. Addressing:
    ///   param-id="{AssetId}"        → GET /{itemtype}/{id}
    ///   filter-serial="{Serial}"    → GET /{itemtype}?searchText[serial]=…
    ///   filter-otherserial="…"      (inventory number), filter-name="…" …
    /// GLPI's searchText is a substring match, so results are narrowed
    /// CLIENT-SIDE to rows whose filtered columns match exactly
    /// (case-insensitive); the first such row wins. "id" is the only
    /// param- name GLPI understands here (item types have no BAQ-style
    /// parameters); anything else is an error rather than a silent ignore.
    /// Guard: refuses a fetch with no non-empty constraint — that would
    /// list the whole inventory.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> FetchItemRowAsync(
        string itemtype,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> filters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemtype))
            throw new GlpiException("query= (the GLPI item type, e.g. Computer) is required");
        itemtype = itemtype.Trim().Trim('/');

        string? id = null;
        foreach (var (k, v) in parameters)
        {
            if (!k.Equals("id", StringComparison.OrdinalIgnoreCase))
                throw new GlpiException(
                    $"{itemtype}: param-{k} — GLPI item lookups take only param-id; use filter-{k} to match a column");
            id = v;
        }
        var live = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (string.IsNullOrWhiteSpace(id) && live.Count == 0)
            throw new GlpiException($"{itemtype}: param-id and every filter value are empty; refusing to list the whole inventory");

        await InitSessionAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(id))
        {
            string url = $"{Base}/{Uri.EscapeDataString(itemtype)}/{Uri.EscapeDataString(id.Trim())}?expand_dropdowns=true";
            using var doc = await GetJsonAsync(url, itemtype, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new GlpiException($"{itemtype} {id}: unexpected response shape");
            var row = ToRow(doc.RootElement);
            // a filter alongside param-id still has to hold
            if (!Matches(row, live))
                throw new GlpiException($"{itemtype} {id}: found, but does not match {Describe(live)}");
            return row;
        }

        var terms = new List<string> { "expand_dropdowns=true", "range=0-49" };
        foreach (var (k, v) in live)
            terms.Add($"searchText[{Uri.EscapeDataString(k)}]={Uri.EscapeDataString(v.Trim())}");
        string listUrl = $"{Base}/{Uri.EscapeDataString(itemtype)}?{string.Join("&", terms)}";
        using (var doc = await GetJsonAsync(listUrl, itemtype, ct).ConfigureAwait(false))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new GlpiException($"{itemtype}: unexpected response shape for a list");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var row = ToRow(item);
                if (Matches(row, live)) return row;
            }
        }
        throw new GlpiException($"{itemtype}: no item matches {Describe(live)}");
    }

    /// <summary>ALL items of a type (for a query-fed pick list): optional
    /// searchText filters (substring, as GLPI applies them — no client-side
    /// narrowing here), paged through GLPI's 206 Partial Content ranges up
    /// to <paramref name="max"/> rows. Dropdowns expanded as for single
    /// fetches.</summary>
    public async Task<List<Dictionary<string, JsonElement>>> FetchItemRowsAsync(
        string itemtype,
        IReadOnlyDictionary<string, string> filters,
        int max = 2000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemtype))
            throw new GlpiException("query= (the GLPI item type, e.g. Computer) is required");
        itemtype = itemtype.Trim().Trim('/');
        await InitSessionAsync(ct).ConfigureAwait(false);

        var rows = new List<Dictionary<string, JsonElement>>();
        const int page = 200;
        for (int start = 0; start < max; start += page)
        {
            var terms = new List<string>
            {
                "expand_dropdowns=true",
                $"range={start}-{Math.Min(start + page, max) - 1}",
            };
            foreach (var (k, v) in filters)
                if (!string.IsNullOrWhiteSpace(v))
                    terms.Add($"searchText[{Uri.EscapeDataString(k)}]={Uri.EscapeDataString(v.Trim())}");
            string url = $"{Base}/{Uri.EscapeDataString(itemtype)}?{string.Join("&", terms)}";
            int got = 0;
            using (var doc = await GetJsonAsync(url, itemtype, ct).ConfigureAwait(false))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new GlpiException($"{itemtype}: unexpected response shape for a list");
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    got++;   // counts toward paging even when skipped
                    // GLPI lists item TEMPLATES (is_template=1) and soft-deleted
                    // items (is_deleted=1) alongside real assets; neither is
                    // something to put a tag on
                    if (Flag(item, "is_template") || Flag(item, "is_deleted")) continue;
                    rows.Add(ToRow(item));
                }
            }
            if (got < page) break;   // last page
        }
        return rows;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string itemtype, CancellationToken ct)
    {
        using var req = Get(url);
        req.Headers.TryAddWithoutValidation("Session-Token", _session ?? "");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await ReadCappedAsync(resp, ct).ConfigureAwait(false);
        int code = (int)resp.StatusCode;
        // 206 = Partial Content: GLPI's normal answer for a ranged list
        if (code is not (200 or 206))
        {
            if (code == 401) _session = null;   // expired/invalid session: next call re-inits
            // the url carries no secrets (tokens travel in headers) — show
            // exactly what the server rejected
            throw new GlpiException($"{itemtype} returned HTTP {code} for {url}: {Trim(body, 300)}");
        }
        try { return JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            throw new GlpiException($"{itemtype}: response is not JSON ({ex.Message}): {Trim(body, 120)}");
        }
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetInt32(out int n) && n != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false,
        };

    /// <summary>Flatten one item: scalars as-is; an expanded dropdown that
    /// still arrives as an object keeps its raw JSON (the resolver shows
    /// it verbatim, which is at least debuggable).</summary>
    private static Dictionary<string, JsonElement> ToRow(JsonElement obj)
    {
        var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            // the legacy API returns strings as stored: HTML-ENCODED
            // ("D&#38;B &#62; Front Office", "&amp;", "&lt;") — decode once
            // here so labels and split= see the real text
            if (p.Value.ValueKind == JsonValueKind.String &&
                p.Value.GetString() is { } str && str.IndexOf('&') >= 0)
            {
                string dec = System.Net.WebUtility.HtmlDecode(str);
                using var d = JsonDocument.Parse(JsonSerializer.Serialize(dec));
                row[p.Name] = d.RootElement.Clone();
            }
            else row[p.Name] = p.Value.Clone();
        }
        // Virtual columns so ONE template spans item types: GLPI names the
        // model/type dropdowns per class (computermodels_id, monitormodels_id,
        // peripheraltypes_id, …). "model" / "type" alias whichever is
        // present; "location" is the leaf of the expanded location path
        // ("Site > Building > Room 12" → "Room 12") and "location_parent" the
        // level above it ("Building"); "manufacturer" aliases manufacturers_id.
        foreach (var (k, v) in row.ToList())
        {
            string kl = k.ToLowerInvariant();
            if (kl.EndsWith("models_id") && !row.ContainsKey("model")) row["model"] = v;
            else if (kl.EndsWith("types_id") && !row.ContainsKey("type")) row["type"] = v;
            else if (kl == "manufacturers_id" && !row.ContainsKey("manufacturer")) row["manufacturer"] = v;
            else if (kl == "locations_id" && !row.ContainsKey("location") && v.ValueKind == JsonValueKind.String)
            {
                // "Site > Building > Room 12": location = "Room 12",
                // location_parent = "Building" (empty for a one-level path)
                var levels = (v.GetString() ?? "").Split(" > ", StringSplitOptions.None);
                string leaf = levels[^1];
                string parent = levels.Length >= 2 ? levels[^2] : "";
                using var leafDoc = JsonDocument.Parse(JsonSerializer.Serialize(leaf));
                using var parentDoc = JsonDocument.Parse(JsonSerializer.Serialize(parent));
                row["location"] = leafDoc.RootElement.Clone();
                if (!row.ContainsKey("location_parent")) row["location_parent"] = parentDoc.RootElement.Clone();
            }
        }
        return row;
    }

    private static bool Matches(Dictionary<string, JsonElement> row,
                                List<KeyValuePair<string, string>> filters)
    {
        foreach (var (k, v) in filters)
        {
            if (!row.TryGetValue(k, out var cell)) return false;
            string s = cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : cell.ToString();
            if (!s.Trim().Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string Describe(List<KeyValuePair<string, string>> filters) =>
        string.Join(", ", filters.Select(f => $"{f.Key}='{f.Value}'"));

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        const int cap = 4 << 20;
        using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (ms.Length < cap)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, cap - ms.Length)), ct).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "..." : s;

    /// <summary>Releases the session (best effort) and the HttpClient. Safe
    /// to call from a UI thread: every await in this class uses
    /// ConfigureAwait(false), so the blocking wait cannot deadlock on the
    /// WinForms synchronization context.</summary>
    public void Dispose()
    {
        try { KillSessionAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch { }
        _http.Dispose();
    }
}

public sealed class GlpiException : Exception
{
    public GlpiException(string message) : base(message) { }
}
