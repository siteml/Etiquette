using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Etiq.Core;

public enum Severity { Error, Warning }

public sealed record Finding(Severity Severity, string Code, string Message)
{
    public override string ToString() =>
        $"{(Severity == Severity.Error ? "ERROR" : "warn ")}  {Code,-18} {Message}";
}

/// <summary>
/// `etiq validate` rules (docs/convention.md "Validation" section, draft 0.2):
/// declared/used field cross-check, barcode sanity, serial counters,
/// physical units, bounds, compose segments, lookup maps, on-fail policy,
/// layers, text fit.
/// </summary>
public static class TemplateValidator
{
    // pick = dotted path with optional [index] — deliberately NOT JSONPath.
    private static readonly Regex PickRx = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\[\d+\])?(\.[A-Za-z_][A-Za-z0-9_]*(\[\d+\])?)*$",
        RegexOptions.Compiled);
    // pad = side:char:width
    private static readonly Regex PadRx = new(
        @"^(left|right):.:\d+$", RegexOptions.Compiled);

    public static List<Finding> Validate(EtiqTemplate t)
    {
        var findings = new List<Finding>();
        void Err(string code, string msg) => findings.Add(new(Severity.Error, code, msg));
        void Warn(string code, string msg) => findings.Add(new(Severity.Warning, code, msg));

        // --- root physical units ---
        if (t.WidthAttr is null || t.HeightAttr is null)
            Err("root-units", "svg root must declare width and height");
        else if (!Regex.IsMatch(t.WidthAttr, @"^\d+(\.\d+)?(in|mm|cm)$") ||
                 !Regex.IsMatch(t.HeightAttr, @"^\d+(\.\d+)?(in|mm|cm)$"))
            Warn("root-units", $"width/height should carry physical units (in/mm/cm): '{t.WidthAttr}' x '{t.HeightAttr}'");
        if (t.ViewBox is null)
            Err("root-viewbox", "svg root must declare a viewBox");

        // --- declared remote sources ---
        var sourcesByName = new Dictionary<string, EtiqTemplate.SourceDef>(StringComparer.Ordinal);
        foreach (var src in t.Sources)
        {
            if (src.Name == "")
                { Err("source-name", "etiq:query with empty/missing name"); continue; }
            if (!sourcesByName.TryAdd(src.Name, src))
                Err("source-dup", $"source '{src.Name}' declared more than once");
            if (src.Connection == "")
                Err("source-conn", $"source '{src.Name}': connection= is required (a NAMED machine connection — never credentials or URLs in the template)");
            if (string.IsNullOrWhiteSpace(src.Baq) && string.IsNullOrWhiteSpace(src.Query))
                Err("source-baq", $"source '{src.Name}': baq= (epicor) or query= (glpi item type, e.g. Computer) is required");
            if (src.Params.Count == 0 && src.Filters.Count == 0 && !t.Lists.Any(l => l.From == src.Name))
                Warn("source-open", $"source '{src.Name}': no param-/filter- attributes — every fetch pulls the WHOLE result set");
        }

        // --- data-panel presentation ---
        {
            var p = t.Panel;
            if (p.El is not null)
            {
                if (p.Print is not ("dialog" or "direct"))
                    Err("panel-print", $"etiq:panel print='{p.Print}' — must be dialog|direct");
                if (p.Copies is not ("ask" or "embedded") && p.FixedCopies is null)
                    Err("panel-copies", $"etiq:panel copies='{p.Copies}' — must be ask|embedded|fixed:N");
                if (p.Collate is not ("choose" or "grouped" or "sequenced" or "ask"))
                    Err("panel-collate", $"etiq:panel collate='{p.Collate}' — must be choose|grouped|sequenced|ask");
                if (p.ButtonsAt is not ("bottom" or "top"))
                    Err("panel-buttons", $"etiq:panel buttons-at='{p.ButtonsAt}' — must be bottom|top");
                foreach (var b in p.Buttons)
                    if (b is not ("preview" or "print" or "printall" or "clear" or "log"))
                        Err("panel-buttons", $"etiq:panel buttons: unknown '{b}' (preview|print|printall|clear|log)");
                if (p.Printer is not null && p.Print != "direct")
                    Warn("panel-printer", "etiq:panel printer= only applies with print='direct'");
                // printer="embedded" is the on-form picker; any other value is a printer NAME
            }
        }

        // --- lookup maps ---
        var mapsByName = new Dictionary<string, EtiqTemplate.MapDef>();
        foreach (var m in t.Maps)
        {
            if (m.Name == "")
                { Err("map-name", "etiq:map with empty/missing name"); continue; }
            if (!mapsByName.TryAdd(m.Name, m))
                Err("map-dup", $"map '{m.Name}' declared more than once");
            foreach (var w in m.Whens)
            {
                bool hasFrom = w.Attribute("from") is not null;
                bool hasPrefix = w.Attribute("prefix") is not null;
                if (hasFrom == hasPrefix)   // both or neither
                    Err("map-when", $"map '{m.Name}': each etiq:when needs exactly one of from=|prefix=");
                if (w.Attribute("to") is null)
                    Err("map-when", $"map '{m.Name}': etiq:when requires to=");
            }
        }

        // --- embedded pick lists ---
        var listsByName = new Dictionary<string, EtiqTemplate.ListDef>();
        foreach (var l in t.Lists)
        {
            if (l.Name == "")
                { Err("list-name", "etiq:list with empty/missing name"); continue; }
            if (!listsByName.TryAdd(l.Name, l))
                Err("list-dup", $"list '{l.Name}' declared more than once");
            if (l.Key == "")
                { Err("list-key", $"list '{l.Name}': key= (the selector column) is required"); continue; }
            if (l.From is { } lf)
            {
                if (!sourcesByName.ContainsKey(lf))
                    Err("list-from", $"list '{l.Name}': from='{lf}' names no declared etiq:query");
                if (l.Rows.Count > 0)
                    Warn("list-from", $"list '{l.Name}': embedded etiq:row elements are ignored when from= is set");
                if (l.FilterColumn is not null)
                    Warn("list-from", $"list '{l.Name}': filter-column applies to the fetched rows (fine) — but consider filter-* on the query to fetch less");
                continue;   // rows/keys/default are only known at run time
            }
            if (l.Rows.Count == 0)
                Err("list-rows", $"list '{l.Name}': needs at least one etiq:row");
            var keys = new HashSet<string>();
            foreach (var row in l.Rows)
            {
                if (!row.TryGetValue(l.Key, out var kv) || kv == "")
                    Err("list-key", $"list '{l.Name}': a row is missing the key column '{l.Key}'");
                else if (!keys.Add(kv))
                    Err("list-key", $"list '{l.Name}': duplicate key '{kv}'");
            }
            if (l.Default is not null && l.RowByKey(l.Default) is null)
                Err("list-default", $"list '{l.Name}': default '{l.Default}' matches no row");
            if (l.Columns is { } declared && l.Key != "" && !declared.Contains(l.Key))
                Warn("list-columns", $"list '{l.Name}': columns= does not include the key column '{l.Key}'");
        }

        // --- metadata fields ---
        var byName = new Dictionary<string, EtiqTemplate.Field>();
        foreach (var f in t.Fields)
        {
            if (f.Name == "")
                { Err("field-name", "etiq:field with empty/missing name"); continue; }
            if (!byName.TryAdd(f.Name, f))
                Err("field-dup", $"field '{f.Name}' declared more than once");

            bool reserved = EtiqTemplate.ReservedSourceKinds.Contains(f.Source);
            if (!EtiqTemplate.SourceKinds.Contains(f.Source) && !reserved)
                Err("field-source", $"field '{f.Name}': unknown source '{f.Source}'");
            if (reserved)
                Warn("field-reserved", $"field '{f.Name}': source '{f.Source}' is reserved — validates structurally but fails at print time (not yet implemented)");

            switch (f.Source)
            {
                case "epicor":
                    if (string.IsNullOrWhiteSpace(f.Column))
                        Err("field-epicor", $"field '{f.Name}': source=epicor requires column=");
                    if (f.From is not null && !sourcesByName.ContainsKey(f.From))
                        Err("field-from", $"field '{f.Name}': from='{f.From}' names no declared etiq:query");
                    break;
            }
            if (f.Override && f.Source is not ("epicor" or "rest"))
                Warn("field-override", $"field '{f.Name}': override= has no effect on source={f.Source}");
            switch (f.Source)
            {
                case "serial" when string.IsNullOrWhiteSpace(f.Counter):
                    Err("serial-counter", $"field '{f.Name}': source=serial requires counter="); break;
                case "fixed" when f.Value is null:
                    Err("field-fixed", $"field '{f.Name}': source=fixed requires value="); break;
                case "auto" when string.IsNullOrWhiteSpace(f.Value):
                    Err("field-auto", $"field '{f.Name}': source=auto requires value= (e.g. date:dd-MMM-yyyy)"); break;
                case "prompt" when string.IsNullOrWhiteSpace(f.Caption):
                    Warn("prompt-caption", $"field '{f.Name}': prompt without caption= (operator sees a blank prompt)"); break;
                case "rest" when f.From is not null:
                    // declared-query form: from= + column= (like epicor)
                    if (!sourcesByName.ContainsKey(f.From))
                        Err("field-from", $"field '{f.Name}': from='{f.From}' names no declared etiq:query");
                    if (string.IsNullOrWhiteSpace(f.Column))
                        Err("field-rest", $"field '{f.Name}': source=rest with from= requires column=");
                    if (f.Connection is not null || f.Pick is not null)
                        Warn("field-rest", $"field '{f.Name}': connection=/pick= are ignored when from= names a declared query");
                    break;
                case "rest":
                    if (string.IsNullOrWhiteSpace(f.Connection))
                        Err("field-rest", $"field '{f.Name}': source=rest requires connection= (a named profile)");
                    if (string.IsNullOrWhiteSpace(f.Pick))
                        Err("field-rest", $"field '{f.Name}': source=rest requires pick=");
                    else if (!PickRx.IsMatch(f.Pick))
                        Err("field-pick", $"field '{f.Name}': pick='{f.Pick}' is not a dotted path with optional [index] (e.g. assets[0].name)");
                    break;
                case "db":       // reserved: structural checks only
                    if (string.IsNullOrWhiteSpace(f.Connection))
                        Err("field-db", $"field '{f.Name}': source=db requires connection= (a named profile)");
                    if (string.IsNullOrWhiteSpace(f.Query))
                        Err("field-db", $"field '{f.Name}': source=db requires query= (a NAMED query in the profile — raw SQL never in templates)");
                    if (string.IsNullOrWhiteSpace(f.Column))
                        Err("field-db", $"field '{f.Name}': source=db requires column=");
                    break;
                case "file":     // reserved: structural checks only
                    if (string.IsNullOrWhiteSpace(f.FilePath))
                        Err("field-file", $"field '{f.Name}': source=file requires path=");
                    if (string.IsNullOrWhiteSpace(f.Column))
                        Err("field-file", $"field '{f.Name}': source=file requires column=");
                    if ((f.MatchColumn is null) != (f.MatchValue is null))
                        Err("field-file", $"field '{f.Name}': match-column= and match-value= must be set together");
                    break;
                case "device":   // reserved: structural checks only
                    if (string.IsNullOrWhiteSpace(f.Connection))
                        Err("field-device", $"field '{f.Name}': source=device requires connection= (a named device profile)");
                    break;
                case "list":
                    if (string.IsNullOrWhiteSpace(f.ListRef))
                        Err("field-list", $"field '{f.Name}': source=list requires list=");
                    else if (!listsByName.TryGetValue(f.ListRef, out var ld))
                        Err("field-list", $"field '{f.Name}': list '{f.ListRef}' is not declared");
                    else if (string.IsNullOrWhiteSpace(f.Column))
                        Err("field-list", $"field '{f.Name}': source=list requires column=");
                    else if (ld.From is null && !ld.Rows.Any(r => r.ContainsKey(f.Column)))
                        // query-fed lists get their columns at run time —
                        // nothing to cross-check statically
                        Err("field-list", $"field '{f.Name}': no row of list '{f.ListRef}' has a column '{f.Column}'");
                    else if (!ld.Rows.All(r => r.ContainsKey(f.Column)))
                        Warn("field-list", $"field '{f.Name}': column '{f.Column}' is missing from some rows of '{f.ListRef}' (resolves empty there)");
                    break;
            }

            // on-fail: remote kinds only, closed enum
            if (f.OnFail is not null)
            {
                if (f.Source is not ("epicor" or "rest" or "db" or "file" or "device"))
                    Warn("on-fail", $"field '{f.Name}': on-fail= has no effect on source={f.Source}");
                if (f.OnFail is not ("block" or "cached") && !f.OnFail.StartsWith("use:"))
                    Err("on-fail", $"field '{f.Name}': on-fail must be block|cached|use:TEXT, got '{f.OnFail}'");
            }
            if (f.Required is not null && f.Required is not ("true" or "false"))
                Err("field-required", $"field '{f.Name}': required= must be true|false");
            if (f.Case is not null)
            {
                if (f.Case is not ("normal" or "upper" or "lower" or "title"))
                    Err("field-case", $"field '{f.Name}': case= must be normal|upper|lower|title");
                if (f.Source == "compose")
                    Warn("field-case", $"field '{f.Name}': case= has no effect on compose fields (use per-segment case=)");
            }

            // compose structure
            if (f.Source == "compose" && f.Segs.Count == 0 && f.Variants.Count == 0)
                Err("compose-empty", $"field '{f.Name}': source=compose requires etiq:seg children (or etiq:variant blocks)");
            if (f.Source != "compose" && (f.Segs.Count > 0 || f.Variants.Count > 0))
                Err("compose-segs", $"field '{f.Name}': etiq:seg/etiq:variant children only allowed on source=compose");
            if (f.CollapseBlankLines && f.Source != "compose")
                Err("compose-collapse", $"field '{f.Name}': collapse-blank-lines= only applies to source=compose");

            // variant structure (conditional segment lists)
            if (f.Variants.Count > 0)
            {
                if (f.Segs.Count > 0)
                    Err("variant-mixed", $"field '{f.Name}': direct etiq:seg children cannot mix with etiq:variant blocks");
                if (f.SwitchOn is null)
                    Err("variant-switch", $"field '{f.Name}': etiq:variant blocks require switch-on=\"FieldName\"");
                int defaults = 0, vi = 0;
                foreach (var v in f.Variants)
                {
                    vi++;
                    if (v.When is not null && v.Prefix is not null)
                        Err("variant-match", $"field '{f.Name}' variant #{vi}: at most one of when=|prefix=");
                    if (v.IsDefault) defaults++;
                    if (v.Segs.Count == 0)
                        Err("variant-empty", $"field '{f.Name}' variant #{vi}: needs at least one etiq:seg");
                }
                if (defaults > 1)
                    Err("variant-default", $"field '{f.Name}': more than one default variant (no when=/prefix=)");
                if (defaults == 0)
                    Warn("variant-default", $"field '{f.Name}': no default variant — an unmatched {f.SwitchOn} value will block at print time");
            }
            else if (f.SwitchOn is not null)
            {
                Err("variant-switch", $"field '{f.Name}': switch-on= without etiq:variant blocks has no effect");
            }
        }

        // --- source param/filter field references (needs full field table) ---
        foreach (var src in t.Sources)
        {
            var targetRef = new Dictionary<string, string>();
            if (src.Baq is { } bq) targetRef["baq"] = bq;
            if (src.Query is { } qy) targetRef["query"] = qy;
            foreach (var (kind, dict) in new[] { ("param", src.Params), ("filter", src.Filters), ("", targetRef) })
                foreach (var (key, val) in dict)
                {
                    if (!val.StartsWith('{') || !val.EndsWith('}')) continue;   // literal
                    string rf = val[1..^1];
                    string attr = kind == "" ? key : $"{kind}-{key}";
                    if (!byName.TryGetValue(rf, out var pf))
                        { Err("source-ref", $"source '{src.Name}': {attr} references undeclared field '{rf}'"); continue; }
                    if (pf.Source is ("epicor" or "rest") && pf.From == src.Name)
                        Err("source-cycle", $"source '{src.Name}': {attr} references field '{rf}' which reads FROM this source (circular)");
                }
        }

        // --- compose segments (second pass: needs full field table) ---
        var segUsed = new HashSet<string>();
        foreach (var f in t.Fields.Where(f => f.Source == "compose"))
        {
            if (f.SwitchOn is { } sw)
            {
                if (!byName.TryGetValue(sw, out var swTarget))
                    Err("variant-switch", $"field '{f.Name}': switch-on='{sw}' is not a declared field");
                else if (swTarget.Variants.Count > 0 || swTarget.SwitchOn is not null)
                    Err("variant-switch", $"field '{f.Name}': switch-on='{sw}' points at a field that itself switches (no chained switching)");
                else
                    segUsed.Add(sw);   // plain compose helpers (e.g. country → code via map) are fine
            }
            int i = 0;
            foreach (var s in f.Segs.Concat(f.Variants.SelectMany(v => v.Segs)))
            {
                i++;
                string loc = $"field '{f.Name}' seg #{i}";
                if (s.Newline)
                {
                    // a line break carries nothing else
                    if (s.Value is not null || s.Ref is not null || s.Sep is not null ||
                        s.Map is not null || s.Format is not null || s.Pad is not null ||
                        s.Start is not null || s.Len is not null || s.Case is not null ||
                        s.Split is not null || s.Part is not null)
                        Err("seg-newline", $"{loc}: newline=\"true\" must be the segment's only attribute");
                    continue;
                }
                if ((s.Value is null) == (s.Ref is null))   // both or neither
                    Err("seg-content", $"{loc}: exactly one of value=|ref= required");
                if (s.Ref is not null)
                {
                    segUsed.Add(s.Ref);
                    if (!byName.TryGetValue(s.Ref, out var target))
                        Err("seg-ref", $"{loc}: ref='{s.Ref}' is not a declared field");
                    else if (target.Source == "compose")
                        Err("seg-ref", $"{loc}: ref='{s.Ref}' points at a compose field (composition is one level deep by design)");
                }
                if (s.Pad is not null && !PadRx.IsMatch(s.Pad))
                    Err("seg-pad", $"{loc}: pad='{s.Pad}' must be side:char:width (e.g. left:0:6)");
                if (s.Part is not null && s.Split is null)
                    Err("seg-split", $"{loc}: part= needs split=");
                if (s.Split is not null && s.Split == "")
                    Err("seg-split", $"{loc}: split= must not be empty");
                if (s.Part is not null && EtiqTemplate.ParseNum(s.Part) is null)
                    Err("seg-split", $"{loc}: part= must be an integer (negative counts from the end)");
                if (s.Start is not null && (EtiqTemplate.ParseNum(s.Start) is not (>= 0)))
                    Err("seg-substr", $"{loc}: start= must be a non-negative number");
                if (s.Len is not null && (EtiqTemplate.ParseNum(s.Len) is not (>= 0)))
                    Err("seg-substr", $"{loc}: len= must be a non-negative number");
                if (s.Case is not null && s.Case is not ("normal" or "upper" or "lower" or "title"))
                    Err("seg-case", $"{loc}: case= must be normal|upper|lower|title");
                if (s.Map is not null)
                {
                    if (!mapsByName.TryGetValue(s.Map, out var map))
                        Err("seg-map", $"{loc}: map='{s.Map}' is not a declared etiq:map");
                    else if (s.Default is null && map.Default is null)
                        Warn("map-default", $"{loc}: map '{s.Map}' has no default anywhere — a non-match will block at print time");
                }
            }
        }

        // --- used vs declared ---
        var used = new HashSet<string>();
        foreach (var el in t.DynamicTexts)
        {
            string name = (string?)el.Attribute("data-field") ?? "";
            used.Add(name);
            if (!byName.ContainsKey(name))
                Err("field-undeclared", $"<{el.Name.LocalName} data-field=\"{name}\"> has no etiq:field declaration");
            if (string.IsNullOrWhiteSpace(el.Value))
                Warn("text-placeholder", $"data-field=\"{name}\": empty placeholder text (convention: placeholder doubles as sample-data preview)");
        }
        foreach (var b in t.Barcodes)
            if (b.FieldRef is not null)
            {
                used.Add(b.FieldRef);
                if (!byName.ContainsKey(b.FieldRef))
                    Err("field-undeclared", $"barcode rect data-field=\"{b.FieldRef}\" has no etiq:field declaration");
            }
        // --- list picker attributes (caption/display/filter) ---
        foreach (var l in t.Lists)
        {
            if (l.Display is { } disp)
            {
                if (!byName.ContainsKey(disp))
                    Err("list-display", $"list '{l.Name}': display='{disp}' is not a declared field");
                else segUsed.Add(disp);
            }
            if ((l.FilterColumn is null) != (l.FilterRef is null))
                Err("list-filter", $"list '{l.Name}': filter-column= and filter-ref= must be set together");
            if (l.FilterRef is { } fr)
            {
                if (!byName.TryGetValue(fr, out var ff))
                    Err("list-filter", $"list '{l.Name}': filter-ref='{fr}' is not a declared field");
                else
                {
                    segUsed.Add(fr);
                    if (ff.Source == "list" && ff.ListRef == l.Name)
                        Err("list-filter", $"list '{l.Name}': filter-ref='{fr}' reads the list it filters (circular)");
                }
            }
            if (l.FilterColumn is { } fc && l.Rows.Count > 0 && !l.Rows.Any(r => r.ContainsKey(fc)))
                Warn("list-filter", $"list '{l.Name}': no row has filter-column '{fc}'");
        }

        used.UnionWith(segUsed);   // a field consumed by a compose seg counts as used
        foreach (var name in byName.Keys.Where(n => !used.Contains(n)))
            Warn("field-unused", $"field '{name}' declared but not used by any element");

        // --- on-fail="use:" feeding a barcode (directly or via compose) ---
        foreach (var b in t.Barcodes)
        {
            if (b.FieldRef is null || !byName.TryGetValue(b.FieldRef, out var bf)) continue;
            if (bf.OnFail?.StartsWith("use:") == true)
                Warn("barcode-onfail", $"barcode consumes field '{bf.Name}' with on-fail=\"use:\" — a substituted literal in a barcode is usually wrong");
            if (bf.Source == "compose")
                foreach (var s in bf.Segs)
                    if (s.Ref is not null && byName.TryGetValue(s.Ref, out var rf) &&
                        rf.OnFail?.StartsWith("use:") == true)
                        Warn("barcode-onfail", $"barcode consumes compose field '{bf.Name}' whose seg ref '{rf.Name}' has on-fail=\"use:\"");
        }

        // --- layers ---
        var layerNames = new HashSet<string>();
        foreach (var l in t.Layers)
        {
            if (l.Name == "")
                Err("layer-name", "layer <g data-layer> with empty name");
            else if (!layerNames.Add(l.Name))
                Err("layer-dup", $"layer '{l.Name}' declared more than once");
            if (l.PrintAttr is not null && l.PrintAttr is not ("true" or "false"))
                Err("layer-print", $"layer '{l.Name}': data-print must be true|false");
            if (l.PrintAttr == "false" &&
                l.El.Descendants().Any(e => e.Attribute("data-field") is not null))
                Warn("layer-print", $"layer '{l.Name}': data-print=\"false\" but contains data-field elements (bound but never printed)");
        }
        // layer attributes anywhere other than a root-child <g> are misplaced
        var layerEls = t.Layers.Select(l => l.El).ToHashSet();
        foreach (var e in t.Doc.Descendants())
        {
            if (layerEls.Contains(e)) continue;
            foreach (var attr in new[] { "data-layer", "data-locked", "data-print" })
                if (e.Attribute(attr) is not null)
                    Err("layer-misplaced", $"{attr} on <{e.Name.LocalName}> — layer attributes belong only on direct-child <g> of the svg root");
        }

        // --- barcode rects ---
        foreach (var b in t.Barcodes)
        {
            string sym = b.Symbology;
            if (!EtiqTemplate.Symbologies.Contains(sym))
                Err("barcode-symbology", $"unknown data-barcode '{sym}' (known: {string.Join('|', EtiqTemplate.Symbologies)})");
            if (b.W <= 0 || b.H <= 0)
                Err("barcode-degenerate", $"barcode '{sym}': degenerate rect {b.W}x{b.H}");
            if (b.FieldRef is null && b.FixedValue is null)
                Err("barcode-content", $"barcode '{sym}': needs data-field or data-value");
            if (b.FieldRef is not null && b.FixedValue is not null)
                Warn("barcode-content", $"barcode '{sym}': both data-field and data-value set; data-field wins");
            if (b.Hri is not null && b.Hri is not ("none" or "below" or "above"))
                Err("barcode-hri", $"barcode '{sym}': data-hri must be none|below|above, got '{b.Hri}'");
            if (b.ModuleMils is not null && b.ModuleMils <= 0)
                Err("barcode-module", $"barcode '{sym}': data-module-mils must be positive");
            else if (b.ModuleMils is not null && b.ModuleMils < 10)
                Warn("barcode-module", $"barcode '{sym}': X-dim {b.ModuleMils} mils below AIAG ~10-13 mil guidance");
            if ((string?)b.El.Attribute("data-ecc") is { } becc)
            {
                if (becc is not ("L" or "M" or "Q" or "H"))
                    Err("barcode-ecc", $"barcode '{sym}': data-ecc must be L|M|Q|H, got '{becc}'");
                if (sym != "qr")
                    Warn("barcode-ecc", $"barcode '{sym}': data-ecc only applies to qr");
            }
            if ((string?)b.El.Attribute("data-columns") is { } bcols)
            {
                if (!int.TryParse(bcols, out int nc) || nc is < 1 or > 30)
                    Err("barcode-columns", $"barcode '{sym}': data-columns must be 1-30, got '{bcols}'");
                if (sym != "pdf417")
                    Warn("barcode-columns", $"barcode '{sym}': data-columns only applies to pdf417");
            }
            if ((string?)b.El.Attribute("data-logo") is { } blogo)
            {
                if (sym != "qr")
                    Warn("barcode-logo", $"barcode '{sym}': data-logo only applies to qr");
                else if ((string?)b.El.Attribute("data-ecc") is { } le && le != "H")
                    Warn("barcode-logo", $"barcode '{sym}': a logo overlay forces ECC level H — data-ecc=\"{le}\" is ignored");
                if (blogo is not "etiq" && !blogo.StartsWith("data:") &&
                    !blogo.StartsWith("http://") && !blogo.StartsWith("https://") &&
                    t.Path != "<memory>" && Path.GetDirectoryName(t.Path) is { } bd &&
                    !File.Exists(Path.IsPathRooted(blogo) ? blogo : Path.Combine(bd, blogo)))
                    Warn("barcode-logo", $"barcode '{sym}': logo image '{blogo}' not found (renders without the logo)");
            }
            if ((string?)b.El.Attribute("data-logo-scale") is { } blsc)
            {
                if (!int.TryParse(blsc, out int lv) || lv is < 25 or > 130)
                    Err("barcode-logo", $"barcode '{sym}': data-logo-scale must be 25-130, got '{blsc}'");
                if (b.El.Attribute("data-logo") is null)
                    Warn("barcode-logo", $"barcode '{sym}': data-logo-scale without data-logo has no effect");
            }
        }

        // --- text fit (any <text>/<tspan>, dynamic or static) ---
        foreach (var el in t.Doc.Descendants().Where(e => e.Name.LocalName is "text" or "tspan"))
        {
            double? w = EtiqTemplate.ParseNum((string?)el.Attribute("data-width"));
            double? h = EtiqTemplate.ParseNum((string?)el.Attribute("data-height"));
            string? overflow = (string?)el.Attribute("data-overflow");
            string what = $"<{el.Name.LocalName}> '{Snip(el.Value)}'";
            if (el.Attribute("data-width") is not null && w is not > 0)
                Err("text-fit", $"{what}: data-width must be a positive number");
            if (overflow is not null)
            {
                if (overflow is not ("clip" or "shrink" or "wrap"))
                    Err("text-fit", $"{what}: data-overflow must be clip|shrink|wrap, got '{overflow}'");
                if (el.Attribute("data-width") is null)
                    Err("text-fit", $"{what}: data-overflow requires data-width");
                if (overflow == "wrap" && h is not > 0)
                    Err("text-fit", $"{what}: data-overflow=\"wrap\" requires a positive data-height");
            }
            // data-fit: none (dynamic width) | width (squeeze) | box (shrink
            // font into data-width × data-height)
            if ((string?)el.Attribute("data-fit") is { } dfit)
            {
                if (dfit is not ("none" or "width" or "box"))
                    Err("text-fit", $"{what}: data-fit must be none|width|box, got '{dfit}'");
                else if (dfit == "width" && w is not > 0)
                    Warn("text-fit", $"{what}: data-fit=\"width\" without a positive data-width has no effect");
                else if (dfit == "box" && (w is not > 0 || h is not > 0))
                    Warn("text-fit", $"{what}: data-fit=\"box\" needs positive data-width AND data-height to constrain the font");
            }
            // data-line: this element shows the Nth line of its field's value
            if ((string?)el.Attribute("data-line") is { } dl)
            {
                if (!int.TryParse(dl, out int ln) || ln < 0)
                    Err("text-line", $"{what}: data-line must be a non-negative integer, got '{dl}'");
                if (el.Attribute("data-field") is null)
                    Warn("text-line", $"{what}: data-line without data-field has no effect (static text shows its own content)");
            }
            // width box within label bounds, per text-anchor
            if (w is > 0 && t.ViewBox is [var minX, _, var vw, _])
            {
                double? x = EtiqTemplate.ParseNum((string?)el.Attribute("x"));
                if (x is not null)
                {
                    string anchor = (string?)el.Attribute("text-anchor") ?? "start";
                    (double lo, double hi) = anchor switch
                    {
                        "middle" => (x.Value - w.Value / 2, x.Value + w.Value / 2),
                        "end" => (x.Value - w.Value, x.Value),
                        _ => (x.Value, x.Value + w.Value),
                    };
                    if (lo < minX || hi > minX + vw)
                        Err("text-fit", $"{what}: data-width box ({lo:0.#}..{hi:0.#}) exceeds viewBox width");
                }
            }
        }

        // --- bounds (viewBox coordinate space) ---
        if (t.ViewBox is [var vbX, var vbY, var vbW, var vbH])
        {
            foreach (var el in t.DynamicTexts.Concat(
                     t.Doc.Descendants().Where(e => e.Name.LocalName == "text" && e.Attribute("data-field") is null)))
            {
                double? x = EtiqTemplate.ParseNum((string?)el.Attribute("x"));
                double? y = EtiqTemplate.ParseNum((string?)el.Attribute("y"));
                if (x is null || y is null) continue;
                if (x < vbX || x > vbX + vbW || y < vbY || y > vbY + vbH)
                    Err("out-of-bounds", $"<text> at ({x},{y}) lies outside viewBox {vbX} {vbY} {vbW} {vbH}: '{Snip(el.Value)}'");
            }
            foreach (var b in t.Barcodes)
                if (b.X < vbX || b.X + b.W > vbX + vbW || b.Y < vbY || b.Y + b.H > vbY + vbH)
                    Err("out-of-bounds", $"barcode rect ({b.X},{b.Y} {b.W}x{b.H}) exceeds viewBox");
        }

        return findings;

        static string Snip(string s) => s.Length > 30 ? s[..30] + "…" : s;
    }
}
