using System.Xml.Linq;

namespace Etiq.Editor.Core;

/// <summary>
/// Screen-only redaction (remote demos, screen shares). An element marked
/// <c>data-sensitive</c> shows a stand-in in the EDITOR — canvas, outline,
/// inspector, data-panel echoes — while the editor's redact mode is on.
/// Print paths, engines and the validator never look at it: what prints
/// is always the real content.
///
/// - <c>data-sensitive="true"</c> on a field-bound element → the SVG
///   placeholder (the anonymous sample text the convention already asks
///   for) is shown instead of the resolved value.
/// - <c>data-sensitive="true"</c> on static text → a block mask.
/// - <c>data-sensitive="SAMPLE COMPANY"</c> → that literal is the stand-in.
/// </summary>
public static class Redaction
{
    public const string Attr = "data-sensitive";

    public static bool IsSensitive(XElement el) => el.Attribute(Attr) is not null;

    /// <summary>Element-level mark OR bound to a field flagged
    /// <c>sensitive="true"</c> in the metadata.</summary>
    public static bool IsSensitive(XElement el, ISet<string> sensitiveFields) =>
        el.Attribute(Attr) is not null ||
        (el.Attribute("data-field") is { } f && sensitiveFields.Contains(f.Value));

    /// <summary>Field names flagged sensitive in etiq:label (field level).</summary>
    public static HashSet<string> FlaggedFields(XElement root) =>
        root.Descendants(EditorDoc.EtiqNs + "field")
            .Where(f => (string?)f.Attribute("sensitive") == "true" && f.Attribute("name") is { } n)
            .Select(f => f.Attribute("name")!.Value)
            .ToHashSet();

    /// <summary>Everything that should be redacted, by field name: flagged
    /// fields ∪ fields bound to marked elements.</summary>
    public static HashSet<string> AllSensitiveFields(XElement root)
    {
        var set = FlaggedFields(root);
        set.UnionWith(SensitiveFields(root));
        return set;
    }

    /// <summary>Stand-in VALUES for the resolver (ResolveContext.Substitutes):
    /// the field's `stand-in`, else the placeholder content of the first
    /// element bound to it (text body / barcode data-value), else "SAMPLE".
    /// Fed to the editor's preview resolve only.</summary>
    public static Dictionary<string, string> Substitutes(XElement root)
    {
        var d = new Dictionary<string, string>();
        var fields = root.Descendants(EditorDoc.EtiqNs + "field")
            .Where(f => f.Attribute("name") is not null)
            .ToDictionary(f => f.Attribute("name")!.Value, f => f);
        foreach (string name in AllSensitiveFields(root))
        {
            string? standIn = fields.TryGetValue(name, out var fe) ? (string?)fe.Attribute("stand-in") : null;
            if (string.IsNullOrWhiteSpace(standIn))
            {
                var bound = root.Descendants().FirstOrDefault(e => (string?)e.Attribute("data-field") == name);
                if (bound is not null)
                {
                    // an element-level literal stand-in wins over the placeholder
                    string a = ((string?)bound.Attribute(Attr) ?? "").Trim();
                    standIn = !IsFlag(a) && a != "" ? a
                            : (string?)bound.Attribute("data-value") ?? bound.Value;
                }
            }
            d[name] = string.IsNullOrWhiteSpace(standIn) ? "SAMPLE" : standIn.Trim();
        }
        return d;
    }

    private static bool IsFlag(string a) =>
        a is "" || string.Equals(a, "true", StringComparison.OrdinalIgnoreCase) || a == "1";

    /// <summary>What to show for a sensitive element: `placeholder` is the
    /// element's design-time content (text body, or data-value for a
    /// barcode). Callers check <see cref="IsSensitive"/> and the editor's
    /// redact flag first.</summary>
    public static string Display(XElement el, string placeholder)
    {
        string a = ((string?)el.Attribute(Attr) ?? "").Trim();
        if (!IsFlag(a)) return a;
        return el.Attribute("data-field") is not null ? placeholder : Mask(placeholder);
    }

    /// <summary>Block mask roughly the length of the original (per line).</summary>
    public static string Mask(string s)
    {
        var lines = s.Replace("\r", "").Split('\n');
        return string.Join("\n", lines.Select(l => new string('█', Math.Clamp(l.Trim().Length, 3, 24))));
    }

    /// <summary>Set/clear the mark. `standIn` null/blank with `on` = "true".</summary>
    public static EditCommand Set(XElement el, bool on, string? standIn) =>
        EditCommand.SetAttr(el, Attr,
            !on ? null : string.IsNullOrWhiteSpace(standIn) ? "true" : standIn.Trim(),
            on ? "mark sensitive" : "clear sensitive");

    /// <summary>Names of pick lists that feed a sensitive field: any
    /// <c>etiq:field source="list" list="X"</c> whose name is in
    /// <paramref name="sensitiveFields"/>. Their dropdown rows are masked
    /// too — a row list of real addresses is the same leak.</summary>
    public static HashSet<string> SensitiveLists(XElement root, ISet<string> sensitiveFields)
    {
        var ns = EditorDoc.EtiqNs;
        return root.Descendants(ns + "field")
            .Where(f => string.Equals((string?)f.Attribute("source"), "list", StringComparison.OrdinalIgnoreCase)
                        && f.Attribute("name") is { } n && sensitiveFields.Contains(n.Value)
                        && f.Attribute("list") is { } l && !string.IsNullOrWhiteSpace(l.Value))
            .Select(f => f.Attribute("list")!.Value)
            .ToHashSet();
    }

    /// <summary>Names of fields bound to any sensitive element (for masking
    /// data-panel echoes of pulled values).</summary>
    public static HashSet<string> SensitiveFields(XElement root) =>
        root.Descendants()
            .Where(e => e.Attribute(Attr) is not null && e.Attribute("data-field") is { } f && !string.IsNullOrWhiteSpace(f.Value))
            .Select(e => e.Attribute("data-field")!.Value)
            .ToHashSet();
}
