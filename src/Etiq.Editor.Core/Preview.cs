using Etiq.Core;

namespace Etiq.Editor.Core;

/// <summary>
/// The ONE state the canvas reads (docs/data-flow.md §2): every declared
/// field either resolved to a value or carries an error — never both,
/// never neither. Produced whole by <see cref="Preview.Produce"/> and
/// swapped in atomically; nothing edits it afterwards.
/// </summary>
public sealed class PreviewSnapshot
{
    public static readonly PreviewSnapshot Empty = new(
        new Dictionary<string, string>(), new Dictionary<string, string>(), null, 0);

    public PreviewSnapshot(IReadOnlyDictionary<string, string> values,
                           IReadOnlyDictionary<string, string> errors,
                           IReadOnlyDictionary<string, string>? redacted,
                           int generation)
    {
        Values = values; Errors = errors; Redacted = redacted; Generation = generation;
    }

    /// <summary>field → resolved value (fields that resolved).</summary>
    public IReadOnlyDictionary<string, string> Values { get; }
    /// <summary>field → message (fields that did NOT resolve). Print is
    /// refused while any bound field is here.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; }
    /// <summary>The same resolve with stand-ins fed IN (View → Redact
    /// Sensitive); null when redaction is off or nothing is sensitive.
    /// Composes over a sensitive field are redacted inside.</summary>
    public IReadOnlyDictionary<string, string>? Redacted { get; }
    /// <summary>Bumped by commit / Clear / Refresh / dataset change; the
    /// fetch cache retries failures recorded in an older generation.</summary>
    public int Generation { get; }

    public bool HasErrors => Errors.Count > 0;

    /// <summary>What a bound element draws: the (redacted) value, or null
    /// when the field errored / is unknown — the canvas draws nothing.</summary>
    public string? Display(string field, bool redact) =>
        redact && Redacted is not null && Redacted.TryGetValue(field, out var r) ? r
        : Values.TryGetValue(field, out var v) ? v : null;
}

/// <summary>
/// The single producer of <see cref="PreviewSnapshot"/>s. Pure: the host
/// supplies a context factory (committed inputs, fetch provider, counters),
/// this resolves every declared field ON ITS OWN so one failing field
/// (a `required` prompt left empty — required for PRINTING, not for
/// showing what can be pieced together) never hides the rest.
/// </summary>
public static class Preview
{
    /// <summary>Resolve every field of `template`. `makeContext(substitutes)`
    /// builds the resolve context, with `substitutes` fed into it as
    /// ResolveContext.Substitutes (null = plain). `standIns` non-empty → a
    /// second, redacted resolve is produced through the same factory.</summary>
    public static PreviewSnapshot Produce(EtiqTemplate template,
                                          Func<IReadOnlyDictionary<string, string>?, ResolveContext> makeContext,
                                          IReadOnlyDictionary<string, string>? standIns,
                                          int generation)
    {
        var (values, errors) = ResolveEach(template, makeContext(null));
        IReadOnlyDictionary<string, string>? redacted = null;
        if (standIns is { Count: > 0 })
        {
            // errors of the redacted pass are the same fields (a stand-in
            // never fails); the plain pass's errors are the ones reported
            var (rv, _) = ResolveEach(template, makeContext(standIns));
            redacted = rv;
        }
        return new PreviewSnapshot(values, errors, redacted, generation);
    }

    /// <summary>Per-field resolve: one resolver (memo shares compose /
    /// fetch work across fields), each field caught on its own.</summary>
    public static (Dictionary<string, string> Values, Dictionary<string, string> Errors)
        ResolveEach(EtiqTemplate template, ResolveContext ctx)
    {
        var resolver = new FieldResolver(template, ctx);
        var values = new Dictionary<string, string>();
        var errors = new Dictionary<string, string>();
        foreach (var f in template.Fields)
        {
            try { values[f.Name] = resolver.Resolve(f.Name); }
            catch (ResolveException ex) { errors[f.Name] = Strip(ex, f.Name); }
            catch (Exception ex) { errors[f.Name] = ex.Message; }   // provider bug: still per field
        }
        return (values, errors);

        // "field 'X': message" → "message" for X's own row; a failure that
        // surfaced through another field ("field 'Key': waiting for Job")
        // keeps its prefix so the operator sees where it came from
        static string Strip(ResolveException ex, string self)
        {
            string p = $"field '{self}': ";
            return ex.Message.StartsWith(p, StringComparison.Ordinal) ? ex.Message[p.Length..] : ex.Message;
        }
    }
}
