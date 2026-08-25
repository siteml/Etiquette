using Etiq.Editor.Core;

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
        return $"{o.Kind}|{(string?)o.El.Attribute("data-barcode")}|{logoKey}";
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
            AddNum("X1", () => O.GetNum("x1"), v => SetAttr(O, "x1", N(v), "set X1"));
            AddNum("Y1", () => O.GetNum("y1"), v => SetAttr(O, "y1", N(v), "set Y1"));
            AddNum("X2", () => O.GetNum("x2"), v => SetAttr(O, "x2", N(v), "set X2"));
            AddNum("Y2", () => O.GetNum("y2"), v => SetAttr(O, "y2", N(v), "set Y2"));
            AddNum("Stroke", () => O.GetNum("stroke-width", 1),
                v => SetAttr(O, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
            return;
        }

        AddNum("X", () => O.GetNum("x"), v => SetAttr(O, "x", N(v), "set X"));
        AddNum("Y", () => O.GetNum("y"), v => SetAttr(O, "y", N(v), "set Y"));
        AddNum("Rotation", () => O.RotationDeg, v => Push(O.SetRotation(v)), step: 15);

        switch (o.Kind)
        {
            case ObjectKind.Text: BuildText(); break;
            case ObjectKind.Barcode: BuildBarcode(o); break;
            case ObjectKind.Box:
                AddNum("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
                AddNum("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));
                AddCheck("Filled", () => ((string?)O.El.Attribute("fill") ?? "none") != "none",
                    v => SetAttr(O, "fill", v ? "black" : null, "set fill"));
                AddNum("Stroke", () => O.GetNum("stroke-width", 1),
                    v => SetAttr(O, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
                break;
            case ObjectKind.Image:
                AddNum("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
                AddNum("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));
                break;
        }
    }

    private void BuildText()
    {
        AddHeader("Text");
        // multiline content edits through the shared prompt (Enter = new line)
        AddButtonRow("Text", () => Snip(O.El.Value), "Edit…", () =>
        {
            string? t = Prompts.PromptText(FindForm()!, "Edit text (Enter = new line)",
                O.El.Value, multiline: true);
            if (t is not null) Push(O.SetText(t));
        });
        AddCombo("Font", InstalledFonts(), () => O.FontFamily,
            v => SetAttr(O, "font-family", v == "" ? null : v, "set font"), editable: true);
        AddNum("Font size", () => O.GetNum("font-size", 12),
            v => SetAttr(O, "font-size", N(Math.Max(1, v)), "set font size"));
        AddCheck("Bold", () => O.Bold,
            v => SetAttr(O, "font-weight", v ? "bold" : null, "set bold"));
        AddNum("Line height", () => O.GetNum("data-line-height"),
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
        AddNum("Width", () => O.GetNum("data-width"),
            v => SetAttr(O, "data-width", v <= 0 ? null : N(v), "set width"),
            allowEmpty: true, hint: "empty = natural width");
        AddNum("Height", () => O.GetNum("data-height"),
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
        AddNum("Width", () => O.GetNum("width"), v => SetAttr(O, "width", N(v), "set width"));
        AddNum("Height", () => O.GetNum("height"), v => SetAttr(O, "height", N(v), "set height"));

        AddHeader("Barcode");
        AddCombo("Symbology", Etiq.Core.EtiqTemplate.Symbologies,
            () => (string?)O.El.Attribute("data-barcode") ?? "",
            v =>
            {
                if (v == "") return;
                SetAttr(O, "data-barcode", v, "set symbology");
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
            AddCombo("HRI", new[] { "", "none", "below", "above" },
                () => (string?)O.El.Attribute("data-hri") ?? "",
                v => SetAttr(O, "data-hri", v == "" ? null : v, "set hri"),
                hint: "human-readable text inside the box, under or above the bars; empty = none");

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
        AddNum("Module mils", () => O.GetNum("data-module-mils"),
            v => SetAttr(O, "data-module-mils", v <= 0 ? null : N(v), "set module mils"),
            allowEmpty: true);
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
        if (sym is "qr" or "datamatrix" or "aztec" or "rmqr")
            AddCheck("Tight box", () => (string?)O.El.Attribute("data-tight") == "1",
                v =>
                {
                    SetAttr(O, "data-tight", v ? "1" : null, "set tight box");
                    // enabling snaps immediately; resizes keep it snapped
                    if (v && LabelRenderer.TightBarcodeRect(O, _measurer) is { } r)
                        Push(O.Resize(r, _measurer));
                },
                hint: "keep the box snapped to the symbol's exact drawn size after every resize");

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

    private string? PickLogoFile(string? baseDir)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose logo image",
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
        AddNum("X", () => SelBounds().X, v => _doc!.MoveObjects(_objs, v - SelBounds().X, 0));
        AddNum("Y", () => SelBounds().Y, v => _doc!.MoveObjects(_objs, 0, v - SelBounds().Y));
        AddNum("Rotate by", () => 0, v =>
        {
            if (v % 360 != 0) _doc!.RotateObjects(_objs, v, SelBounds().Center);
        }, step: 15, hint: "degrees clockwise about the selection center; snaps back to 0");

        if (Texts().Count == 0) return;      // has-text is part of the shape key
        AddHeader(() => $"Text ({Texts().Count})");
        AddNum("Font size", () =>
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

    private static string N(double v) =>
        v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
                        string? hint = null)
    {
        AddLabelCell(label);
        var tb = new TextBox { Dock = DockStyle.Fill };
        void Load()
        {
            double v = get();
            tb.Text = allowEmpty && v == unset ? "" : N(v);
        }
        void Commit()
        {
            if (_loading) return;
            string t = tb.Text.Trim();
            if (allowEmpty && t == "")
            {
                if (get() != unset) set(unset);
            }
            else if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out double v)
                     && Math.Abs(v - get()) > 0.0005)
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
                double cur = double.TryParse(tb.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double c) ? c : get();
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
