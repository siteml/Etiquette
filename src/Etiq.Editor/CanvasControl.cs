using Etiq.Editor.Core;

namespace Etiq.Editor;

public enum EditorMode { Design, Data }

/// <summary>
/// The label canvas. World units = the template's user units (mils); Zoom
/// maps world→screen. Hit-testing, handle math, snapping and undoable
/// edits all come from Etiq.Editor.Core — this control paints and routes
/// the mouse.
///
/// Selection model: click selects an object (and its whole enclosing
/// group); double-click drills into a single group member; Ctrl+click
/// toggles; dragging on empty canvas rubber-bands a marquee. Dragging any
/// selected object moves the whole selection as ONE undo entry, snapping
/// to other objects' edges/centers and the label bounds (magenta guides).
///
/// Design mode: full editing. Data mode: read-only preview; when
/// ResolvedValues is set, dynamic content renders resolved.
/// </summary>
public sealed class CanvasControl : Control
{
    private EditorDoc? _doc;
    private readonly GdiTextMeasurer _measurer = new();

    public EditorMode Mode { get; set; } = EditorMode.Design;
    public double Zoom { get; private set; } = 0.15;
    public PointF Pan { get; private set; } = new(20, 20);
    public IReadOnlyDictionary<string, string>? ResolvedValues { get; set; }

    private readonly List<EditorObject> _sel = new();
    public IReadOnlyList<EditorObject> Selection => _sel;
    public EditorObject? Selected => _sel.Count > 0 ? _sel[0] : null;

    public event Action<EditorObject?>? SelectionChanged;
    public event Action<PointD>? CursorWorldMoved;

    // drag state
    private Core.Handle? _dragHandle;
    private int? _lineEnd;                      // 1 or 2: dragging a line endpoint
    // group-edit mode: set by double-click drill; clicks inside select
    // members, the FIRST click outside exits the mode without dragging
    private System.Xml.Linq.XElement? _editGroup;
    private bool _dragging, _panning, _marquee;
    // click-vs-drag: a mouse-down only ARMS the drag; it starts once the
    // pointer travels past a small screen-space threshold, so selecting an
    // element never nudges it by a pixel or two of hand jitter
    private bool _dragPending;
    private Point _downScreen;
    private Point _panLast;
    private PointD _dragStartW;
    private RectD _dragOrigBounds;
    private double _appliedDx, _appliedDy;
    private int _gesture;                       // undo merge-key generation
    private PointD _marqueeEndW;
    private List<SnapGuide> _guides = new();

    public CanvasControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(55, 55, 60);
    }

    public EditorDoc? Doc
    {
        get => _doc;
        set
        {
            if (_doc is not null) _doc.Undo.Changed -= Invalidate;
            _doc = value;
            _sel.Clear();
            if (_doc is not null)
            {
                _doc.Undo.Changed += Invalidate;
                FitToWindow();
            }
            SelectionChanged?.Invoke(null);
            Invalidate();
        }
    }

    public void FitToWindow()
    {
        if (_doc is null || Width < 40 || Height < 40) return;
        var vb = _doc.ViewBox;
        if (vb.W <= 0 || vb.H <= 0) return;
        Zoom = Math.Min((Width - 40.0) / vb.W, (Height - 40.0) / vb.H);
        Pan = new((float)((Width - vb.W * Zoom) / 2), (float)((Height - vb.H * Zoom) / 2));
        Invalidate();
    }

    public void Select(EditorObject? o)
    {
        _sel.Clear();
        if (o is not null) _sel.Add(o);
        SelectionChanged?.Invoke(Selected);
        Invalidate();
    }

    public void SelectMany(IEnumerable<EditorObject> objs)
    {
        _sel.Clear();
        _sel.AddRange(objs);
        SelectionChanged?.Invoke(Selected);
        Invalidate();
    }

    private PointD ToWorld(Point p) => new((p.X - Pan.X) / Zoom, (p.Y - Pan.Y) / Zoom);

    /// <summary>A selected object under the cursor, if any — selection
    /// overrides z-order so buried elements can be dug out via the outline
    /// and then dragged on the canvas. Skips detached (deleted) elements
    /// and objects on hidden/locked layers.</summary>
    private EditorObject? SelectionFirstHit(PointD w) =>
        _sel.FirstOrDefault(s => s.El.Parent is not null &&
            (s.Layer is not { } l || (l.Visible && !l.Locked)) &&
            s.HitTest(w, 3 / Zoom, _measurer));

    private bool InSelection(EditorObject o) => _sel.Any(s => s.El == o.El);

    private RectD SelectionBounds()
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach (var o in _sel)
        {
            var b = o.WorldBounds(_measurer); // rotation-aware
            x1 = Math.Min(x1, b.X); y1 = Math.Min(y1, b.Y);
            x2 = Math.Max(x2, b.Right); y2 = Math.Max(y2, b.Bottom);
        }
        return new(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Rotation-aware snap candidates: every visible unselected
    /// object's world bounds.</summary>
    private List<RectD> OthersWorldBounds() => _doc!.Objects
        .Where(o => !InSelection(o) && o.Layer?.Visible != false)
        .Select(o => o.WorldBounds(_measurer)).ToList();

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        if (_doc is null)
        {
            TextRenderer.DrawText(g, "File → Open a template (.svg)",
                Font, ClientRectangle, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var vb = _doc.ViewBox;
        var state = g.Save();
        g.TranslateTransform(Pan.X, Pan.Y);
        g.ScaleTransform((float)Zoom, (float)Zoom);

        g.FillRectangle(Brushes.Black, (float)vb.X + 60, (float)vb.Y + 60, (float)vb.W, (float)vb.H);
        g.FillRectangle(Brushes.White, (float)vb.X, (float)vb.Y, (float)vb.W, (float)vb.H);

        foreach (var o in _doc.Objects)
        {
            var layer = o.Layer;
            if (layer is not null && !layer.Visible) continue;
            if (Mode == EditorMode.Data && layer is not null && !layer.Printed) continue;
            DrawObject(g, o);
        }

        // group-edit mode cue: dashed outline around the group being edited
        if (Mode == EditorMode.Design && _editGroup is not null && _editGroup.Parent is not null)
        {
            double x1 = double.MaxValue, y1 = double.MaxValue,
                   x2 = double.MinValue, y2 = double.MinValue;
            foreach (var o in _doc.Objects.Where(o => o.El.Ancestors().Contains(_editGroup)))
            {
                var wb = o.WorldBounds(_measurer);
                x1 = Math.Min(x1, wb.X); y1 = Math.Min(y1, wb.Y);
                x2 = Math.Max(x2, wb.Right); y2 = Math.Max(y2, wb.Bottom);
            }
            if (x2 > x1)
            {
                using var ep = new Pen(Color.DarkOrange, (float)(1.5 / Zoom))
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                float pad = (float)(8 / Zoom);
                g.DrawRectangle(ep, (float)x1 - pad, (float)y1 - pad,
                    (float)(x2 - x1) + 2 * pad, (float)(y2 - y1) + 2 * pad);
            }
        }

        // snap guides (world space, across the whole label)
        if (Mode == EditorMode.Design && _guides.Count > 0)
        {
            using var gp = new Pen(Color.Magenta, (float)(1.0 / Zoom))
                { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            foreach (var gd in _guides)
            {
                if (gd.Vertical)
                    g.DrawLine(gp, (float)gd.Pos, (float)vb.Y, (float)gd.Pos, (float)vb.Bottom);
                else
                    g.DrawLine(gp, (float)vb.X, (float)gd.Pos, (float)vb.Right, (float)gd.Pos);
            }
        }

        g.Restore(state);

        if (Mode == EditorMode.Design)
        {
            foreach (var o in _sel)
                DrawSelection(g, o, primary: o == Selected && _sel.Count == 1);
            if (_marquee)
            {
                var a = new PointF((float)(_dragStartW.X * Zoom + Pan.X), (float)(_dragStartW.Y * Zoom + Pan.Y));
                var b = new PointF((float)(_marqueeEndW.X * Zoom + Pan.X), (float)(_marqueeEndW.Y * Zoom + Pan.Y));
                using var mp = new Pen(Color.DeepSkyBlue) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                g.DrawRectangle(mp, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                    Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            }
        }
    }

    private void DrawObject(Graphics g, EditorObject o)
    {
        // transform="rotate(a [x y])" applies to EVERY kind, not just text —
        // the selection box already rotates (DrawSelection), so the render
        // must match or rotated barcodes/boxes look broken.
        double objRot = o.RotationDeg;
        var rotState = g.Save();
        if (objRot != 0)
        {
            var opv = o.RotationPivot;
            g.TranslateTransform((float)opv.X, (float)opv.Y);
            g.RotateTransform((float)objRot);
            g.TranslateTransform((float)-opv.X, (float)-opv.Y);
        }
        try
        {
        switch (o.Kind)
        {
            case ObjectKind.Line:
            {
                using var pen = new Pen(Color.Black, (float)o.GetNum("stroke-width", 1));
                g.DrawLine(pen,
                    (float)o.GetNum("x1"), (float)o.GetNum("y1"),
                    (float)o.GetNum("x2"), (float)o.GetNum("y2"));
                break;
            }
            case ObjectKind.Box:
            {
                var b = o.Bounds(_measurer);
                string fill = (string?)o.El.Attribute("fill") ?? "none";
                if (fill != "none")
                    g.FillRectangle(Brushes.Black, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                using var pen = new Pen(Color.Black, (float)o.GetNum("stroke-width", 1));
                g.DrawRectangle(pen, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                break;
            }
            case ObjectKind.Barcode:
            {
                var b = o.Bounds(_measurer);
                string sym = (string?)o.El.Attribute("data-barcode") ?? "?";
                // draw the REAL symbol (fill-the-box, same rule as every
                // print path) so the canvas is honest about proportions;
                // unresolved fields encode a sample so the box reads true
                string content = ResolvedContent(o)
                    ?? (string?)o.El.Attribute("data-value")
                    ?? (string?)o.El.Attribute("data-field")
                    ?? "SAMPLE";
                bool drawn = LabelRenderer.DrawBarcode(g, b, sym, content,
                    (string?)o.El.Attribute("data-ecc"),
                    (int)o.GetNum("data-columns", 0),
                    (string?)o.El.Attribute("data-logo"),
                    _doc?.Path is { } dp ? System.IO.Path.GetDirectoryName(dp) : null,
                    (int)o.GetNum("data-logo-scale", 0),
                    (string?)o.El.Attribute("data-dmshape") == "rect",
                    (string?)o.El.Attribute("data-hri"));
                if (!drawn)
                {
                    using var hatch = new System.Drawing.Drawing2D.HatchBrush(
                        System.Drawing.Drawing2D.HatchStyle.NarrowVertical, Color.Black, Color.White);
                    g.FillRectangle(hatch, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                }
                if (Mode == EditorMode.Design)
                {
                    g.DrawRectangle(Pens.DimGray, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                    string note = LabelRenderer.IsImplemented(sym) ? "" : " (render not implemented yet)";
                    using var f = new Font("Arial", (float)Math.Max(b.H * 0.12, 8), GraphicsUnit.Pixel);
                    g.DrawString($"{sym}: {ResolvedContent(o) ?? content}{note}", f, Brushes.DimGray,
                        (float)b.X, (float)(b.Bottom + 4));
                }
                break;
            }
            case ObjectKind.Text:
            {
                // ONE text renderer for canvas and print (baseline-exact,
                // multiline, shrink squeeze, box alignment) - WYSIWYG by
                // construction; rotation is already on the Graphics above
                LabelRenderer.DrawText(g, o, ResolvedContent(o) ?? o.El.Value, _measurer);
                break;
            }
            case ObjectKind.Image:
            {
                var b = o.Bounds(_measurer);
                g.DrawRectangle(Pens.DarkGray, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                g.DrawLine(Pens.DarkGray, (float)b.X, (float)b.Y, (float)b.Right, (float)b.Bottom);
                g.DrawLine(Pens.DarkGray, (float)b.Right, (float)b.Y, (float)b.X, (float)b.Bottom);
                break;
            }
        }
        }
        finally
        {
            g.Restore(rotState);
        }
    }

    private string? ResolvedContent(EditorObject o)
    {
        if (ResolvedValues is null) return null;
        string? field = (string?)o.El.Attribute("data-field");
        return field is not null && ResolvedValues.TryGetValue(field, out var v) ? v : null;
    }

    private void DrawSelection(Graphics g, EditorObject o, bool primary)
    {
        var b = o.Bounds(_measurer);
        double rot = o.RotationDeg;
        var pv = o.RotationPivot;
        PointF W(PointD p)
        {
            var r = Geometry.Rotate(p, rot, pv);
            return new((float)(r.X * Zoom + Pan.X), (float)(r.Y * Zoom + Pan.Y));
        }
        using var pen = new Pen(primary ? Color.DodgerBlue : Color.SteelBlue, primary ? 1.4f : 1f)
            { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        var corners = new[]
        {
            W(new(b.X, b.Y)), W(new(b.Right, b.Y)),
            W(new(b.Right, b.Bottom)), W(new(b.X, b.Bottom)),
        };
        g.DrawPolygon(pen, corners);
        if (primary && o.Kind == ObjectKind.Line)
        {
            // lines resize by their endpoints - draw a handle on each
            foreach (var p in new PointD[]
                     { new(o.GetNum("x1"), o.GetNum("y1")), new(o.GetNum("x2"), o.GetNum("y2")) })
            {
                var q = W(p);
                g.FillRectangle(Brushes.White, q.X - 3.5f, q.Y - 3.5f, 7, 7);
                g.DrawRectangle(Pens.DodgerBlue, q.X - 3.5f, q.Y - 3.5f, 7, 7);
            }
            return;
        }
        if (!primary || o.Kind == ObjectKind.Line) return;
        foreach (Core.Handle h in Enum.GetValues<Core.Handle>())
        {
            if (h == Core.Handle.Rotate) continue;
            var p = W(Geometry.HandlePos(b, h));
            g.FillRectangle(Brushes.White, p.X - 3.5f, p.Y - 3.5f, 7, 7);
            g.DrawRectangle(Pens.DodgerBlue, p.X - 3.5f, p.Y - 3.5f, 7, 7);
        }
    }

    // ---------- mouse / keyboard ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_doc is null) return;
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true; _panLast = e.Location; return;
        }
        if (e.Button == MouseButtons.Right && Mode == EditorMode.Design)
        {
            var wr = ToWorld(e.Location);
            var rHit = SelectionFirstHit(wr) ?? _doc.HitTest(wr, 3 / Zoom);
            if (rHit is not null)
            {
                if (_editGroup is not null &&
                    (_editGroup.Parent is null || !rHit.El.Ancestors().Contains(_editGroup)))
                    _editGroup = null; // right-click outside also exits group edit
                if (_editGroup is not null)
                {
                    if (!InSelection(rHit)) Select(rHit); // member-level
                }
                else if (!InSelection(rHit))
                {
                    SelectMany(_doc.GroupMembers(rHit));
                }
                ShowObjectMenu(rHit, e.Location);
            }
            return;
        }
        if (e.Button != MouseButtons.Left || Mode != EditorMode.Design) return;

        var w = ToWorld(e.Location);
        _gesture++;
        double r = 6 / Zoom;

        // line endpoint handles: single selected line only
        if (_sel.Count == 1 && Selected!.Kind == ObjectKind.Line)
        {
            var p1 = new PointD(Selected.GetNum("x1"), Selected.GetNum("y1"));
            var p2 = new PointD(Selected.GetNum("x2"), Selected.GetNum("y2"));
            double D(PointD a) => Math.Sqrt((w.X - a.X) * (w.X - a.X) + (w.Y - a.Y) * (w.Y - a.Y));
            int? end = D(p1) <= r ? 1 : D(p2) <= r ? 2 : null;
            if (end is not null)
            {
                _lineEnd = end; _dragPending = true; _downScreen = e.Location; _dragStartW = w;
                Capture = true; return;
            }
        }

        // resize handles: single selection only
        if (_sel.Count == 1 && Selected!.Kind != ObjectKind.Line)
        {
            var h = Geometry.HitHandle(w, Selected.Bounds(_measurer),
                Selected.RotationDeg, Selected.RotationPivot, r);
            if (h is not null && h != Core.Handle.Rotate)
            {
                _dragHandle = h; _dragPending = true; _downScreen = e.Location; _dragStartW = w;
                Capture = true; return;
            }
        }

        // the CURRENT selection wins over z-order: an element selected from
        // the outline (or already selected) stays clickable/draggable even
        // when buried under others at this point
        var hit = SelectionFirstHit(w) ?? _doc.HitTest(w, 3 / Zoom);
        bool ctrl = ModifierKeys.HasFlag(Keys.Control);

        // group-edit mode bookkeeping: drop it when the group is gone
        // (deleted/undone), and EXIT it on the first click outside - that
        // click only selects, it never starts a drag (no accidental moves)
        if (_editGroup is not null && _editGroup.Parent is null) _editGroup = null;
        if (_editGroup is not null && hit is not null &&
            !hit.El.Ancestors().Contains(_editGroup))
        {
            _editGroup = null;
            if (!ctrl) SelectMany(_doc.GroupMembers(hit));
            Invalidate();
            return;
        }
        if (_editGroup is not null && hit is null) _editGroup = null;

        if (hit is null)
        {
            if (!ctrl) { _sel.Clear(); SelectionChanged?.Invoke(null); }
            _marquee = true; _dragStartW = w; _marqueeEndW = w;
            Capture = true; Invalidate();
            return;
        }

        if (_editGroup is not null)
        {
            // inside the edited group: clicks work on the UNITS at this
            // level — a direct member, or a nested subgroup as one piece
            var sub = UnitGroupUnder(_editGroup, hit);
            var unit = sub is not null
                ? sub.Descendants().Where(EditorObject.IsEditable)
                     .Select(EditorObject.Wrap).ToList()
                : new List<EditorObject> { hit };
            if (ctrl)
            {
                if (InSelection(hit)) _sel.RemoveAll(s => unit.Any(u => u.El == s.El));
                else _sel.AddRange(unit.Where(u => !InSelection(u)));
                SelectionChanged?.Invoke(Selected);
                Invalidate();
                return;
            }
            if (!InSelection(hit)) SelectMany(unit);
        }
        else
        {
            var members = _doc.GroupMembers(hit);
            if (ctrl)
            {
                // toggle the clicked group in/out of the selection
                if (InSelection(hit))
                    _sel.RemoveAll(s => members.Any(m => m.El == s.El));
                else
                    _sel.AddRange(members.Where(m => !InSelection(m)));
                SelectionChanged?.Invoke(Selected);
                Invalidate();
                return; // ctrl-click adjusts selection; no drag
            }
            if (!InSelection(hit))
                SelectMany(members);
        }

        _dragHandle = null; _dragPending = true; _downScreen = e.Location; _dragStartW = w;
        _dragOrigBounds = SelectionBounds();
        _appliedDx = _appliedDy = 0;
        Capture = true;
    }

    /// <summary>Right-click quick actions: the fast path for the properties
    /// the sidebar makes clunky.</summary>
    private void ShowObjectMenu(EditorObject o, Point at)
    {
        if (_doc is null) return;
        var m = new ContextMenuStrip();
        void Changed()
        {
            Invalidate();
            SelectionChanged?.Invoke(Selected); // keep the property grid live
        }
        // MULTI-selection menu: whole-selection operations, layout preserved
        if (_sel.Count > 1 && InSelection(o))
        {
            var rotSel = new ToolStripMenuItem("&Rotate Selection");
            foreach (var (caption, delta) in new[]
                     { ("90° clockwise", 90.0), ("180°", 180.0), ("90° counter-clockwise", -90.0) })
            {
                double d = delta;
                rotSel.DropDownItems.Add(caption, null, (_, _) =>
                {
                    _doc.RotateObjects(_sel.ToList(), d, SelectionBounds().Center);
                    Changed();
                });
            }
            m.Items.Add(rotSel);
            AddMoveToLayer(m, _sel.ToList());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("&Delete Selection", null, (_, _) =>
            {
                _doc.RemoveObjects(_sel.ToList());
                Select(null);
            });
            m.Show(this, at);
            return;
        }
        if (o.Kind == ObjectKind.Text)
        {
            m.Items.Add("Edit &Text…", null, (_, _) =>
            {
                string? t = Prompts.PromptText(FindForm()!, "Edit text (Enter = new line)",
                    o.El.Value, multiline: true);
                if (t is not null) { _doc.Undo.Push(o.SetText(t)); Changed(); }
            });
            var bold = new ToolStripMenuItem("&Bold") { Checked = o.Bold };
            bold.Click += (_, _) =>
            {
                _doc.Undo.Push(o.SetAttr("font-weight", o.Bold ? null : "bold", "toggle bold"));
                Changed();
            };
            m.Items.Add(bold);
            m.Items.Add("Font &Size…", null, (_, _) =>
            {
                string? s = Prompts.PromptText(FindForm()!, "Font size (mils)",
                    o.GetNum("font-size", 12).ToString("0.###"));
                if (s is not null && double.TryParse(s, out var v) && v > 0)
                { _doc.Undo.Push(o.SetAttr("font-size", s, "font size")); Changed(); }
            });
            // fit modes: dynamic width / squeeze-to-width / shrink-into-box
            var fit = new ToolStripMenuItem("F&it Mode");
            string effective = o.FitMode;
            void FitItem(string caption, string mode, Action apply)
            {
                var it = new ToolStripMenuItem(caption) { Checked = effective == mode };
                it.Click += (_, _) => { apply(); Changed(); };
                fit.DropDownItems.Add(it);
            }
            FitItem("&Dynamic Width (font locked; boxes clip)", "none", () =>
                _doc.Undo.Push(o.El.Attribute("data-width") is not null
                    ? o.SetAttr("data-fit", "none", "fit mode")
                    : o.SetAttr("data-fit", null, "fit mode")));
            FitItem("Fit &Width (squeeze into the Width box)", "width", () =>
            {
                var b0 = o.Bounds(_measurer);
                _doc.Undo.Push(EditCommand.SetAttrs(o.El, new()
                    {
                        ("data-fit", (string?)o.El.Attribute("data-fit"), null),
                        ("data-width", (string?)o.El.Attribute("data-width"),
                         (string?)o.El.Attribute("data-width") ?? b0.W.ToString("0.###")),
                    }, "fit mode"));
            });
            FitItem("Fixed &Box (shrink font to fit Width × Height)", "box", () =>
            {
                var b0 = o.Bounds(_measurer);
                _doc.Undo.Push(EditCommand.SetAttrs(o.El, new()
                    {
                        ("data-fit", (string?)o.El.Attribute("data-fit"), "box"),
                        ("data-width", (string?)o.El.Attribute("data-width"),
                         (string?)o.El.Attribute("data-width") ?? b0.W.ToString("0.###")),
                        ("data-height", (string?)o.El.Attribute("data-height"),
                         (string?)o.El.Attribute("data-height") ?? b0.H.ToString("0.###")),
                    }, "fit mode"));
            });
            fit.DropDownItems.Add(new ToolStripSeparator());
            var clear = new ToolStripMenuItem("&Clear boxes (back to natural size)")
                { Enabled = o.El.Attribute("data-width") is not null
                         || o.El.Attribute("data-height") is not null };
            clear.Click += (_, _) =>
            {
                _doc.Undo.Push(EditCommand.SetAttrs(o.El, new()
                    {
                        ("data-fit", (string?)o.El.Attribute("data-fit"), null),
                        ("data-width", (string?)o.El.Attribute("data-width"), null),
                        ("data-height", (string?)o.El.Attribute("data-height"), null),
                    }, "clear fit boxes"));
                Changed();
            };
            fit.DropDownItems.Add(clear);
            m.Items.Add(fit);
            if (o.El.Value.Contains('\n'))
                m.Items.Add("Split into Line &Stack", null, (_, _) =>
                {
                    // one element per line - plain-SVG-pure multiline
                    _doc.Undo.Push(o.SplitMultiline());
                    Select(null);
                });
            // box alignment: horizontal needs a Width box, vertical a Height box
            var align = new ToolStripMenuItem("&Align");
            foreach (var a in new[] { "left", "center", "right" })
            {
                var it = new ToolStripMenuItem(a)
                    { Checked = ((string?)o.El.Attribute("data-align") ?? "left") == a };
                string v = a;
                it.Click += (_, _) =>
                {
                    _doc.Undo.Push(o.SetAttr("data-align", v == "left" ? null : v, "align"));
                    Changed();
                };
                align.DropDownItems.Add(it);
            }
            if (o.El.Attribute("data-width") is null)
                align.DropDownItems.Add(new ToolStripMenuItem("(set a Width box first)") { Enabled = false });
            m.Items.Add(align);
            var valign = new ToolStripMenuItem("&Vertical Align");
            foreach (var a in new[] { "top", "middle", "bottom" })
            {
                var it = new ToolStripMenuItem(a)
                    { Checked = ((string?)o.El.Attribute("data-valign") ?? "top") == a };
                string v = a;
                it.Click += (_, _) =>
                {
                    _doc.Undo.Push(o.SetAttr("data-valign", v == "top" ? null : v, "valign"));
                    Changed();
                };
                valign.DropDownItems.Add(it);
            }
            if (o.El.Attribute("data-height") is null)
                valign.DropDownItems.Add(new ToolStripMenuItem("(set a Height box first)") { Enabled = false });
            m.Items.Add(valign);
            m.Items.Add(new ToolStripSeparator());
        }
        if (o.Kind is ObjectKind.Text or ObjectKind.Barcode)
        {
            var bind = new ToolStripMenuItem("Bind Fiel&d");
            void AddBind(string caption, string? value)
            {
                var it = new ToolStripMenuItem(caption)
                    { Checked = ((string?)o.El.Attribute("data-field") ?? "") == (value ?? "") };
                it.Click += (_, _) =>
                {
                    _doc.Undo.Push(o.SetAttr("data-field", value, "bind field"));
                    Changed();
                };
                bind.DropDownItems.Add(it);
            }
            AddBind("(static)", null);
            foreach (var n in FieldNameConverter.Names) AddBind("{" + n + "}", n);
            m.Items.Add(bind);
        }
        var rot = new ToolStripMenuItem("&Rotate");
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            var it = new ToolStripMenuItem($"{deg}°")
                { Checked = Math.Abs(o.RotationDeg - deg) < 0.01 };
            int d = deg;
            it.Click += (_, _) => { _doc.Undo.Push(o.SetRotation(d)); Changed(); };
            rot.DropDownItems.Add(it);
        }
        m.Items.Add(rot);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Bring &Forward", null, (_, _) => { _doc.ReorderZ(o, true); Invalidate(); });
        m.Items.Add("Send Back&ward", null, (_, _) => { _doc.ReorderZ(o, false); Invalidate(); });
        AddMoveToLayer(m, new List<EditorObject> { o });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("&Delete", null, (_, _) => { _doc.RemoveObjects(_sel.ToList()); Select(null); });
        m.Show(this, at);
    }

    /// <summary>"Move to Layer ▸" submenu — moves each object's whole group
    /// unit (a group lives in exactly one layer). Current layer shown
    /// checked; picking one is a single undo step.</summary>
    private void AddMoveToLayer(ContextMenuStrip m, List<EditorObject> objs)
    {
        if (_doc is null || _doc.Layers.Count < 2) return;
        var current = objs.Select(x => x.Layer?.El).Distinct().ToList();
        var sub = new ToolStripMenuItem("Move to La&yer");
        foreach (var l in _doc.Layers)
        {
            var target = l;
            var it = new ToolStripMenuItem(l.Name)
                { Checked = current.Count == 1 && current[0] == l.El };
            it.Click += (_, _) =>
            {
                _doc.MoveToLayer(objs, target);
                SelectionChanged?.Invoke(Selected);
                Invalidate();
            };
            sub.DropDownItems.Add(it);
        }
        m.Items.Add(sub);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // wheel zoom needs keyboard focus; take it when the pointer arrives
        // (only while our own window is active - never steal across apps,
        // and NEVER from the inline text editor: stealing its focus fires
        // LostFocus and closes it the moment the mouse re-enters the canvas)
        if (_inlineEdit is null && !Focused && FindForm() is { ContainsFocus: true }) Focus();
    }

    /// <summary>The selection UNIT under `context`: the outermost plain
    /// group STRICTLY below context on hit's ancestor chain, or null when
    /// hit is a direct leaf at this level. context = null means the layer
    /// level (equivalent to GroupContainer).</summary>
    private static System.Xml.Linq.XElement? UnitGroupUnder(
        System.Xml.Linq.XElement? context, EditorObject hit)
    {
        System.Xml.Linq.XElement? top = null;
        for (var p = hit.El.Parent; p is not null; p = p.Parent)
        {
            if (p == context) break;
            if (p.Name.LocalName != "g" || p.Attribute("data-layer") is not null) break;
            top = p;
        }
        return top;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_doc is null || Mode != EditorMode.Design) return;
        var hit = _doc.HitTest(ToWorld(e.Location), 3 / Zoom);
        if (hit is null) return;
        // Rule: each double-click drills ONE level deeper (groups nest);
        // at the leaf level a text object opens the edit prompt instead.
        var context = _editGroup is not null && hit.El.Ancestors().Contains(_editGroup)
            ? _editGroup : null;
        var sub = UnitGroupUnder(context, hit);
        if (sub is not null)
        {
            _editGroup = sub; // drilled one level
            Select(hit);
            Invalidate();
            return;
        }
        _editGroup = context; // leaf reached; stay at this level
        Select(hit);
        if (hit.Kind != ObjectKind.Text) return;
        StartInlineEdit(hit);   // edit-in-place, right on the canvas
    }

    // ---------- inline text editing ----------

    private TextBox? _inlineEdit;
    private EditorObject? _inlineTarget;

    /// <summary>Overlay a TextBox on the object's canvas footprint, font
    /// scaled to the current zoom. Enter = new line; Escape cancels;
    /// clicking away (focus loss) or Ctrl+Enter commits — one undo step.</summary>
    private void StartInlineEdit(EditorObject o)
    {
        EndInlineEdit(commit: false);
        var b = o.Bounds(_measurer);
        int x = (int)(b.X * Zoom + Pan.X);
        int y = (int)(b.Y * Zoom + Pan.Y);
        int w = Math.Max(80, (int)(b.W * Zoom) + 12);
        int h = Math.Max(26, (int)(b.H * Zoom) + 10);
        float fpx = (float)Math.Max(7, o.GetNum("font-size", 12) * Zoom);
        var tb = new TextBox
        {
            Multiline = true, AcceptsReturn = true, WordWrap = false,
            BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(x - 3, y - 3, w, h),
            // model stores \n; a WinForms TextBox only RENDERS \r\n breaks
            Text = o.El.Value.Replace("\n", "\r\n"),
        };
        try
        {
            tb.Font = new Font(o.FontFamily, fpx,
                o.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch { /* unknown family: keep the default font */ }
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                EndInlineEdit(commit: false);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && e.Control)
            {
                EndInlineEdit(commit: true);
                e.SuppressKeyPress = true;
            }
        };
        tb.LostFocus += (_, _) => EndInlineEdit(commit: true);
        _inlineEdit = tb;
        _inlineTarget = o;
        Controls.Add(tb);
        tb.BringToFront();
        tb.Focus();
        tb.SelectAll();
    }

    private void EndInlineEdit(bool commit)
    {
        if (_inlineEdit is null) return;
        var tb = _inlineEdit;
        var o = _inlineTarget;
        _inlineEdit = null;        // BEFORE removal: LostFocus re-enters here
        _inlineTarget = null;
        string text = tb.Text.Replace("\r\n", "\n");
        Controls.Remove(tb);
        var editFont = tb.Font;    // per-edit Font — TextBox.Dispose doesn't own it
        tb.Dispose();
        // unknown-family fallback leaves the AMBIENT font in place — never dispose that
        if (!ReferenceEquals(editFont, Font)) editFont.Dispose();
        if (commit && o is not null && _doc is not null && text != o.El.Value)
        {
            _doc.Undo.Push(o.SetText(text));
            SelectionChanged?.Invoke(Selected);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            Pan = new(Pan.X + e.X - _panLast.X, Pan.Y + e.Y - _panLast.Y);
            _panLast = e.Location; Invalidate(); return;
        }
        var w = ToWorld(e.Location);
        CursorWorldMoved?.Invoke(w);

        if (_marquee)
        {
            _marqueeEndW = w; Invalidate(); return;
        }
        if (_dragPending)
        {
            // still inside the click dead-zone: not a drag yet
            int tx = Math.Max(SystemInformation.DragSize.Width, 8) / 2;
            int ty = Math.Max(SystemInformation.DragSize.Height, 8) / 2;
            if (Math.Abs(e.X - _downScreen.X) <= tx &&
                Math.Abs(e.Y - _downScreen.Y) <= ty) return;
            _dragPending = false;
            _dragging = true;
        }
        if (!_dragging || _doc is null || _sel.Count == 0) return;

        // hold Alt to drop something EXACTLY where the mouse says —
        // disables both grid and element snapping for the gesture
        bool noSnap = ModifierKeys.HasFlag(Keys.Alt);

        // line endpoint drag: grid + element snapping on the point itself
        if (_lineEnd is int le && _sel.Count == 1 && Selected!.Kind == ObjectKind.Line)
        {
            var p = noSnap ? w : Geometry.Snap(w, _doc.GridMils);
            _guides = new();
            if (!noSnap)
            {
                var (sp, guides) = SnapEngine.SnapPoint(
                    p, OthersWorldBounds(), _doc.ViewBox, 6 / Zoom);
                p = sp; _guides = guides;
            }
            _doc.Undo.Push(Selected.SetLineEndpoint(le, p));
            Invalidate();
            return;
        }

        if (_dragHandle is Core.Handle h && _sel.Count == 1)
        {
            var s = Selected!;
            var local = Geometry.Rotate(w, -s.RotationDeg, s.RotationPivot);
            var snapped = noSnap ? local : Geometry.Snap(local, _doc.GridMils);
            _guides = new();
            // element snapping for the dragged edge(s): restrict to the axes
            // this handle actually moves. Rotated objects keep grid snapping
            // only (the handle moves in object space; edge candidates are in
            // world space, so mixing them would snap to the wrong lines).
            if (!noSnap && s.RotationDeg == 0)
            {
                bool sx = h is not (Core.Handle.N or Core.Handle.S);
                bool sy = h is not (Core.Handle.E or Core.Handle.W);
                var (sp, guides) = SnapEngine.SnapPoint(
                    snapped, OthersWorldBounds(), _doc.ViewBox, 6 / Zoom, sx, sy);
                snapped = sp; _guides = guides;
            }
            _doc.Undo.Push(s.Resize(
                Geometry.ResizeBy(s.Bounds(_measurer), h, snapped, min: 10), _measurer));
            Invalidate();
            return;
        }

        // group move with element snapping: work in TOTAL deltas from the
        // gesture start so snapping never accumulates drift
        double totalX = w.X - _dragStartW.X;
        double totalY = w.Y - _dragStartW.Y;
        if (!noSnap)
        {
            totalX = Geometry.Snap(totalX, _doc.GridMils);
            totalY = Geometry.Snap(totalY, _doc.GridMils);
            var target = new RectD(_dragOrigBounds.X + totalX, _dragOrigBounds.Y + totalY,
                                   _dragOrigBounds.W, _dragOrigBounds.H);
            var (adjX, adjY, guides) = SnapEngine.Adjust(
                target, OthersWorldBounds(), _doc.ViewBox, 6 / Zoom);
            _guides = guides;
            totalX += adjX; totalY += adjY;
        }
        else
        {
            _guides = new();
        }

        double incX = totalX - _appliedDx, incY = totalY - _appliedDy;
        if (incX != 0 || incY != 0)
        {
            _doc.MoveObjects(_sel, incX, incY, $"gmove:{_gesture}");
            _appliedDx = totalX; _appliedDy = totalY;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_marquee && _doc is not null)
        {
            var rect = RectD.FromCorners(_dragStartW, _marqueeEndW);
            var inside = _doc.Objects.Where(o =>
            {
                if (o.Layer is { } l && (!l.Visible || l.Locked)) return false;
                var b = o.WorldBounds(_measurer); // rotation-aware
                return b.X >= rect.X && b.Right <= rect.Right &&
                       b.Y >= rect.Y && b.Bottom <= rect.Bottom;
            }).ToList();
            if (ModifierKeys.HasFlag(Keys.Control))
                _sel.AddRange(inside.Where(o => !InSelection(o)));
            else if (inside.Count > 0)
                { _sel.Clear(); _sel.AddRange(inside); }
            SelectionChanged?.Invoke(Selected);
        }
        // tight-box barcodes (data-tight="1"): at the end of a resize
        // gesture, snap the box to the exact symbol the renderer draws —
        // same mergeKey as the drag, so the whole gesture is ONE undo step
        if (_dragging && _dragHandle is not null && _doc is not null && _sel.Count == 1
            && Selected!.Kind == ObjectKind.Barcode
            && (string?)Selected.El.Attribute("data-tight") == "1"
            && LabelRenderer.TightBarcodeRect(Selected, _measurer) is { } tight)
        {
            _doc.Undo.Push(Selected.Resize(tight, _measurer));
            SelectionChanged?.Invoke(Selected);
        }
        _dragging = false; _dragPending = false; _panning = false; _marquee = false; _dragHandle = null;
        _lineEnd = null;
        _guides = new();
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        EndInlineEdit(commit: true);   // the overlay doesn't follow zoom/pan
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var before = ToWorld(e.Location);
        Zoom = Math.Clamp(Zoom * factor, 0.01, 10);
        Pan = new((float)(e.X - before.X * Zoom), (float)(e.Y - before.Y * Zoom));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or (Keys.Shift | Keys.Up) or (Keys.Shift | Keys.Down)
            or (Keys.Shift | Keys.Left) or (Keys.Shift | Keys.Right)
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc is null || Mode != EditorMode.Design) return;
        double step = e.Shift ? 1 : 10;   // mils
        switch (e.KeyCode)
        {
            case Keys.Left when _sel.Count > 0: _doc.MoveObjects(_sel, -step, 0, $"nudge:{_gesture}"); break;
            case Keys.Right when _sel.Count > 0: _doc.MoveObjects(_sel, step, 0, $"nudge:{_gesture}"); break;
            case Keys.Up when _sel.Count > 0: _doc.MoveObjects(_sel, 0, -step, $"nudge:{_gesture}"); break;
            case Keys.Down when _sel.Count > 0: _doc.MoveObjects(_sel, 0, step, $"nudge:{_gesture}"); break;
            case Keys.Delete when _sel.Count > 0:
                _doc.RemoveObjects(_sel.ToList()); Select(null); break;
            case Keys.Escape:
                if (_editGroup is not null)
                {
                    // pop out ONE nesting level; null once at the top
                    var p = _editGroup.Parent;
                    _editGroup = p is not null && p.Name.LocalName == "g" &&
                                 p.Attribute("data-layer") is null ? p : null;
                    Invalidate();
                }
                else Select(null);
                break;
            case Keys.A when e.Control:
                SelectMany(_doc.Objects.Where(o => o.Layer?.Locked != true)); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _measurer.Dispose();
        base.Dispose(disposing);
    }
}
