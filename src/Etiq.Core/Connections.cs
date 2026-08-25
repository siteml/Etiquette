using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Etiq.Core;

/// <summary>
/// One NAMED connection in the machine's connection store: type + shared
/// settings + any number of named DATASETS (Epicor calls them
/// "environments"; for a database it's the database name; for other
/// services whatever they parameterize). A dataset is a type-specific
/// override bag merged over the base settings — any subset of keys may be
/// overridden (a pilot with its own apiKey is just a bigger override).
///
/// Templates reference connections by NAME only. Which dataset is live is a
/// machine/session choice (see the editor's dataset picker), never a
/// template edit — unless an etiq:source pins one with dataset=.
///
/// Secret VALUES (password, apiKey, anything the UI marks secret) are
/// stored dpapi-wrapped via CredentialStore ("dpapi:..." strings) and
/// unwrapped only inside Resolved().
/// </summary>
public sealed class ConnectionDef
{
    public string Name { get; set; } = "";
    /// <summary>epicor | rest | (future: db, ...) — decides which settings
    /// keys matter and which client consumes them.</summary>
    public string Type { get; set; } = "epicor";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>dataset name → settings overrides (may be empty for a
    /// connection with a single fixed target).</summary>
    public Dictionary<string, Dictionary<string, string>> Datasets { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Dataset used when neither the session nor the machine picks
    /// one. Null with datasets present = caller must choose (error, not a
    /// silent guess).</summary>
    public string? DefaultDataset { get; set; }

    /// <summary>Settings keys whose values are secrets (dpapi-wrapped at
    /// rest, masked in UIs). Extend as new connection types appear.</summary>
    public static readonly string[] SecretKeys = { "password", "apiKey", "token", "userToken", "appToken" };
    public static bool IsSecretKey(string key) =>
        SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Effective settings for one dataset: base merged with the
    /// dataset's overrides, secrets unwrapped. datasetName null → the
    /// connection's default; unknown name → InvalidOperationException
    /// (NEVER silently fall back — a print against the wrong dataset is
    /// worse than no print).</summary>
    public Dictionary<string, string> Resolved(string? datasetName = null)
    {
        var merged = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase);
        string? ds = datasetName ?? DefaultDataset;
        if (ds is not null)
        {
            if (!Datasets.TryGetValue(ds, out var over))
                throw new InvalidOperationException(
                    $"connection '{Name}' has no dataset '{ds}'" +
                    (Datasets.Count > 0 ? $" (has: {string.Join(", ", Datasets.Keys)})" : " (has none)"));
            foreach (var (k, v) in over) merged[k] = v;
        }
        else if (Datasets.Count > 0)
            throw new InvalidOperationException(
                $"connection '{Name}' declares datasets ({string.Join(", ", Datasets.Keys)}) " +
                "but none was selected and no default is set");
        foreach (var k in merged.Keys.ToList())
            merged[k] = CredentialStore.Unprotect(merged[k]);
        return merged;
    }

    /// <summary>Build the EpicorClient config from resolved settings
    /// (Type == "epicor"): baseUrl, company, apiKey, username, password.</summary>
    public EpicorConfig ToEpicorConfig(string? datasetName = null)
    {
        var s = Resolved(datasetName);
        return new EpicorConfig
        {
            BaseUrl = s.GetValueOrDefault("baseUrl", ""),
            Company = s.GetValueOrDefault("company", ""),
            ApiKey = s.GetValueOrDefault("apiKey", ""),
            Username = s.GetValueOrDefault("username", ""),
            Password = s.GetValueOrDefault("password", ""),
        };
    }
}

/// <summary>Load/save the machine connection store (a JSON array of
/// ConnectionDef) and dpapi-wrap secrets on the way in.</summary>
public static class ConnectionsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<ConnectionDef> Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    public static List<ConnectionDef> Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<ConnectionDef>>(json, JsonOpts) ?? new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in list)
        {
            if (c.Name == "") throw new InvalidDataException("connection with empty name");
            if (!seen.Add(c.Name)) throw new InvalidDataException($"duplicate connection '{c.Name}'");
        }
        return list;
    }

    /// <summary>Protect any plaintext secret values in place (freshly typed
    /// ones next to already-wrapped ones re-encrypt only the new).</summary>
    public static void ProtectSecrets(IEnumerable<ConnectionDef> list)
    {
        foreach (var c in list)
        {
            WrapIn(c.Settings);
            foreach (var ds in c.Datasets.Values) WrapIn(ds);
        }
        static void WrapIn(Dictionary<string, string> d)
        {
            foreach (var k in d.Keys.ToList())
                if (ConnectionDef.IsSecretKey(k) && CredentialStore.SecretPresent(d[k]))
                    d[k] = CredentialStore.Protect(d[k]);
        }
    }

    public static void Save(string path, List<ConnectionDef> list)
    {
        ProtectSecrets(list);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
    }
}

/// <summary>
/// Password-protected connections bundle (*.etiqcreds) for provisioning
/// machines: the SAME JSON as the store, but with secrets in PLAINTEXT
/// inside the encrypted envelope (DPAPI blobs are machine-bound and would
/// be garbage elsewhere). Export from the designer machine, convey the
/// password out-of-band, import on each station — where secrets are
/// immediately re-wrapped for THAT machine and the bundle is discarded.
///
/// Format: "ETQC1" magic ∥ 16-byte salt ∥ 12-byte nonce ∥ AES-256-GCM
/// ciphertext ∥ 16-byte tag. Key = PBKDF2-SHA256(password, salt, 200k).
/// Standard primitives only — nothing invented.
/// </summary>
public static class CredsBundle
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ETQC1");
    private const int Iterations = 200_000;

    public static byte[] Export(List<ConnectionDef> list, string password)
    {
        // outgoing secrets must be portable: unwrap any dpapi values
        var portable = ConnectionsStore.Parse(JsonSerializer.Serialize(list));
        foreach (var c in portable)
        {
            Unwrap(c.Settings);
            foreach (var ds in c.Datasets.Values) Unwrap(ds);
        }
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(portable));

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, plain, cipher, tag);

        using var ms = new MemoryStream();
        ms.Write(Magic); ms.Write(salt); ms.Write(nonce); ms.Write(cipher); ms.Write(tag);
        return ms.ToArray();

        static void Unwrap(Dictionary<string, string> d)
        {
            foreach (var k in d.Keys.ToList()) d[k] = CredentialStore.Unprotect(d[k]);
        }
    }

    /// <summary>Decrypt a bundle. Secrets come back PLAINTEXT — pass the
    /// result straight to ConnectionsStore.Save so they get machine-wrapped.
    /// Wrong password (or tampering) throws CryptographicException.</summary>
    public static List<ConnectionDef> Import(byte[] bundle, string password)
    {
        if (bundle.Length < Magic.Length + 16 + 12 + 16 ||
            !bundle.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("not an Etiquette connections bundle");
        var span = bundle.AsSpan(Magic.Length);
        byte[] salt = span[..16].ToArray();
        byte[] nonce = span[16..28].ToArray();
        byte[] tag = span[^16..].ToArray();
        byte[] cipher = span[28..^16].ToArray();
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, 16))
            gcm.Decrypt(nonce, cipher, tag, plain);
        return ConnectionsStore.Parse(Encoding.UTF8.GetString(plain));
    }
}
