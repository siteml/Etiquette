using System.Text;
using System.Text.Json;

namespace Etiq.Core;

/// <summary>
/// Append-only print log: one JSON line per event, monthly files
/// (printlog-yyyyMM.jsonl) in a configurable directory. This is the
/// series manifest v0 — each "spooled" event carries the resolved field
/// values EXACTLY as the renderer printed them, so a reprint (and later
/// the series pipeline's replay) is a verbatim re-run of that record.
/// Status follow-ups (completed / error / stuck) are separate events with
/// the same job id — the file is never rewritten.
///
/// Logging is best-effort by design: a full disk or locked file must
/// never block a label from printing.
/// </summary>
public static class PrintLog
{
    /// <summary>Log directory; null = logging off. The host (the editor)
    /// sets this at startup from its settings.</summary>
    public static string? Directory { get; set; }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>One printed label (event "spooled") or a later status
    /// update for the whole job ("completed" | "error" | "stuck").</summary>
    public static void Append(string job, string @event, string? template = null,
                              string? printer = null, int? page = null, int? pages = null,
                              IReadOnlyDictionary<string, string>? values = null,
                              string? detail = null)
    {
        if (Directory is not { Length: > 0 } dir) return;
        try
        {
            var rec = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ["job"] = job,
                ["event"] = @event,
                ["template"] = template,
                ["printer"] = printer,
                ["page"] = page,
                ["pages"] = pages,
                ["station"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["values"] = values,
                ["detail"] = detail,
            };
            string line = JsonSerializer.Serialize(rec, JsonOpts);
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"printlog-{DateTime.Now:yyyyMM}.jsonl"),
                                   line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* best-effort: never fail a print over its log */ }
    }

    /// <summary>All events since a cutoff, oldest first — reads every
    /// monthly file the window touches (the window is not capped to one
    /// month). Unparseable lines are skipped.</summary>
    public static List<Dictionary<string, JsonElement>> Read(DateTimeOffset since)
    {
        var result = new List<Dictionary<string, JsonElement>>();
        if (Directory is not { Length: > 0 } dir || !System.IO.Directory.Exists(dir)) return result;
        var files = System.IO.Directory.GetFiles(dir, "printlog-*.jsonl").OrderBy(f => f);
        string firstMonth = $"printlog-{since:yyyyMM}.jsonl";
        foreach (var f in files)
        {
            if (string.CompareOrdinal(Path.GetFileName(f), firstMonth) < 0) continue;
            IEnumerable<string> lines;
            try
            {
                // share-friendly read: another process may be appending
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch { continue; }
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    var rec = new Dictionary<string, JsonElement>();
                    foreach (var p in doc.RootElement.EnumerateObject()) rec[p.Name] = p.Value.Clone();
                    if (rec.TryGetValue("ts", out var ts) &&
                        DateTimeOffset.TryParse(ts.GetString(), out var when) && when < since)
                        continue;
                    result.Add(rec);
                }
                catch { /* skip torn/foreign lines */ }
            }
        }
        return result;
    }
}
