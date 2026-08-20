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
    public static readonly string[] Symbologies =
        { "code39", "code39ext", "code128", "datamatrix", "qr", "pdf417", "iqr" };
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
