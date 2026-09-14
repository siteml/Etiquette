using Etiq.Editor.Core;
using System.Xml.Linq;

namespace Etiq.Editor;

/// <summary>
/// Purpose-built replacement for the PropertyGrid: shows ONLY the rows
/// that apply to the selected object (even symbology-aware — Ecc/Logo for
/// qr, Columns for pdf417), edits through real controls (textboxes,
/// dropdowns, checkboxes), commits on Enter/focus-leave, reverts on
/// Escape, and nudges numbers with Up/Down (±1; Shift = ±10). Every
/// change goes through the undo stack exactly like before.
/// </summary>
public sealed class InspectorPanel : UserControl
{
    private TableLayoutPanel _table;                      // ACTIVE set (also the build target)
    private List<Action> _refreshers = new();             // active set's model → control re-readers
    private readonly ToolTip _tips = new();
    private readonly GdiTextMeasurer _measurer = new();
    private EditorDoc? _doc;
    private List<EditorObject> _objs = new();
    private bool _loading;                                // guard: setting control values

    // ---------- control-set cache ----------
    // Built panels are KEPT (hidden) and re-shown on reselect: switching
    // between recently used elements flips Visible + re-reads values
    // instead of destroying and recreating ~20 controls per click.
    private sealed class CachedSet
    {
        public TableLayoutPanel Table = null!;
        public List<Action> Refreshers = new();
    }
    private readonly Dictionary<string, CachedSet> _cache = new();
    private readonly List<string> _lru = new();           // oldest first
    private const int CacheMax = 12;
    private EditorDoc? _cacheDoc;

    /// <summary>Raised after any committed edit (canvas + outline refresh).</summary>
    public event Action? Changed;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _measurer.Dispose();
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    public InspectorPanel()
    {
        Ui.AutoScale(this);   // scales the fixed 96px label column etc. at high DPI
        AutoScroll = true;
        _table = NewTable();
        Controls.Add(_table);
    }

    private TableLayoutPanel NewTable()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 4, 4, 8),
        };
        // Control.Scale never touches absolute column styles — apply the
        // DPI/font factor by hand (Factor is 1 for the ctor placeholder,
        // correct for every real build, which happens parented)
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(96 * Ui.Factor(this))));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    // ---------- public API ----------

    private string _shape = "";   // what the row STRUCTURE was built from

    /// <summary>Rebuild the panel for the current selection. When the
    /// selection (and everything the row structure depends on) is unchanged
    /// — e.g. SelectionChanged re-fired after a context-menu edit or an
    /// inline text commit — skip the expensive rebuild and just re-read
    /// values into the existing controls.</summary>
    public void ShowSelection(EditorDoc? doc, IReadOnlyList<EditorObject> selection)
    {
        if (!ReferenceEquals(doc, _cacheDoc)) { ClearCache(); _cacheDoc = doc; }
        _doc = doc;
        _objs = doc is null ? new List<EditorObject>() : selection.ToList();
        Reshape();
    }

    /// <summary>Bring the visible row set in line with the current
    /// selection: same shape = value re-read only; known shape = swap in
    /// the cached set; new shape = build (once) and cache.</summary>
    private void Reshape()
    {
        string shape = Shape();
        if (shape == _shape) { RefreshValues(); return; }
        if (_cache.TryGetValue(shape, out var set)) Activate(shape, set);
        else Rebuild();
    }

    /// <summary>Swap in an already-built control set: no creation, no
    /// disposal — just visibility and a value re-read.</summary>
    private void Activate(string shape, CachedSet set)
    {
        SuspendLayout();
        var prev = _table;
        string prevShape = _shape;
        _table = set.Table;
        _refreshers = set.Refreshers;
        _shape = shape;
        Touch(shape);
        RefreshValues();                       // before showing: no stale flash
        set.Table.Visible = true;
        Retire(prev, prevShape);
        AutoScrollPosition = Point.Empty;
        ResumeLayout();
    }

    /// <summary>Hide an outgoing panel if the cache still owns it;
    /// dispose it if it was never cached (ctor placeholder, evicted).</summary>
    private void Retire(TableLayoutPanel prev, string prevShape)
    {
        if (ReferenceEquals(prev, _table)) return;
        if (_cache.TryGetValue(prevShape, out var ps) && ReferenceEquals(ps.Table, prev))
            prev.Visible = false;
        else
        {
            Controls.Remove(prev);
            prev.Dispose();
        }
    }

    private void Touch(string shape)
    {
        _lru.Remove(shape);
        _lru.Add(shape);
    }

    private void Evict()
    {
        while (_lru.Count > CacheMax)
        {
            string s = _lru[0];
            _lru.RemoveAt(0);
            if (_cache.Remove(s, out var set))
            {
                Controls.Remove(set.Table);
                set.Table.Dispose();
            }
        }
    }

    private void ClearCache()
    {
        foreach (var set in _cache.Values)
        {
            Controls.Remove(set.Table);
            set.Table.Dispose();
        }
        _cache.Clear();
        _lru.Clear();
        // the active table may have just been disposed with the cache
        if (!_table.IsDisposed) { Controls.Remove(_table); _table.Dispose(); }
        _table = NewTable();
        Controls.Add(_table);
        _refreshers = new List<Action>();
        _shape = "";
    }

    /// <summary>The object the active single-object rows edit. Row getters
    /// and setters resolve it AT CALL TIME (never capture a specific
    /// object), so one built panel serves EVERY element of that shape —
    /// selecting a different text element reuses the same controls.</summary>
    private EditorObject O => _objs[0];

    /// <summary>Text objects of the current multi selection, resolved at
    /// call time for the same reason as O.</summary>
    private List<EditorObject> Texts() =>
        _objs.Where(x => x.Kind == ObjectKind.Text).ToList();

    /// <summary>Signature of everything that decides WHICH rows exist —
    /// the element TYPE, not its identity: kind, symbology, QR logo mode
    /// (full value when embedded: the Extract caption bakes in the size),
    /// has-text for multi. Per-object values are read through O.</summary>
    private string Shape()
    {
        if (_doc is null || _objs.Count == 0) return "empty";
        if (_objs.Count > 1)
            return "multi|" + (Texts().Count > 0);
        var o = _objs[0];
        string logo = (string?)o.El.Attribute("data-logo") ?? "";
        string logoKey = logo is "" or "etiq" ? logo
            : logo.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? "data:" + logo.Length          // length, not the whole URI, as key
                : "custom";
        // every attribute that decides WHICH rows exist is part of the key
        // — the cached control set is shared by all objects of one shape,
        // and Reshape() after an edit only rebuilds when the key changes
        string hri = (string?)o.El.Attribute("data-hri") is "below" or "above" ? "hri" : "";
        string locked = (string?)o.El.Attribute("data-lock-size") == "1" ? "lock" : "";
        string exact = (string?)o.El.Attribute("data-module-lock") == "1" ? "exact" : "";
        string mono = o.GetNum("data-threshold", 0) is > 0 and < 100 ? "mono" : "";
        string img = o.Kind == ObjectKind.Image
            ? (LabelRenderer.ImageHref(o) ?? "").StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "embedded" : "linked"
            : "";
        return $"{o.Kind}|{(string?)o.El.Attribute("data-barcode")}|{logoKey}|{hri}|{locked}|{exact}|{mono}|{img}";
    }

    /// <summary>Re-read every value from the model (live drag / undo /
    /// redo) without rebuilding the controls.</summary>
    public void RefreshValues()
    {
        _loading = true;
        try
        {
            foreach (var r in _refreshers) r();
        }
        finally { _loading = false; }
    }

    // ---------- build ----------

    private const int WM_SETREDRAW = 0x000B;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void Rebuild()
    {
        // WM_SETREDRAW only when actually on screen: re-enabling it (TRUE)
        // sets the HWND's WS_VISIBLE style directly, so doing the dance on a
        // HIDDEN panel (Data mode: SelectionChanged fires while the
        // inspector is Visible=false) makes the native window visible while
        // WinForms still believes it is hidden — it then sits as a blank
        // gray sheet over the data pane until the next real Visible flip.
        bool redraw = IsHandleCreated && Visible;
        if (redraw) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        SuspendLayout();

        string shape = Shape();
        // an internal rebuild (symbology / logo-mode change) makes any
        // cached set for this shape stale — drop it before rebuilding
        if (_cache.Remove(shape, out var stale))
        {
            _lru.Remove(shape);
            Controls.Remove(stale.Table);
            stale.Table.Dispose();
        }

        var prev = _table;
        string prevShape = _shape;
        _table = NewTable();
        _refreshers = new List<Action>();
        _table.SuspendLayout();
        _table.Visible = false;                // add hidden, show when built

        if (_doc is null || _objs.Count == 0)
        {
            AddInfo("Nothing selected", "Click an object on the canvas.");
        }
        else if (_objs.Count > 1)
        {
            BuildMulti();
        }
        else
        {
            BuildSingle(_objs[0]);
        }

        _table.ResumeLayout();
        Controls.Add(_table);
        _cache[shape] = new CachedSet { Table = _table, Refreshers = _refreshers };
        _shape = shape;
        Touch(shape);
        Evict();
        RefreshValues();
        _table.Visible = true;
        Retire(prev, prevShape);
        AutoScrollPosition = Point.Empty;

        ResumeLayout();
        if (redraw)
        {
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            Refresh();
        }
    }

    // NOTE for every builder below: lambdas must reference the selection
    // through O / Texts() — never a captured EditorObject — so the cached
    // panel is reusable for any element of the same shape.
    private void BuildSingle(EditorObject o)
    {
        AddHeader(() => $"{O.Kind}   ·   layer {O.Layer?.Name ?? "(none)"}");

        // position (lines edit their endpoints instead)
        if (o.Kind == ObjectKind.Line)
        {
            AddLen("X1", () => O.GetNum("x1"), v => SetAttr(O, "x1", N(v), "set X1"));
            AddLen("Y1", () => O.GetNum("y1"), v => SetAttr(O, "y1", N(v), "set Y1"));
            AddLen("X2", () => O.GetNum("x2"), v => SetAttr(O, "x2", N(v), "set X2"));
            AddLen("Y2", () => O.GetNum("y2"), v => SetAttr(O, "y2", N(v), "set Y2"));
            AddLen("Stroke", () => O.GetNum("stroke-width", 1),
                v => SetAttr(O, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
            AddStrokeDots();
            return;
        }

        AddLen("X", () => O.GetNum("x"), v => SetAttr(O, "x", N(v), "set X"));
        AddLen("Y", () => O.GetNum("y"), v => SetAttr(O, "y", N(v), "set Y"));
        AddNum("Rotation", () => O.RotationDeg, v => Push(O.SetRotation(v)), step: 15);

        switch (o.Kind)
        {
            case ObjectKind.Text: BuildText(); break;
            case ObjectKind.Barcode: BuildBarcode(o); break;
            case ObjectKind.Box:
                AddLen("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
                AddLen("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));
                AddCheck("Filled", () => ((string?)O.El.Attribute("fill") ?? "none") != "none",
                    v => SetAttr(O, "fill", v ? "black" : null, "set fill"));
                AddLen("Stroke", () => O.GetNum("stroke-width", 1),
                    v => SetAttr(O, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
                AddStrokeDots();
                break;
            case ObjectKind.Image:
                AddLen("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
                AddLen("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));
                BuildImageRows();
                break;
        }
    }

    private void BuildText()
    {
        AddHeader("Text");
        // multiline content edits through the shared prompt (Enter = new line)
        AddButtonRow("Text", () => Snip(UnitPrefs.Redact && Redaction.IsSensitive(O.El)
                                          ? Redaction.Display(O.El, O.El.Value) : O.El.Value), "Edit…", () =>
        {
            string? t = Prompts.PromptText(FindForm()!, "Edit text (Enter = new line)",
                O.El.Value, multiline: true);
            if (t is not null) Push(O.SetText(t));
        });
        AddSensitive();
        AddClearBlank();
        AddCheck("Inverse", () => (string?)O.El.Attribute("data-plate") == "black",
            v =>
            {
                // white glyphs on a black plate the size of the text box —
                // one object, nothing to keep aligned
                SetAttr(O, "fill", v ? "white" : null, "inverse text");
                SetAttr(O, "data-plate", v ? "black" : null, "inverse text");
            },
            hint: "white text on a black plate (\"MASTER LOAD\"); the plate is the text box — give it a Width/Height for padding");
        AddCombo("Font", InstalledFonts(), () => O.FontFamily,
            v => SetAttr(O, "font-family", v == "" ? null : v, "set font"), editable: true);
        AddPt("Font size", () => O.GetNum("font-size", 12),
            v => SetAttr(O, "font-size", N(Math.Max(1, v)), "set font size"));
        AddCheck("Bold", () => O.Bold,
            v => SetAttr(O, "font-weight", v ? "bold" : null, "set bold"));
        AddLen("Line height", () => O.GetNum("data-line-height"),
            v => SetAttr(O, "data-line-height", v <= 0 ? null : N(v), "set line height"),
            allowEmpty: true, hint: "baseline-to-baseline; empty = 1.2 × font size");

        AddHeader("Fit box");
        // absent attributes show an explicit token, never a blank: a blank
        // reads as "nothing", but omission means "program default" — which a
        // future version may define differently from today's inference
        AddCombo("Fit mode", new[] { "auto", "none", "width", "box" },
            () => (string?)O.El.Attribute("data-fit") ?? "auto",
            v => SetAttr(O, "data-fit", v == "auto" ? null : v, "set fit mode"),
            hint: "auto = inferred: width if a box width is set, else none");
        AddLen("Width", () => O.GetNum("data-width"),
            v => SetAttr(O, "data-width", v <= 0 ? null : N(v), "set width"),
            allowEmpty: true, hint: "empty = natural width");
        AddLen("Height", () => O.GetNum("data-height"),
            v => SetAttr(O, "data-height", v <= 0 ? null : N(v), "set height"),
            allowEmpty: true, hint: "empty = natural height");
        AddCombo("Align", new[] { "default", "left", "center", "right" },
            () => (string?)O.El.Attribute("data-align") ?? "default",
            v => SetAttr(O, "data-align", v == "default" ? null : v, "set align"),
            hint: "default = left");
        AddCombo("Vert. align", new[] { "default", "top", "middle", "bottom" },
            () => (string?)O.El.Attribute("data-valign") ?? "default",
            v => SetAttr(O, "data-valign", v == "default" ? null : v, "set valign"),
            hint: "default = top");
        AddCombo("Overflow", new[] { "default", "shrink", "clip", "wrap" },
            () => (string?)O.El.Attribute("data-overflow") ?? "default",
            v => SetAttr(O, "data-overflow", v == "default" ? null : v, "set overflow"),
            hint: "default = shrink");

        AddHeader("Data");
        AddCombo("Field", FieldNames(), () => (string?)O.El.Attribute("data-field") ?? "",
            v => SetAttr(O, "data-field", v == "" ? null : v, "bind field"), editable: true);
        AddNum("Line #", () => O.GetNum("data-line", -1),
            v => SetAttr(O, "data-line", v < 0 ? null : ((int)v).ToString(), "set line index"),
            allowEmpty: true, unset: -1,
            hint: "Line-stack element: show only line N (0-based) of the field's value");
    }

    private void BuildBarcode(EditorObject o)
    {
        string sym = (string?)o.El.Attribute("data-barcode") ?? "";
        AddLen("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
        AddLen("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));

        AddSensitive();
        AddClearBlank();
        AddHeader("Barcode");
        AddCombo("Symbology", Etiq.Core.EtiqTemplate.Symbologies,
            () => (string?)O.El.Attribute("data-barcode") ?? "",
            v =>
            {
                if (v == "") return;
                SetAttr(O, "data-barcode", v, "set symbology");
                // data-symsize is read per symbology ("16x48" means nothing
                // to a QR) — a symbology change drops it
                SetAttr(O, "data-symsize", null, "set symbology");
                // deferred: the rebuild swaps out the combo raising this event
                BeginInvoke(new Action(Reshape));
            });
        AddCombo("Field", FieldNames(), () => (string?)O.El.Attribute("data-field") ?? "",
            v => SetAttr(O, "data-field", v == "" ? null : v, "bind field"), editable: true);
        AddText("Fixed value", () => (string?)O.El.Attribute("data-value") ?? "",
            v => SetAttr(O, "data-value", v == "" ? null : v, "set value"),
            hint: sym == "gs1-128"
                ? "GS1 AI syntax: (01)09501101530003(10)LOT42 — FNC1 separators are added automatically"
                : null);

        if (sym is "code128" or "code39" or "code39ext" or "gs1-128" or "itf14")
        {
            AddCombo("HRI", new[] { "", "none", "below", "above" },
                () => (string?)O.El.Attribute("data-hri") ?? "",
                v =>
                {
                    SetAttr(O, "data-hri", v == "" ? null : v, "set hri");
                    BeginInvoke(new Action(Reshape));   // the rows below come and go
                },
                hint: "human-readable text inside the box, under or above the bars; empty = none");
            if ((string?)O.El.Attribute("data-hri") is "below" or "above")
            {
                AddPt("HRI size", () => O.GetNum("data-hri-size", 0),
                    v => SetAttr(O, "data-hri-size", v <= 0 ? null : N(v), "set hri size"),
                    allowEmpty: true,
                    hint: "text height; empty = automatic (a quarter of the box, at most 0.15 in). The bars give up that much of the box");
                AddCombo("HRI align", new[] { "center", "left", "right" },
                    () => (string?)O.El.Attribute("data-hri-align") ?? "center",
                    v => SetAttr(O, "data-hri-align", v == "center" ? null : v, "set hri align"));
                AddCombo("HRI font", InstalledFonts(),
                    () => (string?)O.El.Attribute("data-hri-font") ?? "",
                    v => SetAttr(O, "data-hri-font", v == "" ? null : v, "set hri font"),
                    editable: true, hint: "empty = Arial; OCR-B if the spec asks for it and the font is installed");
                AddLen("HRI gap", () => O.GetNum("data-hri-gap", 0),
                    v => SetAttr(O, "data-hri-gap", v <= 0 ? null : N(v), "set hri gap"),
                    allowEmpty: true, hint: "clear space between the bars and the text; taken from the bars");
            }
        }

        // live feedback for symbologies whose encoding transforms or
        // validates the value — otherwise a wrong value silently shows the
        // placeholder and an added check digit is invisible (no HRI yet)
        if (sym == "itf14")
            AddStatus("Encodes", () =>
            {
                string v = (string?)O.El.Attribute("data-value") ?? "";
                if (v == "") return "(field value at print time)";
                if (!Etiq.Core.Itf.CanEncode(v)) return "✗ digits only";
                string n = Etiq.Core.Itf.Normalize(v);
                return n + (v.Length == 13 ? "   (check digit added)"
                    : n.Length != v.Length ? "   (zero-padded to even)" : "");
            }, hint: "exactly 13 digits get the GS1 check digit appended automatically");
        else if (sym == "gs1-128")
            AddStatus("Syntax", () =>
            {
                string v = (string?)O.El.Attribute("data-value") ?? "";
                if (v == "") return "(AI syntax, e.g. (01)09501101530003(10)LOT42)";
                return Etiq.Core.Gs1128.CanEncode(v)
                    ? "✓ valid — encodes FNC1 + AI stream"
                    : "✗ expected (01)09501101530003(10)LOT42 style (fixed-length AIs must match their defined length)";
            }, hint: "parenthesized GS1 Application Identifiers; separators are handled for you");
        // module size: a MINIMUM for the feasibility check by default; with
        // "Exact module" the symbol is drawn at exactly this X-dim, centered
        // in the box, whatever the content length (spec fidelity)
        AddLen("Module", () => O.GetNum("data-module-mils"),
            v => SetAttr(O, "data-module-mils", v <= 0 ? null : N(v), "set module mils"),
            allowEmpty: true,
            hint: "X-dimension (narrowest bar / one cell). Empty = whatever fills the box. Without Exact module it is a minimum the printer check enforces");
        bool exact0 = (string?)O.El.Attribute("data-module-lock") == "1";
        AddCheck("Exact module", () => (string?)O.El.Attribute("data-module-lock") == "1",
            v =>
            {
                SetAttr(O, "data-module-lock", v ? "1" : null, "set exact module");
                if (v && O.GetNum("data-module-mils") <= 0)
                {
                    // seed from what the box gives today so the symbol does not jump
                    double m0 = CurrentModule(sym);
                    if (m0 > 0) SetAttr(O, "data-module-mils", N(Math.Round(m0, 2)), "set module mils");
                }
                BeginInvoke(new Action(Reshape));
            },
            hint: "draw at exactly this module size, centered in the box, whatever the content; content too long for the box falls back to filling it");
        if (exact0)
            AddButtons(("Box from module", () =>
            {
                // width/height = modules × module for the current sample, and
                // lock the size — the box is then the printed size
                double m = O.GetNum("data-module-mils");
                if (m <= 0) return;
                var (cols, rows) = ModuleCounts(sym);
                if (cols <= 0) return;
                var changes = new List<(string, string?, string?)>
                {
                    ("width", (string?)O.El.Attribute("width"), N(cols * m)),
                    ("data-lock-size", (string?)O.El.Attribute("data-lock-size"), "1"),
                    ("data-tight", (string?)O.El.Attribute("data-tight"), null),
                };
                if (rows > 0)   // 2D: height follows too; linear keeps its bar height
                    changes.Add(("height", (string?)O.El.Attribute("height"), N(rows * m)));
                Push(EditCommand.SetAttrs(O.El, changes, "box from module"));
                BeginInvoke(new Action(Reshape));
            }));
        if (sym == "datamatrix")
            AddCheck("Rectangular", () => (string?)O.El.Attribute("data-dmshape") == "rect",
                v =>
                {
                    SetAttr(O, "data-dmshape", v ? "rect" : null, "set dm shape");
                    // shape change moves the symbol grid — keep a tight box tight
                    if ((string?)O.El.Attribute("data-tight") == "1"
                        && LabelRenderer.TightBarcodeRect(O, _measurer) is { } tr)
                        Push(O.Resize(tr, _measurer));
                },
                hint: "prefer the short-and-wide ECC200 rectangle formats (8x18 … 16x48); content too long for any rectangle falls back to a square");
        if (sym == "rmqr")
            AddCombo("ECC", new[] { "", "M", "H" },
                () => (string?)O.El.Attribute("data-ecc") ?? "",
                v => SetAttr(O, "data-ecc", v == "" ? null : v, "set rmqr ecc"),
                hint: "error correction level; empty = M. The symbol version follows the box aspect automatically.");
        bool locked = (string?)O.El.Attribute("data-lock-size") == "1";
        if (sym is ("qr" or "datamatrix" or "aztec" or "rmqr") && !locked)
            AddCheck("Tight box", () => (string?)O.El.Attribute("data-tight") == "1",
                v =>
                {
                    SetAttr(O, "data-tight", v ? "1" : null, "set tight box");
                    // enabling snaps immediately; resizes keep it snapped
                    if (v && LabelRenderer.TightBarcodeRect(O, _measurer) is { } r)
                        Push(O.Resize(r, _measurer));
                },
                hint: "keep the box snapped to the symbol's exact drawn size after every resize");
        // data-symsize: pin the symbol grid, whatever the content (a spec
        // that shows a "2×2" Data Matrix, a fixed QR version, …). Choices
        // and the stored value are per symbology; the renderer reads it.
        (string[] Choices, Func<string, string> Store, Func<string, string> Show, string Hint)? ss = sym switch
        {
            "datamatrix" => (Etiq.Core.DataMatrix.SquareSizes.Select(n => $"{n}x{n}")
                                .Concat(Etiq.Core.DataMatrix.RectNames.Select(r => r + " (rect)")).ToArray(),
                v => v.EndsWith(" (rect)") ? v[..^7] : v.Split('x')[0],
                v => v.Contains('x') ? v + " (rect)" : $"{v}x{v}",
                "square: pad to at least this ECC200 size — 32x32 and up print as 2×2 regions (the internal cross), 64x64 and up as 4×4. " +
                "rect: force that rectangular format"),
            "qr" => (Enumerable.Range(1, 40).Select(v => $"v{v} ({17 + 4 * v}x{17 + 4 * v})").ToArray(),
                v => v[1..v.IndexOf(' ')], v => $"v{v} ({17 + 4 * int.Parse(v)}x{17 + 4 * int.Parse(v)})",
                "pad to at least this QR version"),
            "aztec" => (Etiq.Core.Aztec.Sizes.Select(n => $"{n}x{n}").ToArray(),
                v => v.Split('x')[0], v => $"{v}x{v}",
                "pad to at least this many modules per side"),
            "rmqr" => (Etiq.Core.Rmqr.VersionNames.Select(n => "R" + n).ToArray(),
                v => v[1..], v => "R" + v,
                "force this rMQR version; content that won't fit falls back to the best fit for the box"),
            "pdf417" => (Enumerable.Range(3, 88).Select(n => $"{n} rows").ToArray(),
                v => v.Split(' ')[0], v => $"{v} rows",
                "pad to at least this many rows (columns are set above)"),
            _ => null,
        };
        if (ss is { } sz)
            AddCombo("Symbol size", new[] { "" }.Concat(sz.Choices).ToArray(),
                () =>
                {
                    // a value from another symbology (hand-edited file, older
                    // build) shows as "not set" rather than throwing
                    if ((string?)O.El.Attribute("data-symsize") is not { } d) return "";
                    try { string shown = sz.Show(d); return sz.Choices.Contains(shown) ? shown : ""; }
                    catch (FormatException) { return ""; }
                },
                v =>
                {
                    SetAttr(O, "data-symsize", v == "" ? null : sz.Store(v), "set symbol size");
                    if ((string?)O.El.Attribute("data-tight") == "1"
                        && LabelRenderer.TightBarcodeRect(O, _measurer) is { } tr)
                        Push(O.Resize(tr, _measurer));
                },
                hint: sz.Hint + "; empty = smallest that fits");
        // pin the printed dimensions: no resize handles, no tight-box snap;
        // Width/Height above are still the way to SET the size
        AddCheck("Lock size", () => (string?)O.El.Attribute("data-lock-size") == "1",   // live, not the captured local
            v =>
            {
                SetAttr(O, "data-lock-size", v ? "1" : null, "lock size");
                if (v) SetAttr(O, "data-tight", null, "lock size");
                BeginInvoke(new Action(Reshape));
            },
            hint: "keep this box at exactly the size set above: no resize handles, no tight-box snapping");
        AddInfo("Prints as", PrintedSizeInfo(sym));

        if (sym == "qr")
        {
            AddHeader("QR");
            AddCombo("ECC", new[] { "", "L", "M", "Q", "H" },
                () => (string?)O.El.Attribute("data-ecc") ?? "",
                v => SetAttr(O, "data-ecc", v == "" ? null : v, "set qr ecc"));
            BuildQrLogoRows();
        }
        else if (sym == "pdf417")
        {
            AddHeader("PDF417");
            AddNum("Columns", () => O.GetNum("data-columns"),
                v => SetAttr(O, "data-columns",
                    v <= 0 ? null : ((int)Math.Clamp(v, 1, 30)).ToString(), "set columns"),
                allowEmpty: true);
        }
    }

    private const string LogoModeNone = "QR only";
    private const string LogoModeEtiq = "Etiquette logo";
    private const string LogoModeCustom = "Custom image";

    /// <summary>QR logo UI: three distinct modes. Custom mode shows the
    /// source (path / URL) with Browse… + "Embed" (convert to a data: URI
    /// inside the template — no external dependency); an embedded image
    /// shows its size with "Extract…" to write it back out to a file.</summary>
    // dynamic re-reads so cached rows serve any QR element of the same mode
    private string CurLogo() => (string?)O.El.Attribute("data-logo") ?? "";
    private string CurLogoMode() => CurLogo() switch
    {
        "" => LogoModeNone,
        "etiq" => LogoModeEtiq,
        _ => LogoModeCustom,
    };
    private string? BaseDir() => _doc?.Path is { } dp ? Path.GetDirectoryName(dp) : null;

    private void BuildQrLogoRows()
    {
        // structure decided at BUILD time (part of the shape key)…
        string mode0 = CurLogoMode();
        bool embedded0 = CurLogo().StartsWith("data:", StringComparison.OrdinalIgnoreCase);

        AddCombo("Logo", new[] { LogoModeNone, LogoModeEtiq, LogoModeCustom },
            CurLogoMode, v =>
            {
                // …but handlers read the CURRENT object's state at call time
                string cur = CurLogo();
                string mode = CurLogoMode();
                if (v == mode) return;
                if (v == LogoModeNone)
                    Push(EditCommand.SetAttrs(O.El, new()
                        {
                            ("data-logo", cur == "" ? null : cur, null),
                            ("data-logo-scale", (string?)O.El.Attribute("data-logo-scale"), null),
                        }, "remove qr logo"));
                else if (v == LogoModeEtiq)
                    SetAttr(O, "data-logo", "etiq", "set qr logo");
                else
                {
                    // straight to the picker; cancel = stay in the old mode
                    string? picked = PickLogoFile(BaseDir());
                    if (picked is null) { RefreshValues(); return; }
                    SetAttr(O, "data-logo", picked, "set qr logo");
                }
                BeginInvoke(new Action(Reshape));   // rows depend on the mode
            }, hint: "A logo forces ECC H and QR version ≥2; sizing keeps the code scannable.");

        if (mode0 == LogoModeCustom && !embedded0)
        {
            AddText("Source", CurLogo,
                v =>
                {
                    if (v != "") SetAttr(O, "data-logo", v, "set qr logo");
                    BeginInvoke(new Action(Reshape));
                },
                hint: "image file path (absolute, or relative to the label file) or an http(s) URL");
            AddButtons(
                ("Browse…", () =>
                {
                    string? picked = PickLogoFile(BaseDir());
                    if (picked is null) return;
                    SetAttr(O, "data-logo", picked, "set qr logo");
                    BeginInvoke(new Action(Reshape));
                }
                ),
                ("Embed into template", () =>
                {
                    var bytes = LabelRenderer.FetchLogoBytes(CurLogo(), BaseDir());
                    if (bytes is null)
                    {
                        MessageBox.Show(FindForm(), "Could not read the logo image from its source.", "Embed");
                        return;
                    }
                    if (bytes.Length > 256 * 1024 && MessageBox.Show(FindForm(),
                            $"The image is {bytes.Length / 1024} KB — embedding grows the label file by ~{bytes.Length * 4 / 3 / 1024} KB. Continue?",
                            "Embed", MessageBoxButtons.OKCancel) != DialogResult.OK)
                        return;
                    SetAttr(O, "data-logo",
                        $"data:{SniffMime(bytes)};base64,{Convert.ToBase64String(bytes)}",
                        "embed qr logo");
                    BeginInvoke(new Action(Reshape));
                }
                ));
        }
        else if (mode0 == LogoModeCustom && embedded0)
        {
            // shape key carries the URI length, so this caption stays accurate
            int comma = CurLogo().IndexOf(',');
            int kb = comma > 0 ? (CurLogo().Length - comma) * 3 / 4 / 1024 : 0;
            AddButtons(($"Extract… (embedded, ~{Math.Max(1, kb)} KB)", () =>
            {
                string? baseDir = BaseDir();
                var bytes = LabelRenderer.FetchLogoBytes(CurLogo(), baseDir);
                if (bytes is null) return;
                using var dlg = new SaveFileDialog
                {
                    Title = "Extract embedded logo",
                    FileName = "logo" + ExtFor(SniffMime(bytes)),
                    Filter = "Image|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*",
                    InitialDirectory = baseDir ?? "",
                };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dlg.FileName, bytes);
                    if (MessageBox.Show(FindForm(),
                            "Point the template at the extracted file instead of the embedded copy?",
                            "Extract", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        SetAttr(O, "data-logo", Relativize(dlg.FileName, baseDir), "set qr logo");
                        BeginInvoke(new Action(Reshape));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), ex.Message, "Extract failed");
                }
            }
            ));
        }

        if (mode0 != LogoModeNone)
            AddNum("Logo scale %", () => O.GetNum("data-logo-scale"),
                v => SetAttr(O, "data-logo-scale",
                    v <= 0 ? null : ((int)Math.Clamp(v, 25, 130)).ToString(), "set logo scale"),
                step: 5, allowEmpty: true,
                hint: "empty = FILL (auto-scale to the safe limit); 25-130 = manual % of the reserved box");
    }

    // ---- <image> rows: source (path / URL / embedded) like the QR logo,
    // plus fit and black-and-white ----
    private string CurImage() => LabelRenderer.ImageHref(O) ?? "";
    private void SetImage(string? v, string what)
    {
        // write SVG 2 `href`; drop a legacy xlink:href so there is one source
        // ("{ns}local" is XName's string form — SetAttrs takes string names)
        const string xl = "{http://www.w3.org/1999/xlink}href";
        Push(EditCommand.SetAttrs(O.El, new()
        {
            ("href", (string?)O.El.Attribute("href"), v),
            (xl, (string?)O.El.Attribute(XName.Get(xl)), null),
        }, what));
    }

    private void BuildImageRows()
    {
        AddHeader("Image");
        bool embedded0 = CurImage().StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (!embedded0)
        {
            AddText("Source", CurImage,
                v => { if (v != "") { SetImage(v, "set image source"); BeginInvoke(new Action(Reshape)); } },
                hint: "image file path (absolute, or relative to the label file) or an http(s) URL");
            AddButtons(
                ("Browse…", () =>
                {
                    string? picked = PickLogoFile(BaseDir(), "Choose image");
                    if (picked is null) return;
                    SetImage(picked, "set image source");
                    BeginInvoke(new Action(Reshape));
                }),
                ("Embed into template", () =>
                {
                    var bytes = LabelRenderer.FetchLogoBytes(CurImage(), BaseDir());
                    if (bytes is null)
                    {
                        MessageBox.Show(FindForm(), "Could not read the image from its source.", "Embed");
                        return;
                    }
                    if (bytes.Length > 256 * 1024 && MessageBox.Show(FindForm(),
                            $"The image is {bytes.Length / 1024} KB — embedding grows the label file by ~{bytes.Length * 4 / 3 / 1024} KB. Continue?",
                            "Embed", MessageBoxButtons.OKCancel) != DialogResult.OK)
                        return;
                    SetImage($"data:{SniffMime(bytes)};base64,{Convert.ToBase64String(bytes)}", "embed image");
                    BeginInvoke(new Action(Reshape));
                }));
        }
        else
        {
            int comma = CurImage().IndexOf(',');
            int kb = comma > 0 ? (CurImage().Length - comma) * 3 / 4 / 1024 : 0;
            AddButtons(($"Extract… (embedded, ~{Math.Max(1, kb)} KB)", () =>
            {
                string? baseDir = BaseDir();
                var bytes = LabelRenderer.FetchLogoBytes(CurImage(), baseDir);
                if (bytes is null) return;
                using var dlg = new SaveFileDialog
                {
                    Title = "Extract embedded image",
                    FileName = "image" + ExtFor(SniffMime(bytes)),
                    Filter = "Image|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*",
                    InitialDirectory = baseDir ?? "",
                };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dlg.FileName, bytes);
                    if (MessageBox.Show(FindForm(),
                            "Point the template at the extracted file instead of the embedded copy?",
                            "Extract", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        SetImage(Relativize(dlg.FileName, baseDir), "set image source");
                        BeginInvoke(new Action(Reshape));
                    }
                }
                catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Extract failed"); }
            }));
        }
        AddCombo("Fit", new[] { "keep aspect", "stretch" },
            () => (string?)O.El.Attribute("preserveAspectRatio") == "none" ? "stretch" : "keep aspect",
            v => SetAttr(O, "preserveAspectRatio", v == "stretch" ? "none" : null, "set image fit"),
            hint: "keep aspect = largest size that fits the box, centered (SVG default); stretch = fill the box exactly");
        // colour printers exist (ColorWorks, VC-500W…): the picture goes to
        // the driver as-is unless a threshold is set
        bool mono0 = O.GetNum("data-threshold", 0) is > 0 and < 100;
        AddCheck("Black & white", () => O.GetNum("data-threshold", 0) is > 0 and < 100,
            v =>
            {
                SetAttr(O, "data-threshold", v ? "50" : null, "set image threshold");
                BeginInvoke(new Action(Reshape));
            },
            hint: "threshold every pixel to black or white — crisp logos on monochrome thermal printers, no driver dithering; off = the picture as is (colour printers print colour)");
        if (mono0)
            AddNum("Threshold %", () => O.GetNum("data-threshold", 50),
                v => SetAttr(O, "data-threshold", ((int)Math.Clamp(v, 1, 99)).ToString(), "set image threshold"),
                step: 5, hint: "luminance cut: pixels darker than this % print black, lighter print white. Lower = less black (thin/light logos), higher = more black");
        AddButtons(("Box from image aspect", () =>
        {
            // keep the width, set the height from the pixel aspect
            var img = LabelRenderer.LoadImage(CurImage(), BaseDir());
            if (img is null || img.Width == 0) return;
            double w = O.GetNum("width");
            SetAttr(O, "height", N(w * img.Height / img.Width), "fit image box");
        }));
        AddInfo("Pixels", LabelRenderer.LoadImage(CurImage(), BaseDir()) is { } im ? $"{im.Width} × {im.Height}" : "(not readable)");
    }

    private string? PickLogoFile(string? baseDir, string title = "Choose logo image")
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*",
            InitialDirectory = baseDir ?? "",
        };
        return dlg.ShowDialog(FindForm()) == DialogResult.OK
            ? Relativize(dlg.FileName, baseDir) : null;
    }

    /// <summary>Prefer a template-relative path when the file sits under
    /// the label's folder — those templates survive moving the folder.</summary>
    private static string Relativize(string path, string? baseDir)
    {
        if (baseDir is null) return path;
        try
        {
            string rel = Path.GetRelativePath(baseDir, path);
            return rel.StartsWith("..") || Path.IsPathRooted(rel) ? path : rel;
        }
        catch { return path; }
    }

    private static string SniffMime(byte[] b) =>
        b.Length > 3 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg"
        : b.Length > 2 && b[0] == 'G' && b[1] == 'I' ? "image/gif"
        : b.Length > 1 && b[0] == 'B' && b[1] == 'M' ? "image/bmp"
        : "image/png";

    private static string ExtFor(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private void BuildMulti()
    {
        AddHeader(() => $"{_objs.Count} objects selected");
        AddLen("X", () => SelBounds().X, v => _doc!.MoveObjects(_objs, v - SelBounds().X, 0));
        AddLen("Y", () => SelBounds().Y, v => _doc!.MoveObjects(_objs, 0, v - SelBounds().Y));
        AddNum("Rotate by", () => 0, v =>
        {
            if (v % 360 != 0) _doc!.RotateObjects(_objs, v, SelBounds().Center);
        }, step: 15, hint: "degrees clockwise about the selection center; snaps back to 0");

        if (Texts().Count == 0) return;      // has-text is part of the shape key
        AddHeader(() => $"Text ({Texts().Count})");
        AddPt("Font size", () =>
        {
            var sizes = Texts().Select(t => t.GetNum("font-size", 12)).Distinct().ToList();
            return sizes.Count == 1 ? sizes[0] : 0;
        }, v =>
        {
            if (v > 0) PushAll(Texts(), "font-size", N(v), "set font size");
        }, allowEmpty: true);
        AddCheck("Bold", () => Texts().All(t => t.Bold),
            v => PushAll(Texts(), "font-weight", v ? "bold" : null, "set bold"));
    }

    // ---------- edit plumbing (all through the undo stack) ----------

    private static string N(double v) => Num.F(v);

    private void Push(EditCommand cmd)
    {
        _doc?.Undo.Push(cmd);
        Changed?.Invoke();
    }

    private void SetAttr(EditorObject o, string attr, string? value, string label) =>
        Push(o.SetAttr(attr, value, label));

    /// <summary>One attribute on many objects = ONE undo entry.</summary>
    private void PushAll(IReadOnlyList<EditorObject> objs, string attr, string? value, string label)
    {
        if (objs.Count == 0) return;
        Push(EditCommand.Combine(
            objs.Select(x => EditCommand.SetAttr(x.El, attr, value, label)).ToList(),
            $"{label} ({objs.Count} objects)"));
    }

    private RectD SelBounds()
    {
        double x1 = double.MaxValue, y1 = double.MaxValue,
               x2 = double.MinValue, y2 = double.MinValue;
        foreach (var o in _objs)
        {
            var b = o.WorldBounds(_measurer);
            x1 = Math.Min(x1, b.X);
            y1 = Math.Min(y1, b.Y);
            x2 = Math.Max(x2, b.Right);
            y2 = Math.Max(y2, b.Bottom);
        }
        return new(x1, y1, x2 - x1, y2 - y1);
    }

    private static string[] FieldNames() =>
        new[] { "" }.Concat(FieldNameConverter.Names).ToArray();

    private static string[]? _fonts;
    private static string[] InstalledFonts() =>
        _fonts ??= System.Drawing.FontFamily.Families.Select(f => f.Name).ToArray();

    private static string Snip(string s)
    {
        s = s.Replace('\n', '¶');
        return s.Length > 24 ? s[..22] + "…" : s;
    }

    // ---------- row builders ----------

    private Label AddLabelCell(string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _table.Controls.Add(l, 0, _table.RowCount);
        return l;
    }

    private void FinishRow(Control c, string? hint)
    {
        if (hint is not null) _tips.SetToolTip(c, hint);
        _table.Controls.Add(c, 1, _table.RowCount);
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowCount++;
    }

    private Font? _boldFont;   // one shared instance — a new Font per header leaked a GDI handle per rebuild

    /// <summary>Header whose text depends on the selected object (kind ·
    /// layer, counts): refreshed like a value so the cached panel stays
    /// correct for whichever element it is showing.</summary>
    private void AddHeader(Func<string> get)
    {
        var l = AddHeaderLabel("");
        _refreshers.Add(() => l.Text = get());
    }

    private void AddHeader(string text) => AddHeaderLabel(text);

    private Label AddHeaderLabel(string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, Padding = new Padding(0, 8, 0, 2),
            Font = _boldFont ??= new Font(Font, FontStyle.Bold),
        };
        _table.Controls.Add(l, 0, _table.RowCount);
        _table.SetColumnSpan(l, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowCount++;
        return l;
    }

    /// <summary>Read-only, live-refreshed status row (encode feedback).</summary>
    private void AddStatus(string label, Func<string> get, string? hint = null)
    {
        AddLabelCell(label);
        var l = new Label
        {
            AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
        };
        _refreshers.Add(() => l.Text = get());
        FinishRow(l, hint);
    }

    private void AddInfo(string caption, string text)
    {
        AddHeader(caption);
        var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };
        _table.Controls.Add(l, 0, _table.RowCount);
        _table.SetColumnSpan(l, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowCount++;
    }

    /// <summary>Plain text row: commit on Enter / focus-leave, Escape reverts.</summary>
    private void AddText(string label, Func<string> get, Action<string> set, string? hint = null)
    {
        AddLabelCell(label);
        var tb = new TextBox { Dock = DockStyle.Fill };
        void Load() => tb.Text = get();
        void Commit()
        {
            if (_loading) return;
            if (tb.Text == get()) return;   // unchanged: no undo noise
            set(tb.Text);
            _loading = true;
            Load();
            _loading = false;
        }
        tb.Leave += (_, _) => Commit();
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape)
            { _loading = true; Load(); _loading = false; e.SuppressKeyPress = true; }
        };
        _refreshers.Add(() => { if (!tb.Focused) Load(); });
        FinishRow(tb, hint);
    }

    /// <summary>Numeric row: Enter/leave commits, Escape reverts, Up/Down
    /// nudges by ±step (Shift = ×10) with immediate apply. allowEmpty shows
    /// blank when the value equals the `unset` sentinel, and an emptied box
    /// commits the sentinel (which the setter maps to attribute removal).</summary>
    private void AddNum(string label, Func<double> get, Action<double> set,
                        double step = 1, bool allowEmpty = false, double unset = 0,
                        string? hint = null,
                        Func<double, string>? fmt = null, Func<string, double?>? parse = null)
    {
        AddLabelCell(label);
        var tb = new TextBox { Dock = DockStyle.Fill };
        fmt ??= N;
        parse ??= t => double.TryParse(t, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;
        void Load()
        {
            double v = get();
            tb.Text = allowEmpty && v == unset ? "" : fmt(v);
        }
        void Commit()
        {
            if (_loading) return;
            string t = tb.Text.Trim();
            if (allowEmpty && t == "")
            {
                if (get() != unset) set(unset);
            }
            else if (parse(t) is double v && Math.Abs(v - get()) > 0.0005)
            {
                set(v);
            }
            _loading = true;
            Load();
            _loading = false;
        }
        tb.Leave += (_, _) => Commit();
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape)
            { _loading = true; Load(); _loading = false; e.SuppressKeyPress = true; }
            else if (e.KeyCode is Keys.Up or Keys.Down)
            {
                double d = (e.KeyCode == Keys.Up ? 1 : -1) * step * (e.Shift ? 10 : 1);
                double cur = parse(tb.Text) ?? get();
                set(cur + d);
                _loading = true;
                Load();
                _loading = false;
                e.SuppressKeyPress = true;
            }
        };
        _refreshers.Add(() => { if (!tb.Focused) Load(); });
        FinishRow(tb, hint);
    }

    /// <summary>A LENGTH row: the model value is mils, the box shows and
    /// accepts the current display unit (label carries the suffix; a typed
    /// suffix — "12mm", "0.5in", "40mils" — overrides). Nudge = one display
    /// tick (0.01 in / 0.1 mm / 1 mil). The unit is part of the row
    /// structure, so a unit change clears the cache (see UnitsChanged).</summary>
    private void AddLen(string label, Func<double> get, Action<double> set,
                        bool allowEmpty = false, double unset = 0, string? hint = null)
    {
        AddNum($"{label} ({UnitPrefs.Suffix})", get, set,
            step: UnitPrefs.NudgeMils, allowEmpty: allowEmpty, unset: unset, hint: hint,
            fmt: UnitPrefs.F,
            parse: t => UnitPrefs.TryParse(t, out double m) ? m : null);
    }

    /// <summary>A FONT SIZE row: model value mils, shown in points (1 pt =
    /// 13.889 mils) regardless of the display unit; a typed suffix (mils,
    /// mm, in) still works. Nudge 1 pt, Shift = 10 pt.</summary>
    private void AddPt(string label, Func<double> get, Action<double> set,
                       bool allowEmpty = false, string? hint = null)
    {
        AddNum($"{label} (pt)", get, set,
            step: Units.MilsPerPt, allowEmpty: allowEmpty, hint: hint,
            fmt: Units.FormatPoints,
            parse: t => Units.TryParsePoints(t, out double m) ? m : null);
    }

    /// <summary>"Prints as" readout for a barcode: the drawn extent, and
    /// for 2D codes the symbol grid and module size — with a warning when
    /// the module isn't a whole number of printer dots (that is what makes
    /// a Data Matrix print fuzzy on a thermal head).</summary>
    /// <summary>Sample content the inspector sizes against (the fixed
    /// value, else the field name as a stand-in).</summary>
    private string SampleContent() =>
        (string?)O.El.Attribute("data-value") ?? (string?)O.El.Attribute("data-field") ?? "SAMPLE";

    /// <summary>Module grid of the sample: (cols, rows) for 2D; (modules, 0)
    /// for linear — height is free; (0, 0) when not encodable.</summary>
    private (int Cols, int Rows) ModuleCounts(string sym)
    {
        var b = O.Bounds(_measurer);
        if (sym is "qr" or "datamatrix" or "aztec" or "rmqr" or "pdf417")
        {
            var m = LabelRenderer.TryEncodeMatrix(sym, SampleContent(),
                (string?)O.El.Attribute("data-ecc"), (int)O.GetNum("data-columns", 0), 1,
                (string?)O.El.Attribute("data-dmshape") == "rect", b.H > 0 ? b.W / b.H : 0,
                (string?)O.El.Attribute("data-symsize"));
            return m is null ? (0, 0) : (m.GetLength(1), m.GetLength(0));
        }
        var mods = LabelRenderer.TryEncode(sym, SampleContent());
        if (mods is null) return (0, 0);
        int total = 0;
        foreach (var w in mods) total += w;
        return (total, 0);
    }

    /// <summary>The module size the box gives the sample today (mils).</summary>
    private double CurrentModule(string sym)
    {
        var b = O.Bounds(_measurer);
        var (cols, rows) = ModuleCounts(sym);
        if (cols <= 0 || b.W <= 0) return 0;
        return rows > 0 ? Math.Min(b.W / cols, b.H / rows) : b.W / cols;
    }

    private string PrintedSizeInfo(string sym)
    {
        var b = O.Bounds(_measurer);
        if (b.W <= 0 || b.H <= 0) return "—";
        double locked = LabelRenderer.ModuleLock(O);
        if (sym is not ("qr" or "datamatrix" or "aztec" or "rmqr"))
        {
            if (locked > 0)
            {
                var (mods1, _) = ModuleCounts(sym);
                if (mods1 <= 0) return "(sample not encodable)";
                double w1 = mods1 * locked;
                return w1 <= b.W + 0.01
                    ? $"{UnitPrefs.FS(w1)} × {UnitPrefs.FS(b.H)}, {mods1} modules at {UnitPrefs.FS(locked)} (exact), centered"
                    : $"needs {UnitPrefs.FS(w1)} at {UnitPrefs.FS(locked)}/module — wider than the box, fills the box instead";
            }
            return $"{UnitPrefs.FS(b.W)} × {UnitPrefs.FS(b.H)} (fills the box)";
        }
        string content = SampleContent();
        var m = LabelRenderer.TryEncodeMatrix(sym, content,
            (string?)O.El.Attribute("data-ecc"), 0, 1,
            (string?)O.El.Attribute("data-dmshape") == "rect", b.W / b.H,
            (string?)O.El.Attribute("data-symsize"));
        if (m is null) return $"{UnitPrefs.FS(Math.Min(b.W, b.H))} square (sample not encodable)";
        int rows = m.GetLength(0), cols = m.GetLength(1);
        double module = Math.Min(b.W / cols, b.H / rows);      // mils, square modules
        string exact = "";
        if (locked > 0)
        {
            if (cols * locked <= b.W + 0.01 && rows * locked <= b.H + 0.01) { module = locked; exact = " (exact)"; }
            else exact = $" — {UnitPrefs.FS(locked)} exact does not fit ({UnitPrefs.FS(cols * locked)} × {UnitPrefs.FS(rows * locked)} needed), filling instead";
        }
        string s = $"{UnitPrefs.FS(cols * module)} × {UnitPrefs.FS(rows * module)}, {cols}×{rows} modules, {UnitPrefs.FS(module)}/module{exact}";
        double dpmm = UnitPrefs.DotsPerMm;
        if (dpmm > 0)
        {
            double dots = module / Etiq.Editor.Core.Units.DotPitchMils(dpmm);
            s += Math.Abs(dots - Math.Round(dots)) < 0.02
                ? $" = {Math.Round(dots)} dots"
                : $" = {dots:0.00} dots — not whole dots, will print fuzzy; use {UnitPrefs.FS(Math.Floor(dots) * Etiq.Editor.Core.Units.DotPitchMils(dpmm) * cols)} wide";
        }
        return s;
    }

    /// <summary>Element-level Clear behavior (docs/convention.md
    /// `data-clear`): a data-bound element that draws EMPTY while the data
    /// panel is in its cleared state, even when its field (a compose of
    /// prompt defaults, say) still resolves to something. Lifted by the
    /// operator's first entry. Only offered on bound elements.</summary>
    private void AddClearBlank()
    {
        if (O.El.Attribute("data-field") is null) return;
        AddCheck("Blank on Clear", () => (string?)O.El.Attribute("data-clear") == "blank",
            v => Push(O.SetAttr("data-clear", v ? "blank" : null, "blank on clear")),
            hint: "After the data panel's Clear this element shows nothing until the operator enters data — for composes that would otherwise show their defaults");
    }

    /// <summary>Screen redaction mark (docs/convention.md `data-sensitive`):
    /// checkbox + optional stand-in text. Field-bound elements fall back to
    /// their placeholder, static text to a block mask when no stand-in is
    /// given. Print is never affected.</summary>
    private void AddSensitive()
    {
        AddCheck("Sensitive", () => Redaction.IsSensitive(O.El),
            v => Push(Redaction.Set(O.El, v, (string?)O.El.Attribute(Redaction.Attr))),
            hint: "View → Redact Sensitive shows a stand-in on screen; printing is unaffected");
        AddText("Stand-in", () =>
        {
            string a = (string?)O.El.Attribute(Redaction.Attr) ?? "";
            return a is "true" or "1" ? "" : a;
        }, v => Push(Redaction.Set(O.El, true, v)),
            hint: "empty = placeholder text (field) or a block mask (static)");
    }

    /// <summary>Stroke width in printer dots with an explicit "snap to whole
    /// dots" button — shown only when the template knows its head density.
    /// Explicit, never automatic: imported art carries its own widths. A
    /// 1-dot hairline is the usual target; sub-dot strokes render
    /// inconsistently on thermal heads.</summary>
    private void AddStrokeDots()
    {
        double dpmm = UnitPrefs.DotsPerMm;
        if (dpmm <= 0) return;
        double pitch = Units.DotPitchMils(dpmm);
        AddButtonRow("Stroke dots", () =>
        {
            double d = O.GetNum("stroke-width", 1) / pitch;
            bool whole = Math.Abs(d - Math.Round(d)) < 0.01;
            return whole ? $"{Math.Round(d)} dot{(Math.Round(d) == 1 ? "" : "s")}" : $"{d:0.##} dots — not whole";
        }, "Snap", () =>
        {
            double dots = Math.Max(1, Math.Round(O.GetNum("stroke-width", 1) / pitch));
            SetAttr(O, "stroke-width", N(dots * pitch), "snap stroke to dots");
        });
    }

    /// <summary>Display unit changed: every cached row set has the old
    /// suffix baked into its labels, so drop them all and rebuild.</summary>
    public void UnitsChanged()
    {
        ClearCache();
        _shape = "";
        if (_doc is not null) Reshape();
    }

    private void AddCombo(string label, string[] items, Func<string> get, Action<string> set,
                          bool editable = false, string? hint = null)
    {
        AddLabelCell(label);
        var cb = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
        };
        cb.Items.AddRange(items.Cast<object>().ToArray());
        if (editable)
        {
            cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cb.AutoCompleteSource = AutoCompleteSource.ListItems;
            // FIRST build of a row set: Load() runs before the combo has a
            // handle, so its highlight-clear is lost — when the handle is
            // created, WinForms pushes Text into the new edit control and
            // re-selects it all. Clear again at that moment. (Cached sets
            // revisited later already have handles, which is why only the
            // first visit showed the highlight.)
            // ... and clearing INSIDE HandleCreated is still too early: the
            // cached Text is pushed into the fresh edit control (and
            // selected) after this event returns. Defer past the whole
            // handle-creation message sequence with BeginInvoke.
            cb.HandleCreated += (_, _) => cb.BeginInvoke(() =>
            {
                if (cb.IsDisposed || cb.Focused) return;
                cb.SelectionStart = cb.Text.Length;
                cb.SelectionLength = 0;
            });
        }
        void Load()
        {
            string v = get();
            if (!editable && !cb.Items.Contains(v)) cb.Items.Add(v);
            cb.Text = v;
            // setting ComboBox.Text selects it all, and the blue highlight
            // sticks even without focus — clear it. EDITABLE combos only:
            // a DropDownList has no edit control, and its SelectionStart
            // returns garbage / throws ArgumentOutOfRange when set
            if (editable)
            {
                cb.SelectionStart = cb.Text.Length;
                cb.SelectionLength = 0;
            }
        }
        void Commit()
        {
            if (_loading) return;
            if (cb.Text == get()) return;
            set(cb.Text);
            _loading = true;
            Load();
            _loading = false;
        }
        cb.SelectionChangeCommitted += (_, _) => BeginInvoke(Commit);
        cb.Leave += (_, _) => Commit();
        cb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
        };
        _refreshers.Add(() => { if (!cb.Focused) Load(); });
        FinishRow(cb, hint);
    }

    private void AddCheck(string label, Func<bool> get, Action<bool> set, string? hint = null)
    {
        AddLabelCell(label);
        var ck = new CheckBox { AutoSize = true };
        ck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (ck.Checked == get()) return;
            set(ck.Checked);
        };
        _refreshers.Add(() => ck.Checked = get());
        FinishRow(ck, hint);
    }

    /// <summary>Row of action buttons in the value column.</summary>
    private void AddButtons(params (string Caption, Action On)[] buttons)
    {
        AddLabelCell("");
        var flow = new FlowLayoutPanel
            { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        foreach (var (caption, on) in buttons)
        {
            var b = new Button { Text = caption, AutoSize = true, Margin = new Padding(0, 0, 4, 2) };
            var act = on;
            b.Click += (_, _) => act();
            flow.Controls.Add(b);
        }
        FinishRow(flow, null);
    }

    /// <summary>Read-only value + action button (multiline text editing).</summary>
    private Button AddButtonRow(string label, Func<string> get, string caption, Action on)
    {
        AddLabelCell(label);
        var panel = new TableLayoutPanel
            { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var val = new Label
        {
            AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
        };
        var btn = new Button { Text = caption, AutoSize = true, Margin = new Padding(2, 0, 0, 0) };
        btn.Click += (_, _) => { on(); RefreshValues(); };
        panel.Controls.Add(val, 0, 0);
        panel.Controls.Add(btn, 1, 0);
        _refreshers.Add(() => val.Text = get());
        FinishRow(panel, null);
        return btn;
    }
}
