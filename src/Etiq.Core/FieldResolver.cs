using System.Globalization;

namespace Etiq.Core;

/// <summary>Thrown when resolution must block printing (convention 0.2:
/// required-empty, on-fail=block, map non-match with no default,
/// reserved/unimplemented source kinds).</summary>
public sealed class ResolveException : Exception
{
    public string Field { get; }
    public ResolveException(string field, string message)
        : base($"field '{field}': {message}") => Field = field;
}

/// <summary>External data the resolver may pull from, all injectable so the
/// resolver itself stays pure logic (testable offline). Any provider left
/// null makes fields of that kind fail per their on-fail policy.</summary>
public sealed class ResolveContext
{
    /// <summary>Operator answers for prompt fields, keyed by field name.</summary>
    public Dictionary<string, string> PromptValues { get; init; } = new();

    /// <summary>Chosen row per embedded pick list: list name → key value.
    /// ONE selection per list — every field bound to the list follows it
    /// (that's the "set" behavior; independent picks = separate lists).</summary>
    public Dictionary<string, string> ListSelections { get; init; } = new();

    /// <summary>Counter service for serial fields (Counters.cs).</summary>
    public ICounterProvider? Counters { get; init; }

    /// <summary>Remote lookup for epicor fields: column name → value.
    /// Legacy single-source path (engine config names the one BAQ).</summary>
    public Func<string, string?>? EpicorColumn { get; init; }

    /// <summary>Remote lookup for fields bound to a DECLARED etiq:source:
    /// (source name, column name) → value. The provider owns fetching and
    /// caching the source's row (one fetch per source per label) and
    /// resolving its param-/filter- field references.</summary>
    public Func<string, string, string?>? SourceColumn { get; init; }

    /// <summary>Remote lookup for rest fields: (connection, query, pick) → value.</summary>
    public Func<string, string?, string, string?>? Rest { get; init; }

    /// <summary>Last-good-value cache for on-fail="cached" (key = field name).
    /// The engine persists this between jobs; tests inject a dictionary.</summary>
    public IDictionary<string, string>? Cache { get; init; }

    /// <summary>Called when a cached value is substituted, for the job log.</summary>
    public Action<string>? OnCachedValueUsed { get; init; }

    /// <summary>Print-time clock for auto date/time fields.</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Values for auto kinds beyond date/time (station, user,
    /// labelindex, labelcount, copyindex...), keyed by the value= word.</summary>
    public Dictionary<string, string> AutoValues { get; init; } = new();
}

/// <summary>
/// Resolves every declared field of a template to its final string — the
/// single place bindings are evaluated (convention 0.2 "Composed fields":
/// nothing downstream of this knows fields exist).
///
/// Per-label semantics: create one Resolver per printed label. Serial
/// counters are memoized per instance, so any number of references to the
/// same serial field consume exactly one counter reservation.
/// </summary>
public sealed class FieldResolver
{
    private readonly EtiqTemplate _t;
    private readonly ResolveContext _ctx;
    private readonly Dictionary<string, EtiqTemplate.Field> _fields = new();
    private readonly Dictionary<string, EtiqTemplate.MapDef> _maps = new();
    private readonly Dictionary<string, EtiqTemplate.ListDef> _lists = new();
    private readonly Dictionary<string, string> _memo = new();   // per-label
    private readonly HashSet<string> _resolving = new();         // cycle guard

    public FieldResolver(EtiqTemplate template, ResolveContext ctx)
    {
        _t = template;
        _ctx = ctx;
        foreach (var f in template.Fields)
            _fields.TryAdd(f.Name, f);
        foreach (var m in template.Maps)
            _maps.TryAdd(m.Name, m);
        foreach (var l in template.Lists)
            _lists.TryAdd(l.Name, l);
    }

    /// <summary>Resolve every declared field. Throws ResolveException on the
    /// first blocking condition.</summary>
    public Dictionary<string, string> ResolveAll()
    {
        var outp = new Dictionary<string, string>();
        foreach (var f in _t.Fields)
            outp[f.Name] = Resolve(f.Name);
        return outp;
    }

    /// <summary>Resolve one field by name (memoized per label).</summary>
    public string Resolve(string name)
    {
        if (_memo.TryGetValue(name, out var hit)) return hit;
        if (!_fields.TryGetValue(name, out var f))
            throw new ResolveException(name, "not declared");
        if (!_resolving.Add(name))
            throw new ResolveException(name, "circular reference (field depends on itself)");
        try
        {

        string value = f.Source switch
        {
            "fixed" => f.Value ?? "",
            "prompt" => _ctx.PromptValues.GetValueOrDefault(name, ""),
            "auto" => ResolveAuto(f),
            "serial" => ResolveSerial(f),
            // override="true": the operator's typed value beats the pull;
            // empty entry = fetch as usual
            "epicor" when f.Override &&
                          _ctx.PromptValues.GetValueOrDefault(f.Name, "") is { Length: > 0 } typed
                => typed,
            "epicor" when f.From is not null => ResolveRemote(f, () =>
                _ctx.SourceColumn?.Invoke(f.From, f.Column ?? "")
                    ?? throw new InvalidOperationException($"no provider for source '{f.From}'")),
            "epicor" => ResolveRemote(f, () =>
                _ctx.EpicorColumn?.Invoke(f.Column ?? "")
                    ?? throw new InvalidOperationException("no Epicor provider/row")),
            "rest" => ResolveRemote(f, () =>
                _ctx.Rest?.Invoke(f.Connection ?? "", f.Query, f.Pick ?? "")
                    ?? throw new InvalidOperationException("no REST provider")),
            "compose" => ResolveCompose(f),
            "list" => ResolveList(f),
            "db" or "file" or "device" =>
                throw new ResolveException(name, $"source '{f.Source}' is reserved — not yet implemented"),
            _ => throw new ResolveException(name, $"unknown source '{f.Source}'"),
        };

        value = ApplyIfEmptyAndRequired(f, value);
        _memo[name] = value;
        return value;

        }
        finally { _resolving.Remove(name); }
    }

    // --- source kinds ---

    private string ResolveAuto(EtiqTemplate.Field f)
    {
        string spec = f.Value ?? "";
        if (spec.StartsWith("date:"))
            return _ctx.Now.ToString(spec["date:".Length..], CultureInfo.InvariantCulture);
        if (spec.StartsWith("time:"))
            return _ctx.Now.ToString(spec["time:".Length..], CultureInfo.InvariantCulture);
        return _ctx.AutoValues.GetValueOrDefault(spec, "");
    }

    private string ResolveSerial(EtiqTemplate.Field f)
    {
        if (_ctx.Counters is null)
            throw new ResolveException(f.Name, "no counter provider configured");
        long v = _ctx.Counters.ReserveAsync(f.Counter ?? "").GetAwaiter().GetResult();
        string? format = (string?)f.El.Attribute("format");
        string? alphabet = (string?)f.El.Attribute("alphabet");
        return SerialFormat.Format(v, format, alphabet);
    }

    private string ResolveRemote(EtiqTemplate.Field f, Func<string?> fetch)
    {
        string onFail = f.OnFail ?? "block";
        try
        {
            string? v = fetch();
            if (v is null)
                throw new InvalidOperationException("value missing from response");
            if (_ctx.Cache is not null) _ctx.Cache[f.Name] = v;
            return v;
        }
        catch (Exception ex) when (ex is not ResolveException)
        {
            if (onFail == "cached" && _ctx.Cache is not null &&
                _ctx.Cache.TryGetValue(f.Name, out var cached))
            {
                _ctx.OnCachedValueUsed?.Invoke(f.Name);
                return cached;
            }
            if (onFail.StartsWith("use:"))
                return onFail["use:".Length..];
            throw new ResolveException(f.Name, $"remote fetch failed ({ex.Message})" +
                (onFail == "cached" ? "; no cached value available" : ""));
        }
    }

    private string ResolveList(EtiqTemplate.Field f)
    {
        var list = _lists.GetValueOrDefault(f.ListRef ?? "")
            ?? throw new ResolveException(f.Name, $"list '{f.ListRef}' not declared");
        string? keyValue = _ctx.ListSelections.GetValueOrDefault(list.Name) ?? list.Default;
        if (keyValue is null)
            throw new ResolveException(f.Name,
                $"no row selected for list '{list.Name}' and the list has no default");
        var row = list.RowByKey(keyValue)
            ?? throw new ResolveException(f.Name,
                $"list '{list.Name}' has no row with {list.Key}='{keyValue}'");
        return row.GetValueOrDefault(f.Column ?? "", "");
    }

    /// <summary>Variant compose: pick the segment list whose variant
    /// matches the switch-on field's resolved value — exact `when` beats
    /// `prefix` (first in document order within each class), else the
    /// default variant; no match and no default blocks, like maps.
    /// when= may list several values separated by "|" ("DE|AT|CH"), so one
    /// variant covers a whole format group without a tab/block per value.</summary>
    private List<EtiqTemplate.Seg> PickSegs(EtiqTemplate.Field f)
    {
        if (f.Variants.Count == 0) return f.Segs;
        string key = f.SwitchOn is { } sw
            ? Resolve(sw)
            : throw new ResolveException(f.Name, "variants require switch-on=");
        foreach (var v in f.Variants)
            if (v.When is not null && v.When.Split('|').Contains(key)) return v.Segs;
        foreach (var v in f.Variants)
            if (v.Prefix is not null && key.StartsWith(v.Prefix, StringComparison.Ordinal))
                return v.Segs;
        foreach (var v in f.Variants)
            if (v.IsDefault) return v.Segs;
        throw new ResolveException(f.Name,
            $"no variant matches {f.SwitchOn}='{key}' and there is no default variant");
    }

    private string ResolveCompose(EtiqTemplate.Field f)
    {
        var sb = new System.Text.StringBuilder();
        int lineStart = 0;   // index in sb where the current line begins
        int i = 0;
        foreach (var s in PickSegs(f))
        {
            i++;
            if (s.Newline)
            {
                sb.Append('\n');
                lineStart = sb.Length;
                continue;
            }
            string part;
            if (s.Value is not null)
                part = s.Value;
            else
            {
                string refName = s.Ref ?? throw new ResolveException(
                    f.Name, $"seg #{i} has neither value= nor ref= (nor newline=)");
                var target = _fields.GetValueOrDefault(refName)
                    ?? throw new ResolveException(f.Name, $"seg #{i} ref '{refName}' not declared");
                if (target.Source == "compose")
                    throw new ResolveException(f.Name, $"seg #{i} ref '{refName}' is itself compose (one level only)");
                part = Resolve(refName);
            }
            part = ApplySegTransforms(f.Name, i, s, part);
            // smart separator: only between two non-empty pieces of one line
            if (s.Sep is not null && part.Length > 0 && sb.Length > lineStart)
                sb.Append(s.Sep);
            sb.Append(part);
        }
        if (!f.CollapseBlankLines) return sb.ToString();
        var lines = sb.ToString().Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", lines);
    }

    // --- transforms, fixed order: start/len → format → case → pad → map ---

    private string ApplySegTransforms(string field, int idx, EtiqTemplate.Seg s, string v)
    {
        if (s.Start is not null || s.Len is not null)
        {
            int start = (int)(EtiqTemplate.ParseNum(s.Start) ?? 0);
            start = Math.Clamp(start, 0, v.Length);
            int len = (int)(EtiqTemplate.ParseNum(s.Len) ?? (v.Length - start));
            len = Math.Clamp(len, 0, v.Length - start);
            v = v.Substring(start, len);
        }
        if (s.Format is not null)
            v = FormatValue(v, s.Format, _ctx.Now);
        v = ApplyCase(v, s.Case);
        if (s.Pad is not null)
        {
            var parts = s.Pad.Split(':');
            if (parts.Length == 3 && parts[1].Length == 1 &&
                int.TryParse(parts[2], out int width))
                v = parts[0] == "left" ? v.PadLeft(width, parts[1][0])
                                       : v.PadRight(width, parts[1][0]);
        }
        if (s.Map is not null)
        {
            if (s.IfEmpty is not null && string.IsNullOrWhiteSpace(v))
                v = s.IfEmpty;   // seg-level if-empty applies before map lookup
            v = ApplyMap(field, idx, s, v);
        }
        else if (s.IfEmpty is not null && string.IsNullOrWhiteSpace(v))
            v = s.IfEmpty;
        return v;
    }

    private string ApplyMap(string field, int idx, EtiqTemplate.Seg s, string v)
    {
        var map = _maps.GetValueOrDefault(s.Map!)
            ?? throw new ResolveException(field, $"seg #{idx} map '{s.Map}' not declared");

        // exact rows win over prefix rows, then document order
        foreach (var w in map.Whens)
            if ((string?)w.Attribute("from") == v)
                return (string?)w.Attribute("to") ?? "";
        foreach (var w in map.Whens)
        {
            string? prefix = (string?)w.Attribute("prefix");
            if (prefix is not null && v.StartsWith(prefix, StringComparison.Ordinal))
                return (string?)w.Attribute("to") ?? "";
        }
        // seg default wins over map default
        string? dflt = s.Default ?? map.Default;
        return dflt ?? throw new ResolveException(field,
            $"seg #{idx}: '{v}' matched no row of map '{s.Map}' and no default is defined");
    }

    private string ApplyIfEmptyAndRequired(EtiqTemplate.Field f, string v)
    {
        // field-level case normalization (selectable per field — never assumed)
        if (f.Source != "compose")
            v = ApplyCase(v, f.Case);
        if (string.IsNullOrWhiteSpace(v) && f.IfEmpty is not null)
            v = f.IfEmpty;
        if (string.IsNullOrWhiteSpace(v) && f.Required == "true")
            throw new ResolveException(f.Name, "required field resolved empty");
        return v;
    }

    /// <summary>Case normalization shared by field-level and per-segment
    /// case=. Enum: normal (explicit no-op) | upper | lower | title.
    /// Absent = untouched — casing is never assumed.</summary>
    public static string ApplyCase(string v, string? kind) => kind switch
    {
        "upper" => v.ToUpperInvariant(),
        "lower" => v.ToLowerInvariant(),
        "title" => EnglishTitleCase(v),
        _ => v,   // null or "normal"
    };

    private static readonly HashSet<string> TitleSmallWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet",
        "as", "at", "by", "in", "of", "off", "on", "per", "to", "up",
        "via", "vs", "v",
    };

    /// <summary>English title case (deterministic, documented in the
    /// convention): input is lowercased, then every word is capitalized
    /// EXCEPT the standard small words (articles, conjunctions, short
    /// prepositions) — which stay lower unless they are the first or last
    /// word, or follow sentence punctuation (: ; . ! ?). Hyphen and slash
    /// compounds capitalize each part ("first-class" → "First-Class").
    /// Note: normalization is total — acronym preservation is impossible
    /// by design; don't use case="title" on fields that carry acronyms.</summary>
    public static string EnglishTitleCase(string v)
    {
        var words = v.ToLowerInvariant().Split(' ');
        int first = Array.FindIndex(words, w => w.Length > 0);
        int last = Array.FindLastIndex(words, w => w.Length > 0);
        bool afterPunct = false;
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            if (w.Length == 0) continue;
            string core = w.TrimEnd(':', ';', '.', '!', '?', ',');
            bool force = i == first || i == last || afterPunct;
            afterPunct = w.Length > core.Length && w[core.Length] is ':' or ';' or '.' or '!' or '?';
            if (!force && TitleSmallWords.Contains(core)) continue;
            words[i] = CapParts(w);
        }
        return string.Join(' ', words);

        static string CapParts(string w)
        {
            var chars = w.ToCharArray();
            bool capNext = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (capNext && char.IsLetter(chars[i]))
                {
                    chars[i] = char.ToUpperInvariant(chars[i]);
                    capNext = false;
                }
                else if (chars[i] is '-' or '/')
                    capNext = true;
                else if (char.IsLetterOrDigit(chars[i]))
                    capNext = false;
            }
            return new string(chars);
        }
    }

    /// <summary>Shared display formatting for data-format= and seg format=:
    /// "date:&lt;pattern&gt;" reformats/stamps a date, "number:&lt;pattern&gt;"
    /// zero-pads/formats numerics. Unknown specs pass the value through.</summary>
    public static string FormatValue(string v, string spec, DateTime now)
    {
        if (spec.StartsWith("date:"))
        {
            string pattern = spec["date:".Length..];
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var parsed))
                return parsed.ToString(pattern, CultureInfo.InvariantCulture);
            return (v == "" ? now : now).ToString(pattern, CultureInfo.InvariantCulture);
        }
        if (spec.StartsWith("number:"))
        {
            string pattern = spec["number:".Length..];
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n.ToString(pattern, CultureInfo.InvariantCulture);
        }
        return v;
    }
}
