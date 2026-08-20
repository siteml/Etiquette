using System.Text.Json;

namespace Etiq.Core;

/// <summary>
/// A named REST connection profile (convention 0.2 `source="rest"`).
/// Profiles are engine-side configuration data files — auth and base URLs
/// never appear in templates. Secrets may be DPAPI-wrapped ("dpapi:...");
/// they pass through CredentialStore.Unprotect at use time.
///
/// Kinds:
///   none    — anonymous GET
///   headers — static headers (values may be dpapi-wrapped)
///   basic   — HTTP Basic (username + passwordSecret)
///   glpi    — GLPI REST: initSession with App-Token + user_token, then
///             Session-Token on every call
/// </summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "none";
    public string BaseUrl { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Username { get; set; }
    public string? PasswordSecret { get; set; }
    public string? AppTokenSecret { get; set; }     // glpi
    public string? UserTokenSecret { get; set; }    // glpi
    public int TimeoutSeconds { get; set; } = 10;
}

public static class ConnectionProfiles
{
    /// <summary>Load profiles from a JSON file: an array of profile objects.</summary>
    public static Dictionary<string, ConnectionProfile> Load(string path) =>
        Parse(File.ReadAllText(path));

    public static Dictionary<string, ConnectionProfile> Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("connection profile file is not a JSON array");
        var byName = new Dictionary<string, ConnectionProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
        {
            if (p.Name == "") throw new InvalidDataException("profile with empty name");
            if (p.BaseUrl == "") throw new InvalidDataException($"profile '{p.Name}': baseUrl required");
            if (!byName.TryAdd(p.Name, p))
                throw new InvalidDataException($"duplicate profile '{p.Name}'");
        }
        return byName;
    }
}

/// <summary>Evaluates convention 0.2 `pick` selectors — a dotted path with
/// optional [index], deliberately NOT JSONPath.</summary>
public static class JsonPick
{
    public static string? Evaluate(JsonElement root, string pick)
    {
        JsonElement cur = root;
        foreach (var raw in pick.Split('.'))
        {
            string token = raw;
            int? index = null;
            int br = raw.IndexOf('[');
            if (br >= 0 && raw.EndsWith("]") &&
                int.TryParse(raw[(br + 1)..^1], out int idx))
            {
                token = raw[..br];
                index = idx;
            }
            if (token != "")
            {
                if (cur.ValueKind != JsonValueKind.Object ||
                    !cur.TryGetProperty(token, out cur)) return null;
            }
            if (index is int i)
            {
                if (cur.ValueKind != JsonValueKind.Array || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object or JsonValueKind.Array => cur.GetRawText(),
            _ => cur.ToString(),
        };
    }
}

/// <summary>
/// Fetches values for `source="rest"` fields through a connection profile.
/// One instance per profile per job; the GLPI session token is cached for
/// the client's lifetime. HttpMessageHandler is injectable for tests.
/// Wire into FieldResolver:
///   ctx.Rest = (conn, query, pick) => clients[conn].Fetch(query, pick)
/// </summary>
public sealed class RestClient : IDisposable
{
    private readonly ConnectionProfile _p;
    private readonly HttpClient _http;
    private string? _glpiSession;

    public RestClient(ConnectionProfile profile, HttpMessageHandler? handler = null)
    {
        _p = profile;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
    }

    public string? Fetch(string? query, string pick) =>
        FetchAsync(query, pick).GetAwaiter().GetResult();

    public async Task<string?> FetchAsync(string? query, string pick, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Combine(_p.BaseUrl, query ?? ""));
        await AddAuthAsync(req, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{_p.Name}: HTTP {(int)resp.StatusCode} for '{query}'");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return JsonPick.Evaluate(doc.RootElement, pick);
    }

    private async Task AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        foreach (var (k, v) in _p.Headers)
            req.Headers.TryAddWithoutValidation(k, CredentialStore.Unprotect(v));

        switch (_p.Kind)
        {
            case "basic":
                var raw = $"{_p.Username}:{CredentialStore.Unprotect(_p.PasswordSecret ?? "")}";
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)));
                break;
            case "glpi":
                string app = CredentialStore.Unprotect(_p.AppTokenSecret ?? "");
                req.Headers.TryAddWithoutValidation("App-Token", app);
                _glpiSession ??= await GlpiInitSessionAsync(app, ct);
                req.Headers.TryAddWithoutValidation("Session-Token", _glpiSession);
                break;
        }
    }

    private async Task<string> GlpiInitSessionAsync(string appToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Combine(_p.BaseUrl, "initSession"));
        req.Headers.TryAddWithoutValidation("App-Token", appToken);
        req.Headers.TryAddWithoutValidation("Authorization",
            $"user_token {CredentialStore.Unprotect(_p.UserTokenSecret ?? "")}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{_p.Name}: initSession HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("session_token").GetString()
            ?? throw new HttpRequestException($"{_p.Name}: initSession returned no session_token");
    }

    private static string Combine(string baseUrl, string rel) =>
        rel == "" ? baseUrl
        : baseUrl.EndsWith("/") || rel.StartsWith("/") ? baseUrl + rel.TrimStart('/')
        : baseUrl + "/" + rel;

    public void Dispose() => _http.Dispose();
}
