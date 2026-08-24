using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Etiq.Editor.Core;

/// <summary>Editable object kinds (the MVP object model).</summary>
public enum ObjectKind { Text, Barcode, Line, Box, Image }

/// <summary>
/// Typed view over one editable SVG element. All geometry is read from and
/// written to the element's attributes — the wrapper holds no state of its
/// own, so any number of wrappers over the same XElement stay consistent.
/// Writes go through EditCommand so they are undoable.
/// </summary>
public sealed class EditorObject
{
    public XElement El { get; }
    public ObjectKind Kind { get; }

    private EditorObject(XElement el, ObjectKind kind) { El = el; Kind = kind; }

    public static bool IsEditable(XElement el) => Classify(el) is not null;

    public static EditorObject Wrap(XElement el) =>
        new(el, Classify(el) ?? throw new ArgumentException("not an editable element"));

    private static ObjectKind? Classify(XElement el) => el.Name.LocalName switch
    {
        "text" => ObjectKind.Text,
        "rect" when el.Attribute("data-barcode") is not null => ObjectKind.Barcode,
        "rect" => ObjectKind.Box,
        "line" => ObjectKind.Line,
        "image" => ObjectKind.Image,
        _ => null,
    };

    /// <summary>The layer group this object sits under (null = anonymous).</summary>
    public EditorLayer? Layer
    {
        get
        {
            for (var p = El.Parent; p is not null; p = p.Parent)
                if (p.Name.LocalName == "g" && p.Attribute("data-layer") is not null)
                    return new EditorLayer(p);
            return null;
        }
    }

    // ---- geometry (user units) ----

    public double GetNum(string attr, double dflt = 0) =>
        double.TryParse((string?)El.Attribute(attr), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : dflt;

    /// <summary>Axis-aligned bounds in object space (before rotation).
    /// Text uses data-width when present, else an estimate via the injected
    /// measurer (the WinForms shell supplies real GDI metrics).</summary>
    public RectD Bounds(ITextMeasurer? measurer = null)
    {
        switch (Kind)
        {
            case ObjectKind.Barcode or ObjectKind.Box or ObjectKind.Image:
                return new(GetNum("x"), GetNum("y"), GetNum("width"), GetNum("height"));
            case ObjectKind.Line:
                return RectD.FromCorners(new(GetNum("x1"), GetNum("y1")),
                                         new(GetNum("x2"), GetNum("y2")));
            default: // Text: baseline at y, box depends on anchor + width;
                     // multiline content ("\n") stacks lines by data-line-height
            {
                double size = GetNum("font-size", 12);
                var m = measurer ?? HeuristicTextMeasurer.Instance;
                var lines = El.Value.Split('\n');
                double natural = 0;
                foreach (var l in lines)
                    natural = Math.Max(natural, m.Width(l, size, FontFamily, Bold));
                double w = GetNum("data-width", natural);
                double lineH = GetNum("data-line-height", size * 1.2);
                double h = size + (lines.Length - 1) * lineH;
                double boxH = GetNum("data-height", 0);
                // fixed-box fit: the box IS the bounds (content shrinks into
                // it). fit "none" with a data-height: the box IS the bounds
                // too — content CLIPS to it, so the handles must sit on the
                // clip edge (a shorter box than the text is legal). Only
                // "width" keeps Max: there data-height is valign room.
                h = boxH > 0 && FitMode is "box" or "none" ? boxH : Math.Max(h, boxH);
                double x = GetNum("x");
                double topY = GetNum("y") - size * 0.8;   // first baseline → approx top
                return TextAnchor switch
                {
                    "middle" => new(x - w / 2, topY, w, h),
                    "end" => new(x - w, topY, w, h),
                    _ => new(x, topY, w, h),
                };
            }
        }
    }

    /// <summary>Axis-aligned bounds in WORLD space: the AABB of the rotated
    /// object-space bounds. Use for snapping, group bounds and marquee tests
    /// so rotated objects align by the edges the user actually sees.</summary>
    public RectD WorldBounds(ITextMeasurer? measurer = null)
    {
        var b = Bounds(measurer);
        double rot = RotationDeg;
        if (rot == 0) return b;
        var pv = RotationPivot;
        double x1 = double.MaxValue, y1 = double.MaxValue,
               x2 = double.MinValue, y2 = double.MinValue;
        foreach (var c in new PointD[]
                 { new(b.X, b.Y), new(b.Right, b.Y), new(b.X, b.Bottom), new(b.Right, b.Bottom) })
        {
            var r = Geometry.Rotate(c, rot, pv);
            x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y);
            x2 = Math.Max(x2, r.X); y2 = Math.Max(y2, r.Y);
        }
        return new(x1, y1, x2 - x1, y2 - y1);
    }

    public string FontFamily => (string?)El.Attribute("font-family") ?? "Arial";
    public bool Bold => (string?)El.Attribute("font-weight") is "bold" or "700";
    public string TextAnchor => (string?)El.Attribute("text-anchor") ?? "start";

    /// <summary>Text fit mode: "none" (dynamic width — box hugs the text,
    /// font never changes), "width" (data-width box; overlong text squeezes
    /// horizontally), "box" (data-width × data-height box; font shrinks
    /// uniformly to fit BOTH). Explicit data-fit wins; otherwise inferred:
    /// data-width present → width, else none. "box" is never inferred —
    /// existing labels using data-height purely for vertical alignment keep
    /// their behavior.</summary>
    public string FitMode
    {
        get
        {
            string? f = (string?)El.Attribute("data-fit");
            if (f is "none" or "width" or "box") return f;
            return El.Attribute("data-width") is not null ? "width" : "none";
        }
    }

    private static readonly Regex RotateRx = new(
        @"^\s*rotate\(\s*(-?[\d.]+)(?:[\s,]+(-?[\d.]+)[\s,]+(-?[\d.]+))?\s*\)\s*$");

    /// <summary>Rotation from transform="rotate(a [x y])"; 0 when absent or
    /// any other transform form (editor leaves those untouched).</summary>
    public double RotationDeg
    {
        get
        {
            var m = RotateRx.Match((string?)El.Attribute("transform") ?? "");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
    }

    public PointD RotationPivot
    {
        get
        {
            var m = RotateRx.Match((string?)El.Attribute("transform") ?? "");
            if (m.Success && m.Groups[2].Success)
                return new(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                           double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            return Bounds().Center;
        }
    }

    public bool HitTest(PointD p, double pad = 2, ITextMeasurer? measurer = null)
    {
        if (Kind == ObjectKind.Line)
            return Geometry.DistToSegment(p,
                new(GetNum("x1"), GetNum("y1")), new(GetNum("x2"), GetNum("y2")))
                <= Math.Max(pad, GetNum("stroke-width", 1) / 2 + pad);
        return Geometry.HitRotatedRect(p, Bounds(measurer), RotationDeg, RotationPivot, pad);
    }

    // ---- undoable edits ----

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Translate by (dx,dy). Mergeable: consecutive moves of the
    /// same object collapse into one undo step (a drag = one undo).</summary>
    public EditCommand Move(double dx, double dy)
    {
        string[] attrs = Kind == ObjectKind.Line
            ? new[] { "x1", "y1", "x2", "y2" }
            : new[] { "x", "y" };
        var changes = new List<(string Attr, string? Old, string? New)>();
        foreach (var a in attrs)
        {
            double d = a.StartsWith('x') ? dx : dy;
            changes.Add((a, (string?)El.Attribute(a), N(GetNum(a) + d)));
        }
        // moving a rotated object moves its explicit pivot too
        var tr = (string?)El.Attribute("transform");
        var m = RotateRx.Match(tr ?? "");
        if (m.Success && m.Groups[2].Success)
        {
            var np = new PointD(
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + dx,
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + dy);
            changes.Add(("transform", tr,
                $"rotate({m.Groups[1].Value} {N(np.X)} {N(np.Y)})"));
        }
        return EditCommand.SetAttrs(El, changes, $"move {Kind}", mergeKey: $"move:{ElId()}");
    }

    /// <summary>Resize to new bounds. Text: a WIDTH change sets data-width
    /// (the shrink box) and a HEIGHT change scales font-size — and only the
    /// axes that actually moved are written, so a vertical font-scale drag
    /// never accidentally locks a natural-width label into a data-width
    /// box. Lines resize by their endpoints instead.</summary>
    public EditCommand Resize(RectD r, ITextMeasurer? measurer = null)
    {
        if (Kind == ObjectKind.Line)
            throw new InvalidOperationException("resize a line by its endpoints");
        if (Kind != ObjectKind.Text)
            return EditCommand.SetAttrs(El, new()
                {
                    ("x", (string?)El.Attribute("x"), N(r.X)),
                    ("y", (string?)El.Attribute("y"), N(r.Y)),
                    ("width", (string?)El.Attribute("width"), N(r.W)),
                    ("height", (string?)El.Attribute("height"), N(r.H)),
                }, $"resize {Kind}", mergeKey: $"resize:{ElId()}");

        var cur = Bounds(measurer);
        var changes = new List<(string, string?, string?)>();
        if (Math.Abs(r.W - cur.W) > 0.01)
        {
            changes.Add(("x", (string?)El.Attribute("x"), N(TextAnchor switch
                { "middle" => r.X + r.W / 2, "end" => r.Right, _ => r.X })));
            changes.Add(("data-width", (string?)El.Attribute("data-width"), N(r.W)));
        }
        if (Math.Abs(r.H - cur.H) > 0.01)
        {
            // when a data-height box exists, a vertical drag sizes the BOX
            // (valign room); otherwise it scales the font. Explicit
            // data-fit="none" LOCKS the font: the drag always sizes the box
            // (creating it), never touches font-size. The baseline must
            // follow the new TOP edge either way — otherwise a top-edge drag
            // grows the box downward (the bottom edge chases the cursor).
            if (El.Attribute("data-height") is not null ||
                (string?)El.Attribute("data-fit") == "none")
            {
                changes.Add(("data-height", (string?)El.Attribute("data-height"), N(r.H)));
                changes.Add(("y", (string?)El.Attribute("y"),
                             N(r.Y + GetNum("font-size", 12) * 0.8)));
                return EditCommand.SetAttrs(El, changes, "resize text", mergeKey: $"resize:{ElId()}");
            }
            // scale the font by the height RATIO (multiline: bounds height
            // is size + (n-1)*lineHeight, not the em size itself)
            double factor = cur.H > 0 ? r.H / cur.H : 1;
            double size = Math.Max(6, GetNum("font-size", 12) * factor);
            changes.Add(("font-size", (string?)El.Attribute("font-size"), N(size)));
            if (El.Attribute("data-line-height") is not null)
                changes.Add(("data-line-height", (string?)El.Attribute("data-line-height"),
                             N(GetNum("data-line-height") * factor)));
            // keep the FIRST baseline consistent with the new top edge
            changes.Add(("y", (string?)El.Attribute("y"), N(r.Y + size * 0.8)));
        }
        if (changes.Count == 0)
            changes.Add(("x", (string?)El.Attribute("x"), (string?)El.Attribute("x")));
        return EditCommand.SetAttrs(El, changes, "resize text", mergeKey: $"resize:{ElId()}");
    }

    /// <summary>Move one endpoint of a line (which: 1 or 2). Mergeable so an
    /// endpoint drag collapses into one undo step.</summary>
    public EditCommand SetLineEndpoint(int which, PointD p)
    {
        if (Kind != ObjectKind.Line)
            throw new InvalidOperationException("endpoints only exist on lines");
        if (which is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(which));
        return EditCommand.SetAttrs(El, new()
            {
                ($"x{which}", (string?)El.Attribute($"x{which}"), N(p.X)),
                ($"y{which}", (string?)El.Attribute($"y{which}"), N(p.Y)),
            }, "move line endpoint", mergeKey: $"lineend{which}:{ElId()}");
    }

    /// <summary>Replace a multiline text element with a GROUP of single-line
    /// elements spaced by line-height — plain-SVG-pure multiline (foreign
    /// renderers show it correctly). A field-bound element's lines become
    /// data-line="0..n-1" so each shows one line of the resolved value
    /// (indexing happens after collapse-blank-lines, so blanks reflow).</summary>
    public EditCommand SplitMultiline()
    {
        if (Kind != ObjectKind.Text)
            throw new InvalidOperationException("only text splits into lines");
        var lines = El.Value.Split('\n');
        double size = GetNum("font-size", 12);
        double lineH = GetNum("data-line-height", size * 1.2);
        double y = GetNum("y");
        bool bound = El.Attribute("data-field") is not null;
        var g = new XElement(El.Name.Namespace + "g");
        for (int i = 0; i < lines.Length; i++)
        {
            var t = new XElement(El) { Value = lines[i] };
            // block-level attributes don't apply to a single line
            t.SetAttributeValue("data-line-height", null);
            t.SetAttributeValue("data-height", null);
            t.SetAttributeValue("data-valign", null);
            t.SetAttributeValue("y", N(y + i * lineH));
            if (bound) t.SetAttributeValue("data-line", i.ToString());
            g.Add(t);
        }
        var orig = El;
        return new EditCommand("split multiline text",
            doIt: () => orig.ReplaceWith(g),
            undoIt: () => g.ReplaceWith(orig));
    }

    public EditCommand SetRotation(double deg)
    {
        var pivot = Bounds().Center;
        string? newVal = deg == 0 ? null : $"rotate({N(deg)} {N(pivot.X)} {N(pivot.Y)})";
        return EditCommand.SetAttr(El, "transform", newVal, "rotate", mergeKey: $"rot:{ElId()}");
    }

    public EditCommand SetText(string value)
    {
        string old = El.Value;
        return new EditCommand("edit text",
            doIt: () => El.Value = value,
            undoIt: () => El.Value = old);
    }

    public EditCommand SetAttr(string attr, string? value, string label) =>
        EditCommand.SetAttr(El, attr, value, label);

    private string ElId() =>
        El.Attribute("id")?.Value ?? El.GetHashCode().ToString();
}

/// <summary>Text measurement abstraction: the WinForms shell supplies GDI
/// metrics; headless code uses the heuristic.</summary>
public interface ITextMeasurer
{
    double Width(string text, double fontSize, string family, bool bold);
}

public sealed class HeuristicTextMeasurer : ITextMeasurer
{
    public static readonly HeuristicTextMeasurer Instance = new();
    public double Width(string text, double fontSize, string family, bool bold) =>
        text.Length * fontSize * (bold ? 0.62 : 0.58);
}
