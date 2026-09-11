using System.Text.Json;

namespace Etiq.Editor.Core;

/// <summary>
/// The only owner of fetched rows and fetch failures (docs/data-flow.md
/// §5). Keyed by the fetch signature (source + dataset + target + committed
/// parameter values). A row, once fetched, is good across generations; a
/// FAILURE is remembered only for the generation it happened in, so the
/// next commit / Clear / Refresh retries it — a bad job number is never
/// remembered as bad once the operator re-commits.
/// </summary>
public sealed class SourceCache
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Generation, string Message)> _failures = new(StringComparer.Ordinal);
    private readonly int _cap;

    /// <summary>cap: rows kept before the cache is emptied (a station
    /// prints hundreds of distinct labels a day; keep it bounded).</summary>
    public SourceCache(int cap = 64) => _cap = cap;

    public int Generation { get; private set; }

    /// <summary>A new generation: failures become retryable, rows stay.</summary>
    public int Bump() => ++Generation;

    public bool TryGetRow(string sig, out Dictionary<string, JsonElement> row) =>
        _rows.TryGetValue(sig, out row!);

    /// <summary>True when this signature failed IN THE CURRENT generation —
    /// the one case that must not auto-retry (a resolve storm against a
    /// dead service).</summary>
    public bool IsFailed(string sig, out string message)
    {
        if (_failures.TryGetValue(sig, out var f) && f.Generation == Generation)
        {
            message = f.Message; return true;
        }
        message = ""; return false;
    }

    public void Store(string sig, Dictionary<string, JsonElement> row)
    {
        if (_rows.Count >= _cap) _rows.Clear();
        _rows[sig] = row;
        _failures.Remove(sig);
    }

    public void Fail(string sig, string message) => _failures[sig] = (Generation, message);

    /// <summary>Drop everything (Clear, dataset change, Refresh Preview).</summary>
    public void Clear()
    {
        _rows.Clear();
        _failures.Clear();
        Bump();
    }

    public int RowCount => _rows.Count;
}
