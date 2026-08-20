using System.ComponentModel;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>PropertyGrid adapter: exposes the selected object's attributes
/// as plain properties; every setter goes through the undo stack.</summary>
public sealed class ObjectProps
{
    private readonly EditorObject _o;
    private readonly EditorDoc _doc;

    public ObjectProps(EditorObject o, EditorDoc doc) { _o = o; _doc = doc; }

    private static string N(double v) =>
        v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private void Set(string attr, string? value, string label) =>
        _doc.Undo.Push(_o.SetAttr(attr, value, label));

    [Category("Object")] public string Kind => _o.Kind.ToString();
    [Category("Object")] public string Layer => _o.Layer?.Name ?? "(none)";

    [Category("Position")]
    public double X
    {
        get => _o.GetNum("x", _o.GetNum("x1"));
        set
        {
            // a line's position is its first endpoint - MOVE the whole line
            // so the second endpoint (and therefore Width/Height) stays linked
            if (_o.Kind == ObjectKind.Line)
                _doc.Undo.Push(_o.Move(value - _o.GetNum("x1"), 0));
            else Set("x", N(value), "set X");
        }
    }
    [Category("Position")]
    public double Y
    {
        get => _o.GetNum("y", _o.GetNum("y1"));
        set
        {
            if (_o.Kind == ObjectKind.Line)
                _doc.Undo.Push(_o.Move(0, value - _o.GetNum("y1")));
            else Set("y", N(value), "set Y");
        }
    }
    [Category("Position")]
    public double Rotation { get => _o.RotationDeg; set => _doc.Undo.Push(_o.SetRotation(value)); }

    [Category("Size")]
    public double Width
    {
        get => _o.Kind switch
        {
            ObjectKind.Text => _o.GetNum("data-width"),
            ObjectKind.Line => Math.Abs(_o.GetNum("x2") - _o.GetNum("x1")),
            _ => _o.GetNum("width"),
        };
        set
        {
            if (_o.Kind == ObjectKind.Line)
            {
                // keep the line's direction; grow/shrink toward endpoint 2
                double x1 = _o.GetNum("x1");
                double dir = _o.GetNum("x2") >= x1 ? 1 : -1;
                Set("x2", N(x1 + dir * Math.Abs(value)), "set width");
            }
            else Set(_o.Kind == ObjectKind.Text ? "data-width" : "width",
                     value <= 0 ? null : N(value), "set width");
        }
    }
    [Category("Size")]
    [Description("Text: the data-height box (0 = natural; needed for vertical alignment)")]
    public double Height
    {
        get => _o.Kind switch
        {
            ObjectKind.Line => Math.Abs(_o.GetNum("y2") - _o.GetNum("y1")),
            ObjectKind.Text => _o.GetNum("data-height"),
            _ => _o.GetNum("height"),
        };
        set
        {
            if (_o.Kind == ObjectKind.Line)
            {
                double y1 = _o.GetNum("y1");
                double dir = _o.GetNum("y2") >= y1 ? 1 : -1;
                Set("y2", N(y1 + dir * Math.Abs(value)), "set height");
            }
            else if (_o.Kind == ObjectKind.Text)
                Set("data-height", value <= 0 ? null : N(value), "set height");
            else Set("height", N(value), "set height");
        }
    }

    [Category("Text")]
    public string Text
    {
        get => _o.Kind == ObjectKind.Text ? _o.El.Value : "";
        set { if (_o.Kind == ObjectKind.Text) _doc.Undo.Push(_o.SetText(value)); }
    }
    [Category("Text")]
    public double FontSize { get => _o.GetNum("font-size", 12); set => Set("font-size", N(value), "set font size"); }
    [Category("Text")]
    public bool Bold { get => _o.Bold; set => Set("font-weight", value ? "bold" : null, "set bold"); }
    [Category("Text")]
    public string FontFamily { get => _o.FontFamily; set => Set("font-family", value, "set font"); }
    [Category("Text")]
    [Description("Baseline-to-baseline distance for multiline text (0 = 1.2 × font size)")]
    public double LineHeight
    {
        get => _o.GetNum("data-line-height");
        set => Set("data-line-height", value <= 0 ? null : N(value), "set line height");
    }
    [Category("Text")]
    [Description("left | center | right — per-line placement inside the Width box")]
    [TypeConverter(typeof(AlignConverter))]
    public string Align
    {
        get => (string?)_o.El.Attribute("data-align") ?? "";
        set => Set("data-align", value is "" or "left" ? null : value, "set align");
    }
    [Category("Text")]
    [Description("top | middle | bottom — block placement inside the Height box")]
    [TypeConverter(typeof(VAlignConverter))]
    public string VAlign
    {
        get => (string?)_o.El.Attribute("data-valign") ?? "";
        set => Set("data-valign", value is "" or "top" ? null : value, "set valign");
    }
    [Category("Text")]
    [Description("clip | shrink | wrap (needs data-height); empty = natural width")]
    public string Overflow
    {
        get => (string?)_o.El.Attribute("data-overflow") ?? "";
        set => Set("data-overflow", value == "" ? null : value, "set overflow");
    }
    [Category("Text")]
    [Description("Fit mode: none = font locked, Width/Height boxes hard-clip overlong text | width = squeeze into the Width box | box = shrink font to fit Width × Height. Empty = inferred (width when a Width box is set).")]
    [TypeConverter(typeof(FitConverter))]
    public string Fit
    {
        get => (string?)_o.El.Attribute("data-fit") ?? $"({_o.FitMode})";
        set => Set("data-fit",
            value is "" || value.StartsWith('(') ? null : value, "set fit mode");
    }

    [Category("Data")]
    [Description("Line-stack element: show only line N (0-based) of the field's value; empty = whole value")]
    public string Line
    {
        get => (string?)_o.El.Attribute("data-line") ?? "";
        set => Set("data-line", value == "" ? null : value, "set line index");
    }
    [Category("Data")]
    [Description("Declared etiq:field this element renders; empty = static")]
    [TypeConverter(typeof(FieldNameConverter))]
    public string DataField
    {
        get => (string?)_o.El.Attribute("data-field") ?? "";
        set => Set("data-field", value == "" ? null : value, "bind field");
    }
    [Category("Data")]
    [Description("Barcode symbology (code39|code39ext|code128|datamatrix|qr|pdf417|iqr)")]
    public string Barcode
    {
        get => (string?)_o.El.Attribute("data-barcode") ?? "";
        set { if (_o.Kind == ObjectKind.Barcode && value != "") Set("data-barcode", value, "set symbology"); }
    }
    [Category("Data")]
    public string ModuleMils
    {
        get => (string?)_o.El.Attribute("data-module-mils") ?? "";
        set => Set("data-module-mils", value == "" ? null : value, "set module mils");
    }
    [Category("Data")]
    [Description("QR error correction: L | M | Q | H (empty = M). Use H when a logo overlay is planned.")]
    public string Ecc
    {
        get => (string?)_o.El.Attribute("data-ecc") ?? "";
        set => Set("data-ecc", value == "" ? null : value.ToUpperInvariant(), "set qr ecc");
    }
    [Category("Data")]
    [Description("PDF417 data columns, 1-30 (empty = 6). More columns = wider, fewer rows.")]
    public string Columns
    {
        get => (string?)_o.El.Attribute("data-columns") ?? "";
        set => Set("data-columns", value == "" ? null : value, "set pdf417 columns");
    }
    [Category("Data")]
    [Description("QR center logo: \"etiq\" (built-in icon), an image file path (relative to the label file), or a data: URI. Forces ECC level H; overlay sized so the code stays scannable.")]
    public string Logo
    {
        get => (string?)_o.El.Attribute("data-logo") ?? "";
        set => Set("data-logo", value == "" ? null : value, "set qr logo");
    }
    [Category("Data")]
    [Description("Logo size as % of the reserved center box (25-130; empty = 100). Above 100 grows into the white margin ring — it can never overlap the code's modules.")]
    public string LogoScale
    {
        get => (string?)_o.El.Attribute("data-logo-scale") ?? "";
        set => Set("data-logo-scale", value == "" ? null : value, "set qr logo scale");
    }
}

/// <summary>PropertyGrid adapter for a MULTI-selection: shared properties
/// applied to every member as ONE undo entry. X/Y MOVE the whole selection
/// (internal layout preserved); RotateBy rotates it about the selection
/// center with layout intact (rotation composition per member).</summary>
public sealed class MultiProps
{
    private readonly IReadOnlyList<EditorObject> _objs;
    private readonly EditorDoc _doc;
    private readonly ITextMeasurer _measurer;

    public MultiProps(IReadOnlyList<EditorObject> objs, EditorDoc doc, ITextMeasurer measurer)
    {
        _objs = objs; _doc = doc; _measurer = measurer;
    }

    private static string N(double v) =>
        v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private RectD SelBounds()
    {
        double x1 = double.MaxValue, y1 = double.MaxValue,
               x2 = double.MinValue, y2 = double.MinValue;
        foreach (var o in _objs)
        {
            var b = o.WorldBounds(_measurer);
            x1 = Math.Min(x1, b.X); y1 = Math.Min(y1, b.Y);
            x2 = Math.Max(x2, b.Right); y2 = Math.Max(y2, b.Bottom);
        }
        return new(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Apply one attribute to matching members as ONE undo entry.</summary>
    private void SetAll(string attr, string? value, string label,
                        Func<EditorObject, bool>? filter = null)
    {
        var targets = _objs.Where(o => filter?.Invoke(o) ?? true).ToList();
        if (targets.Count == 0) return;
        _doc.Undo.Push(EditCommand.Combine(
            targets.Select(o => EditCommand.SetAttr(o.El, attr, value, label)).ToList(),
            $"{label} ({targets.Count} objects)"));
    }

    [Category("Selection")] public int Objects => _objs.Count;

    [Category("Position"),
     Description("Left edge of the selection; setting it MOVES everything together")]
    public double X
    {
        get => SelBounds().X;
        set => _doc.MoveObjects(_objs, value - SelBounds().X, 0);
    }

    [Category("Position"),
     Description("Top edge of the selection; setting it MOVES everything together")]
    public double Y
    {
        get => SelBounds().Y;
        set => _doc.MoveObjects(_objs, 0, value - SelBounds().Y);
    }

    [Category("Position"),
     Description("Rotate the whole selection by this many degrees (clockwise) about its center — layout preserved. Resets to 0 after applying.")]
    public double RotateBy
    {
        get => 0;
        set { if (value % 360 != 0) _doc.RotateObjects(_objs, value, SelBounds().Center); }
    }

    [Category("Text"),
     Description("Font size applied to every TEXT member (0 = leave unchanged)")]
    public double FontSize
    {
        get
        {
            var sizes = _objs.Where(o => o.Kind == ObjectKind.Text)
                             .Select(o => o.GetNum("font-size", 12)).Distinct().ToList();
            return sizes.Count == 1 ? sizes[0] : 0;
        }
        set
        {
            if (value > 0)
                SetAll("font-size", N(value), "set font size", o => o.Kind == ObjectKind.Text);
        }
    }

    [Category("Text"), Description("Bold applied to every TEXT member")]
    public bool Bold
    {
        get
        {
            var texts = _objs.Where(o => o.Kind == ObjectKind.Text).ToList();
            return texts.Count > 0 && texts.All(o => o.Bold);
        }
        set => SetAll("font-weight", value ? "bold" : null, "set bold",
                      o => o.Kind == ObjectKind.Text);
    }
}
