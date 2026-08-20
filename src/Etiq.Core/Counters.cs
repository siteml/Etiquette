using System.Text;
using System.Text.Json;

namespace Etiq.Core;

/// <summary>
/// A serial-number counter service (docs/convention.md SERIALIZATION,
/// docs/counters.md for the Epicor setup). Counters are central and atomic:
/// templates never store the current value; stations never keep local state.
/// Keys default coarse (per customer) — split finer only on documented
/// customer spec (HANDOFF decision #4).
/// </summary>
public interface ICounterProvider
{
    /// <summary>
    /// Atomically reserve <paramref name="count"/> consecutive values of the
    /// named counter. Returns the FIRST reserved value; the caller owns
    /// [first, first+count-1]. Reserving a block up front is what makes
    /// multi-label print jobs gap-safe under concurrency.
    /// </summary>
    Task<long> ReserveAsync(string counter, int count = 1, CancellationToken ct = default);

    /// <summary>Current value without incrementing (dashboards, prefill display).</summary>
    Task<long> PeekAsync(string counter, CancellationToken ct = default);
}

/// <summary>
/// Formats a raw counter value per a template's serial field declaration:
/// format="000000" zero-padding, alphabet= for base-N schemes
/// (e.g. alphabet="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" for the base-36
/// serials seen in the .btw corpus serialization records).
/// </summary>
public static class SerialFormat
{
    public static string Format(long value, string? format = null, string? alphabet = null)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        string s;
        if (!string.IsNullOrEmpty(alphabet) && alphabet.Length > 1)
        {
            int radix = alphabet.Length;
            var sb = new StringBuilder();
            long v = value;
            do { sb.Insert(0, alphabet[(int)(v % radix)]); v /= radix; } while (v > 0);
            s = sb.ToString();
        }
        else
            s = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrEmpty(format))
            s = s.PadLeft(format.Length, format.Length > 0 && char.IsLetterOrDigit(format[0])
                ? format[0] == '0' ? '0' : format[0] : '0');
        return s;
    }
}

/// <summary>
/// Reference ICounterProvider: an Epicor Kinetic Function doing an atomic
/// UD-table read-increment server-side (duplicates impossible across
/// stations). Function library/name and the UD-table shape are documented
/// click-by-click in docs/counters.md.
/// </summary>
public sealed class EpicorCounterProvider : ICounterProvider
{
    public const string DefaultLibrary = "EtiqCounters";

    private readonly EpicorClient _client;
    private readonly string _library;

    public EpicorCounterProvider(EpicorClient client, string library = DefaultLibrary)
    {
        _client = client;
        _library = library;
    }

    public async Task<long> ReserveAsync(string counter, int count = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(counter))
            throw new ArgumentException("counter key is empty", nameof(counter));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var result = await _client.CallFunctionAsync(_library, "NextSerial",
            new { counter, count }, ct);
        return ReadValue(result, "next");
    }

    public async Task<long> PeekAsync(string counter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(counter))
            throw new ArgumentException("counter key is empty", nameof(counter));
        var result = await _client.CallFunctionAsync(_library, "PeekSerial",
            new { counter }, ct);
        return ReadValue(result, "current");
    }

    private static long ReadValue(JsonElement result, string prop)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n))
            return n;
        throw new EpicorException(
            $"counter function response missing numeric '{prop}': {result}");
    }
}

/// <summary>
/// File-backed provider for development/testing WITHOUT Epicor access.
/// Atomic only within one machine (lock file) — explicitly NOT for
/// production multi-station use; that's the whole reason counters are
/// centralized in Epicor (HANDOFF decision #4).
/// </summary>
public sealed class LocalFileCounterProvider : ICounterProvider
{
    private readonly string _path;
    public LocalFileCounterProvider(string path) => _path = path;

    public Task<long> ReserveAsync(string counter, int count = 1, CancellationToken ct = default)
        => Task.FromResult(Advance(counter, count));

    public Task<long> PeekAsync(string counter, CancellationToken ct = default)
        => Task.FromResult(Advance(counter, 0) ); // 0 = read only

    private long Advance(string counter, int count)
    {
        if (string.IsNullOrWhiteSpace(counter))
            throw new ArgumentException("counter key is empty", nameof(counter));
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                      FileShare.None); // FileShare.None = cross-process lock
        Dictionary<string, long> state;
        if (fs.Length > 0)
        {
            state = JsonSerializer.Deserialize<Dictionary<string, long>>(fs)
                    ?? new Dictionary<string, long>();
        }
        else state = new Dictionary<string, long>();
        long current = state.GetValueOrDefault(counter, 0);
        if (count == 0) return current + 1; // next value that WOULD be issued
        long first = current + 1;
        state[counter] = current + count;
        fs.SetLength(0);
        fs.Position = 0;
        JsonSerializer.Serialize(fs, state, new JsonSerializerOptions { WriteIndented = true });
        return first;
    }
}
