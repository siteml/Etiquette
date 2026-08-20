namespace Etiq.Core;

/// <summary>One fully resolved label, ready for the render/print stage —
/// downstream of this, nothing knows fields exist.</summary>
public sealed record ResolvedLabel(
    int LabelIndex,          // 1-based across the whole job
    int RecordIndex,         // 1-based record number
    int CopyIndex,           // 1-based copy within the record
    IReadOnlyDictionary<string, string> Record,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Batch-merge job expansion (roadmap Phase 3, gLabels merge model):
/// job = template + record set + copies-per-record → one resolved label
/// per record × copy. Records are column→value rows (BAQ result rows or
/// Csv.Read output); a record's columns feed the template's `epicor`
/// fields, so a template designed against a BAQ merges from a CSV with the
/// same column names unchanged.
///
/// Per-label semantics come from using a fresh FieldResolver per label:
/// serial fields draw a new counter value on every label (including every
/// copy — each physical label gets its own serial), while prompts are
/// asked once per JOB and shared. The reserved autos labelindex /
/// labelcount / copyindex resolve per label.
/// </summary>
public static class BatchRunner
{
    /// <summary>A single-record job (the classic prompt-driven print).</summary>
    public static List<ResolvedLabel> RunSingle(EtiqTemplate template,
        ResolveContext ctx, int copies = 1) =>
        Run(template, new() { new(StringComparer.OrdinalIgnoreCase) }, ctx, copies);

    /// <summary>Expand and resolve the whole job. Throws ResolveException
    /// (with record/label context prepended) on the first blocking field —
    /// jobs are all-or-nothing so a serial is never burned on half a batch
    /// silently. Counters ARE consumed for labels resolved before the
    /// failure; callers that need stronger guarantees should dry-run first
    /// with a local counter provider.</summary>
    public static List<ResolvedLabel> Run(EtiqTemplate template,
        List<Dictionary<string, string>> records, ResolveContext shared,
        int copiesPerRecord = 1)
    {
        if (copiesPerRecord < 1) throw new ArgumentOutOfRangeException(nameof(copiesPerRecord));
        int total = records.Count * copiesPerRecord;
        var outp = new List<ResolvedLabel>(total);
        int labelIndex = 0;
        for (int r = 0; r < records.Count; r++)
        {
            var record = records[r];
            for (int c = 1; c <= copiesPerRecord; c++)
            {
                labelIndex++;
                var ctx = new ResolveContext
                {
                    PromptValues = shared.PromptValues,
                    ListSelections = shared.ListSelections, // --choose / pick-list rows
                    Counters = shared.Counters,
                    Rest = shared.Rest,
                    Cache = shared.Cache,
                    OnCachedValueUsed = shared.OnCachedValueUsed,
                    Now = shared.Now,
                    // record columns feed epicor fields; an explicit provider
                    // (live BAQ) wins when the record lacks the column
                    EpicorColumn = col =>
                        record.TryGetValue(col, out var v) ? v
                        : shared.EpicorColumn?.Invoke(col),
                    AutoValues = new(shared.AutoValues, StringComparer.OrdinalIgnoreCase)
                    {
                        ["labelindex"] = labelIndex.ToString(),
                        ["labelcount"] = total.ToString(),
                        ["copyindex"] = c.ToString(),
                        ["recordindex"] = (r + 1).ToString(),
                    },
                };
                try
                {
                    var fields = new FieldResolver(template, ctx).ResolveAll();
                    outp.Add(new ResolvedLabel(labelIndex, r + 1, c, record, fields));
                }
                catch (ResolveException ex)
                {
                    throw new ResolveException(ex.Field,
                        $"label {labelIndex}/{total} (record {r + 1}, copy {c}): {ex.Message}");
                }
            }
        }
        return outp;
    }
}
