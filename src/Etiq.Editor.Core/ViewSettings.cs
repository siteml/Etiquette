using System.Globalization;
using System.Xml.Linq;
using U = Etiq.Editor.Core.Units;

namespace Etiq.Editor.Core;

/// <summary>One operator-placed guide: an infinite vertical (axis x) or
/// horizontal (axis y) line at `Pos` mils. Not an object, not a layer —
/// view state (docs/grid-guides.md).</summary>
public sealed record Guide(bool Vertical, double Pos, string? Name = null);

/// <summary>
/// Editor view state persisted in the template as
/// <c>&lt;etiq:view units= grid= dots-per-mm= target= show-grid= snap=
/// guides-locked=&gt;</c> with <c>&lt;etiq:guide axis= pos= name=/&gt;</c>
/// children (docs/grid-guides.md). Engines, validator and print paths
/// ignore the element entirely. Absent element = these defaults, and the
/// editor only writes it once the operator touches a view setting.
/// </summary>
public sealed class ViewSettings
{
    /// <summary>Display unit override; null = the app default.</summary>
    public DisplayUnit? Units { get; set; }

    /// <summary>"off" | a length ("50", "0.05in", "1mm"; bare = mils) | "dots".</summary>
    public string Grid { get; set; } = "off";

    /// <summary>Target head density; authoritative for `dots`. 0 = unknown.</summary>
    public double DotsPerMm { get; set; }

    /// <summary>Human label for where DotsPerMm came from. Display only.</summary>
    public string? Target { get; set; }

    public bool ShowGrid { get; set; } = true;
    public bool SnapGrid { get; set; } = true;
    public bool SnapGuides { get; set; } = true;
    public bool SnapObjects { get; set; } = true;
    public bool GuidesLocked { get; set; }
    public List<Guide> Guides { get; } = new();

    public bool IsDotGrid => string.Equals(Grid, "dots", StringComparison.OrdinalIgnoreCase);
    public bool GridOff => Grid == "" || string.Equals(Grid, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Grid pitch in mils; 0 when off or unresolvable (dots
    /// without a density, unparsable length).</summary>
    public double GridPitchMils()
    {
        if (GridOff) return 0;
        if (IsDotGrid) return U.DotPitchMils(DotsPerMm);
        return U.TryParseLength(Grid, DisplayUnit.Mils, out double m) && m > 0 ? m : 0;
    }

    /// <summary>Pitch actually used for hard snapping: 0 when snap-to-grid is off.</summary>
    public double SnapPitchMils() => SnapGrid ? GridPitchMils() : 0;

    public ViewSettings Clone()
    {
        var c = new ViewSettings
        {
            Units = Units, Grid = Grid, DotsPerMm = DotsPerMm, Target = Target,
            ShowGrid = ShowGrid, SnapGrid = SnapGrid, SnapGuides = SnapGuides,
            SnapObjects = SnapObjects, GuidesLocked = GuidesLocked,
        };
        c.Guides.AddRange(Guides);
        return c;
    }

    // ---- XML ----

    public static readonly XNamespace Ns = EditorDoc.EtiqNs;

    /// <summary>Parse an etiq:view element; null → defaults.</summary>
    public static ViewSettings Read(XElement? view)
    {
        var v = new ViewSettings();
        if (view is null) return v;
        var inv = CultureInfo.InvariantCulture;

        string? units = (string?)view.Attribute("units");
        if (!string.IsNullOrWhiteSpace(units)) v.Units = U.Parse(units);
        v.Grid = ((string?)view.Attribute("grid") ?? "off").Trim();
        if (double.TryParse((string?)view.Attribute("dots-per-mm"), NumberStyles.Float, inv, out double d) && d > 0)
            v.DotsPerMm = d;
        v.Target = (string?)view.Attribute("target");
        v.ShowGrid = Bool(view.Attribute("show-grid"), true);
        v.GuidesLocked = Bool(view.Attribute("guides-locked"), false);

        string? snap = (string?)view.Attribute("snap");
        if (snap is not null)
        {
            var set = snap.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(s => s.ToLowerInvariant()).ToHashSet();
            v.SnapGrid = set.Contains("grid");
            v.SnapGuides = set.Contains("guides");
            v.SnapObjects = set.Contains("objects");
        }
        foreach (var g in view.Elements(Ns + "guide"))
        {
            string axis = ((string?)g.Attribute("axis") ?? "x").Trim().ToLowerInvariant();
            if (!double.TryParse((string?)g.Attribute("pos"), NumberStyles.Float, inv, out double pos)) continue;
            v.Guides.Add(new Guide(axis != "y", pos, (string?)g.Attribute("name")));
        }
        return v;
    }

    public static readonly XNamespace Sodipodi = "http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd";

    /// <summary>Read-only import of Inkscape guides from
    /// <c>sodipodi:namedview/sodipodi:guide</c> when a template has no
    /// etiq:view of its own. Inkscape stores <c>position="x,y"</c> in user
    /// units with the origin at the page's BOTTOM-left (y up, whatever the
    /// UI's y-axis setting) and <c>orientation="nx,ny"</c> as the guide's
    /// normal: 1,0 = vertical line, 0,1 = horizontal. Angled guides are
    /// skipped. Nothing is ever written back to the namedview.</summary>
    public static ViewSettings FromInkscape(XElement root, RectD viewBox)
    {
        var v = new ViewSettings();
        var nv = root.Element(Sodipodi + "namedview");
        if (nv is null) return v;
        var inv = CultureInfo.InvariantCulture;
        foreach (var g in nv.Elements(Sodipodi + "guide"))
        {
            var pos = ((string?)g.Attribute("position") ?? "").Split(',');
            var ori = ((string?)g.Attribute("orientation") ?? "").Split(',');
            if (pos.Length != 2 || ori.Length != 2) continue;
            if (!double.TryParse(pos[0], NumberStyles.Float, inv, out double px) ||
                !double.TryParse(pos[1], NumberStyles.Float, inv, out double py) ||
                !double.TryParse(ori[0], NumberStyles.Float, inv, out double nx) ||
                !double.TryParse(ori[1], NumberStyles.Float, inv, out double ny)) continue;
            bool vertical = Math.Abs(nx) > 0.999 && Math.Abs(ny) < 0.001;
            bool horizontal = Math.Abs(ny) > 0.999 && Math.Abs(nx) < 0.001;
            if (!vertical && !horizontal) continue;      // angled: not supported
            string? name = (string?)g.Attribute(XNamespace.Get("http://www.inkscape.org/namespaces/inkscape") + "label");
            v.Guides.Add(vertical
                ? new Guide(true, viewBox.X + px, name)
                : new Guide(false, viewBox.Y + viewBox.H - py, name));   // flip y-up → y-down
        }
        return v;
    }

    /// <summary>Serialize. Only non-default attributes are written so an
    /// untouched view stays small; guides always write `pos` in mils.</summary>
    public XElement ToElement()
    {
        var e = new XElement(Ns + "view");
        if (Units is DisplayUnit u) e.SetAttributeValue("units", U.Name(u));
        if (!GridOff) e.SetAttributeValue("grid", Grid);
        if (DotsPerMm > 0) e.SetAttributeValue("dots-per-mm", Num.F(DotsPerMm));
        if (!string.IsNullOrWhiteSpace(Target)) e.SetAttributeValue("target", Target);
        if (!ShowGrid) e.SetAttributeValue("show-grid", "false");
        if (!(SnapGrid && SnapGuides && SnapObjects))
        {
            var parts = new List<string>();
            if (SnapGrid) parts.Add("grid");
            if (SnapGuides) parts.Add("guides");
            if (SnapObjects) parts.Add("objects");
            e.SetAttributeValue("snap", parts.Count == 0 ? "none" : string.Join(",", parts));
        }
        if (GuidesLocked) e.SetAttributeValue("guides-locked", "true");
        foreach (var g in Guides)
        {
            var ge = new XElement(Ns + "guide",
                new XAttribute("axis", g.Vertical ? "x" : "y"),
                new XAttribute("pos", Num.F(g.Pos)));
            if (!string.IsNullOrWhiteSpace(g.Name)) ge.SetAttributeValue("name", g.Name);
            e.Add(ge);
        }
        return e;
    }

    /// <summary>True when nothing differs from the defaults — the element
    /// need not exist at all.</summary>
    public bool IsDefault =>
        Units is null && GridOff && DotsPerMm <= 0 && string.IsNullOrWhiteSpace(Target) &&
        ShowGrid && SnapGrid && SnapGuides && SnapObjects && !GuidesLocked && Guides.Count == 0;

    private static bool Bool(XAttribute? a, bool dflt) =>
        a is null ? dflt : !string.Equals(a.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
