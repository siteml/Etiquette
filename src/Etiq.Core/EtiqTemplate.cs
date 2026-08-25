using System.Globalization;
using System.Xml.Linq;

namespace Etiq.Core;

/// <summary>
/// An Etiquette SVG label template (docs/convention.md, draft 0.2).
/// Parsing only — rendering/printing is Phase 3.
/// </summary>
public sealed class EtiqTemplate
{
    public static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    public static readonly XNamespace Ns = "https://etiquette.dev/ns/0.1";
    // iqr removed: Denso Wave never published the iQR spec openly (unlike
    // QR/ISO 18004) and no open decoder exists — unimplementable AND
    // unverifiable. Rectangular needs are covered by DataMatrix rect
    // formats (rMQR / ISO 23941 is the open candidate if a rectangular QR
    // is ever wanted). Legacy templates naming iqr still validate as
    // unknown-symbology and render the placeholder.
    public static readonly string[] Symbologies =
        { "code39", "code39ext", "code128", "gs1-128", "itf14",
          "datamatrix", "qr", "rmqr", "aztec", "pdf417" };
    /// <summary>Source kinds the engine implements.</summary>
    public static readonly string[] SourceKinds =
        { "epicor", "rest", "prompt", "serial", "auto", "fixed", "compose", "list" };
    /// <summary>Reserved kinds: validate structurally, fail at print time (convention 0.2).</summary>
    public static readonly string[] ReservedSourceKinds =
        { "db", "file", "device" };

    public XDocument Doc { get; }
    public string Path { get; }

    /// <summary>Declared width/height ("6in") and viewBox, if present.</summary>
    public string? WidthAttr { get; }
    public string? HeightAttr { get; }
    public double[]? ViewBox { get; }        // minX minY w h

    /// <summary>One segment of a compose field (convention 0.2 "Composed fields").</summary>
    public sealed record Seg(XElement El)
    {
        public string? Value => (string?)El.Attribute("value");
        public string? Ref => (string?)El.Attribute("ref");
        public string? Start => (string?)El.Attribute("start");
        public string? Len => (string?)El.Attribute("len");
        public string? Format => (string?)El.Attribute("format");
        public string? Case => (string?)El.Attribute("case");
        public string? Pad => (string?)El.Attribute("pad");
        public string? Map => (string?)El.Attribute("map");
        public string? Default => (string?)El.Attribute("default");
        public string? IfEmpty => (string?)El.Attribute("if-empty");
        /// <summary>Line-break segment: contributes "\n" and nothing else.</summary>
        public bool Newline => (string?)El.Attribute("newline") == "true";
        /// <summary>Smart separator: emitted BEFORE this segment's content,
        /// but only when the segment resolved non-empty AND the current line
        /// already has content — so blanks never leave dangling commas.</summary>
        public string? Sep => (string?)El.Attribute("sep");
    }

    /// <summary>One conditional segment list of a variant compose field
    /// (convention 0.2 "Variant composition"). Matching mirrors maps:
    /// exact `when` beats `prefix`; a variant with neither is the default.</summary>
    public sealed record Variant(XElement El)
    {
        public string? When => (string?)El.Attribute("when");
        public string? Prefix => (string?)El.Attribute("prefix");
        public bool IsDefault => When is null && Prefix is null;
        public List<Seg> Segs { get; } = new();
    }

    /// <summary>A named lookup table (convention 0.2 "Lookup maps").</summary>
    public sealed record MapDef(XElement El)
    {
        public string Name => (string?)El.Attribute("name") ?? "";
        public string? Default => (string?)El.Attribute("default");
        public IEnumerable<XElement> Whens => El.Elements(Ns + "when");
    }

    /// <summary>An embedded pick list (convention 0.2 "Embedded pick
    /// lists"): rows stored in the template; the operator selects ONE row
    /// per list at print time and every field bound to that list follows
    /// the same selection. Row columns are the etiq:row attributes.</summary>
    public sealed record ListDef(XElement El)
    {
        public string Name => (string?)El.Attribute("name") ?? "";
        /// <summary>Column shown to the operator and used to select a row.</summary>
        public string Key => (string?)El.Attribute("key") ?? "";
        /// <summary>Key value of the row preselected when nothing is chosen.</summary>
        public string? Default => (string?)El.Attribute("default");
        /// <summary>Operator-facing label for the data panel ("Customer:");
        /// absent = the list name.</summary>
        public string? Caption => (string?)El.Attribute("caption");
        /// <summary>Declared field resolved PER ROW for the picker text (can
        /// be a compose over several columns); absent = key + first column.</summary>
        public string? Display => (string?)El.Attribute("display");
        /// <summary>panel="hide": no picker on the data panel (selection
        /// stays whatever default= names).</summary>
        public bool PanelHide => (string?)El.Attribute("panel") == "hide";
        /// <summary>Row filtering: offer only rows where row[filter-column]
        /// equals the resolved value of the filter-ref field (empty filter
        /// value = all rows). Both or neither.</summary>
        public string? FilterColumn => (string?)El.Attribute("filter-column");
        public string? FilterRef => (string?)El.Attribute("filter-ref");
        /// <summary>Declared column ORDER (editor-maintained). Rows may be
        /// sparse (empty cells carry no attribute), so order cannot be
        /// derived from row attributes reliably; this attribute is the
        /// truth when present.</summary>
        public string[]? Columns => ((string?)El.Attribute("columns"))
            ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        public List<Dictionary<string, string>> Rows { get; } = new();

        public Dictionary<string, string>? RowByKey(string keyValue) =>
            Rows.FirstOrDefault(r => r.GetValueOrDefault(Key) == keyValue);
    }

    /// <summary>A declared remote data source (convention 0.2+ "Sources"):
    /// ONE fetch per label against a NAMED connection, yielding a row whose
    /// columns fields reference via source="epicor" from= column=. Multiple
    /// sources per template are fine and may share a connection. The
    /// template never carries credentials or URLs — the connection name is
    /// resolved by the machine's connection store, and WHICH dataset
    /// (Epicor environment / database / tenant) is used is a machine or
    /// session choice, unless dataset= pins it here explicitly.</summary>
    public sealed record SourceDef(XElement El)
    {
        public string Name => (string?)El.Attribute("name") ?? "";
        public string Connection => (string?)El.Attribute("connection") ?? "";
        /// <summary>Rarely used: pin one dataset regardless of the machine
        /// or session selection (e.g. a reference source that must always
        /// read production).</summary>
        public string? Dataset => (string?)El.Attribute("dataset");
        /// <summary>BAQ id (epicor connections); analogous name for other
        /// connection types (table/view, endpoint...).</summary>
        public string? Baq => (string?)El.Attribute("baq");
        public string? Query => (string?)El.Attribute("query");
        /// <summary>param-Xxx="literal or {FieldName}" → BAQ parameter Xxx.</summary>
        public Dictionary<string, string> Params => Prefixed("param-");
        /// <summary>filter-Col="literal or {FieldName}" → OData $filter Col eq value.</summary>
        public Dictionary<string, string> Filters => Prefixed("filter-");
        private Dictionary<string, string> Prefixed(string prefix) =>
            El.Attributes()
              .Where(a => a.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal))
              .ToDictionary(a => a.Name.LocalName[prefix.Length..], a => a.Value);
    }

    /// <summary>Data-panel presentation config (etiq:panel): which action
    /// buttons exist and where, how printing behaves, and whether copies /
    /// collation live directly on the form. Absent = the defaults below,
    /// which are exactly the historical behavior.</summary>
    public sealed record PanelDef(XElement? El)
    {
        private string? A(string n) => El is null ? null : (string?)El.Attribute(n);
        /// <summary>dialog (system print dialog) | direct (straight to the
        /// printer — labelprint behavior; see Printer).</summary>
        public string Print => A("print") ?? "dialog";
        /// <summary>direct printing: absent = machine default printer;
        /// a name = pinned; "embedded" = an on-form picker with a
        /// "Default printer" checkmark (labelprint-style).</summary>
        public string? Printer => A("printer");
        /// <summary>ask (copies dialog on batch) | embedded (count control
        /// on the form) | fixed:N.</summary>
        public string Copies => A("copies") ?? "ask";
        /// <summary>choose (selector on the form) | grouped (1-1-2-2) |
        /// sequenced (1-2-1-2) | ask (no selector — popup only when a run
        /// actually multiplies more than one page).</summary>
        public string Collate => A("collate") ?? "choose";
        /// <summary>Explicit input order for the data panel: comma list of
        /// field:Name / list:Name tokens. Unlisted inputs follow in
        /// declaration order.</summary>
        public string[] Order =>
            (A("order") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        /// <summary>Which action buttons exist, in order:
        /// preview, print, printall, clear.</summary>
        public string[] Buttons =>
            (A("buttons") ?? "preview,print,printall,clear")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        /// <summary>bottom (after the fields) | top.</summary>
        public string ButtonsAt => A("buttons-at") ?? "bottom";

        public int? FixedCopies =>
            Copies.StartsWith("fixed:") &&
            int.TryParse(Copies["fixed:".Length..], out int n) && n > 0 ? n : null;
    }

    /// <summary>A layer group: direct child &lt;g&gt; of the root with data-layer (convention 0.2).</summary>
    public sealed record Layer(XElement El)
    {
        public string Name => (string?)El.Attribute("data-layer") ?? "";
        public bool Locked => (string?)El.Attribute("data-locked") == "true";
        public string? PrintAttr => (string?)El.Attribute("data-print");
    }

    public sealed record Field(string Name, string Source, XElement El)
    {
        public string? Column => (string?)El.Attribute("column");
        public string? Caption => (string?)El.Attribute("caption");
        public string? Counter => (string?)El.Attribute("counter");
        public string? Value => (string?)El.Attribute("value");
        // 0.2 additions
        public string? Connection => (string?)El.Attribute("connection");
        /// <summary>Declared etiq:source this field reads its column from
        /// (source="epicor"); absent = the engine's single implicit source
        /// (legacy labelprint-style config).</summary>
        public string? From => (string?)El.Attribute("from");
        /// <summary>source=epicor: the operator may TYPE OVER the fetched
        /// value — a non-empty prompt entry wins, an empty one falls back
        /// to the remote pull (shown as ghost text in the data panel).</summary>
        public bool Override => (string?)El.Attribute("override") == "true";
        /// <summary>panel="hide": resolve as usual but show no input on the
        /// data panel (prompt/override/list fields).</summary>
        public bool PanelHide => (string?)El.Attribute("panel") == "hide";
        public string? Query => (string?)El.Attribute("query");
        public string? Pick => (string?)El.Attribute("pick");
        public string? FilePath => (string?)El.Attribute("path");
        public string? MatchColumn => (string?)El.Attribute("match-column");
        public string? MatchValue => (string?)El.Attribute("match-value");
        public string? ListRef => (string?)El.Attribute("list");
        public string? OnFail => (string?)El.Attribute("on-fail");
        public string? IfEmpty => (string?)El.Attribute("if-empty");
        public string? Required => (string?)El.Attribute("required");
        /// <summary>upper|lower — normalizes the resolved value (any
        /// non-compose field); prompt UIs may also mirror it while typing.</summary>
        public string? Case => (string?)El.Attribute("case");
        /// <summary>compose only: after composition, drop lines that are
        /// empty or whitespace-only (address-block blank suppression).</summary>
        public bool CollapseBlankLines =>
            (string?)El.Attribute("collapse-blank-lines") == "true";
        /// <summary>Variant compose: name of the field whose resolved value
        /// picks which etiq:variant's segments are used.</summary>
        public string? SwitchOn => (string?)El.Attribute("switch-on");
        public List<Seg> Segs { get; } = new();
        public List<Variant> Variants { get; } = new();
    }
    public sealed record BarcodeRect(XElement El)
    {
        public string Symbology => (string?)El.Attribute("data-barcode") ?? "";
        public string? FieldRef => (string?)El.Attribute("data-field");
        public string? FixedValue => (string?)El.Attribute("data-value");
        public string? Hri => (string?)El.Attribute("data-hri");
        public double? ModuleMils => ParseNum((string?)El.Attribute("data-module-mils"));
        public double W => ParseNum((string?)El.Attribute("width")) ?? 0;
        public double H => ParseNum((string?)El.Attribute("height")) ?? 0;
        public double X => ParseNum((string?)El.Attribute("x")) ?? 0;
        public double Y => ParseNum((string?)El.Attribute("y")) ?? 0;
    }

    public List<Field> Fields { get; } = new();
    public List<XElement> DynamicTexts { get; } = new();   // <text>/<tspan> with data-field
    public List<BarcodeRect> Barcodes { get; } = new();
    public List<MapDef> Maps { get; } = new();
    public List<ListDef> Lists { get; } = new();           // embedded pick lists
    public List<SourceDef> Sources { get; } = new();       // declared remote sources
    /// <summary>Data-panel presentation (etiq:panel); El null = defaults.</summary>
    public PanelDef Panel { get; }
    public List<Layer> Layers { get; } = new();            // root-child <g data-layer>

    private EtiqTemplate(string path, XDocument doc)
    {
        Path = path;
        Doc = doc;
        var root = doc.Root!;
        WidthAttr = (string?)root.Attribute("width");
        HeightAttr = (string?)root.Attribute("height");
        var vb = (string?)root.Attribute("viewBox");
        if (vb is not null)
        {
            var parts = vb.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ParseNum(s)).ToArray();
            if (parts.Length == 4 && parts.All(p => p.HasValue))
                ViewBox = parts.Select(p => p!.Value).ToArray();
        }

        foreach (var f in doc.Descendants(Ns + "field"))
        {
            var field = new Field((string?)f.Attribute("name") ?? "",
                                  (string?)f.Attribute("source") ?? "", f);
            foreach (var s in f.Elements(Ns + "seg"))
                field.Segs.Add(new Seg(s));
            foreach (var v in f.Elements(Ns + "variant"))
            {
                var variant = new Variant(v);
                foreach (var s in v.Elements(Ns + "seg"))
                    variant.Segs.Add(new Seg(s));
                field.Variants.Add(variant);
            }
            Fields.Add(field);
        }

        foreach (var m in doc.Descendants(Ns + "map"))
            Maps.Add(new MapDef(m));

        foreach (var src in doc.Descendants(Ns + "source"))
            Sources.Add(new SourceDef(src));

        Panel = new PanelDef(doc.Descendants(Ns + "panel").FirstOrDefault());

        foreach (var l in doc.Descendants(Ns + "list"))
        {
            var list = new ListDef(l);
            foreach (var row in l.Elements(Ns + "row"))
                list.Rows.Add(row.Attributes().ToDictionary(
                    a => a.Name.LocalName, a => a.Value));
            Lists.Add(list);
        }

        foreach (var g in root.Elements().Where(e =>
                     e.Name.LocalName == "g" && e.Attribute("data-layer") is not null))
            Layers.Add(new Layer(g));

        foreach (var el in doc.Descendants())
        {
            if (el.Attribute("data-field") is null && el.Attribute("data-barcode") is null)
                continue;
            string local = el.Name.LocalName;
            if (local is "text" or "tspan" && el.Attribute("data-field") is not null)
                DynamicTexts.Add(el);
            else if (local == "rect" && el.Attribute("data-barcode") is not null)
                Barcodes.Add(new BarcodeRect(el));
        }
    }

    public static EtiqTemplate Load(string path) =>
        new(path, XDocument.Load(path));

    public static EtiqTemplate Parse(string xml, string path = "<memory>") =>
        new(path, XDocument.Parse(xml));

    internal static double? ParseNum(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
