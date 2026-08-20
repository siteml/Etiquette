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
    private readonly TableLayoutPanel _table;
    private readonly ToolTip _tips = new();
    private readonly GdiTextMeasurer _measurer = new();
    private EditorDoc? _doc;
    private List<EditorObject> _objs = new();
    private bool _loading;                                // guard: setting control values
    private readonly List<Action> _refreshers = new();    // model → control re-readers

    /// <summary>Raised after any committed edit (canvas + outline refresh).</summary>
    public event Action? Changed;

    public InspectorPanel()
    {
        AutoScroll = true;
        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 4, 4, 8),
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
    }

    // ---------- public API ----------

    /// <summary>Rebuild the panel for the current selection.</summary>
    public void ShowSelection(EditorDoc? doc, IReadOnlyList<EditorObject> selection)
    {
        _doc = doc;
        _objs = doc is null ? new List<EditorObject>() : selection.ToList();
        Rebuild();
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

    private void Rebuild()
    {
        SuspendLayout();
        _table.SuspendLayout();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = 0;
        _refreshers.Clear();

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
        ResumeLayout();
        RefreshValues();
    }

    private void BuildSingle(EditorObject o)
    {
        AddHeader($"{o.Kind}   ·   layer {o.Layer?.Name ?? "(none)"}");

        // position (lines edit their endpoints instead)
        if (o.Kind == ObjectKind.Line)
        {
            AddNum("X1", () => o.GetNum("x1"), v => SetAttr(o, "x1", N(v), "set X1"));
            AddNum("Y1", () => o.GetNum("y1"), v => SetAttr(o, "y1", N(v), "set Y1"));
            AddNum("X2", () => o.GetNum("x2"), v => SetAttr(o, "x2", N(v), "set X2"));
            AddNum("Y2", () => o.GetNum("y2"), v => SetAttr(o, "y2", N(v), "set Y2"));
            AddNum("Stroke", () => o.GetNum("stroke-width", 1),
                v => SetAttr(o, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
            return;
        }

        AddNum("X", () => o.GetNum("x"), v => SetAttr(o, "x", N(v), "set X"));
        AddNum("Y", () => o.GetNum("y"), v => SetAttr(o, "y", N(v), "set Y"));
        AddNum("Rotation", () => o.RotationDeg, v => Push(o.SetRotation(v)), step: 15);

        switch (o.Kind)
        {
            case ObjectKind.Text: BuildText(o); break;
            case ObjectKind.Barcode: BuildBarcode(o); break;
            case ObjectKind.Box:
                AddNum("Width", () => o.GetNum("width"), v => SetAttr(o, "width", N(v), "set width"));
                AddNum("Height", () => o.GetNum("height"), v => SetAttr(o, "height", N(v), "set height"));
                AddCheck("Filled", () => ((string?)o.El.Attribute("fill") ?? "none") != "none",
                    v => SetAttr(o, "fill", v ? "black" : null, "set fill"));
                AddNum("Stroke", () => o.GetNum("stroke-width", 1),
                    v => SetAttr(o, "stroke-width", v <= 0 ? null : N(v), "set stroke"));
                break;
            case ObjectKind.Image:
                AddNum("Width", () => o.GetNum("width"), v => SetAttr(o, "width", N(v), "set width"));
                AddNum("Height", () => o.GetNum("height"), v => SetAttr(o, "height", N(v), "set height"));
                break;
        }
    }

    private void BuildText(EditorObject o)
    {
        AddHeader("Text");
        // multiline content edits through the shared prompt (Enter = new line)
        AddButtonRow("Text", () => Snip(o.El.Value), "Edit…", () =>
        {
            string? t = Prompts.PromptText(FindForm()!, "Edit text (Enter = new line)",
                o.El.Value, multiline: true);
            if (t is not null) Push(o.SetText(t));
        });
        AddCombo("Font", InstalledFonts(), () => o.FontFamily,
            v => SetAttr(o, "font-family", v == "" ? null : v, "set font"), editable: true);
        AddNum("Font size", () => o.GetNum("font-size", 12),
            v => SetAttr(o, "font-size", N(Math.Max(1, v)), "set font size"));
        AddCheck("Bold", () => o.Bold,
            v => SetAttr(o, "font-weight", v ? "bold" : null, "set bold"));
        AddNum("Line height", () => o.GetNum("data-line-height"),
            v => SetAttr(o, "data-line-height", v <= 0 ? null : N(v), "set line height"),
            allowEmpty: true, hint: "baseline-to-baseline; empty = 1.2 × font size");

        AddHeader("Fit box");
        AddCombo("Fit mode", new[] { "", "none", "width", "box" },
            () => (string?)o.El.Attribute("data-fit") ?? "",
            v => SetAttr(o, "data-fit", v == "" ? null : v, "set fit mode"));
        AddNum("Width", () => o.GetNum("data-width"),
            v => SetAttr(o, "data-width", v <= 0 ? null : N(v), "set width"),
            allowEmpty: true, hint: "empty = natural width");
        AddNum("Height", () => o.GetNum("data-height"),
            v => SetAttr(o, "data-height", v <= 0 ? null : N(v), "set height"),
            allowEmpty: true, hint: "empty = natural height");
        AddCombo("Align", new[] { "", "left", "center", "right" },
            () => (string?)o.El.Attribute("data-align") ?? "",
            v => SetAttr(o, "data-align", v is "" or "left" ? null : v, "set align"));
        AddCombo("Vert. align", new[] { "", "top", "middle", "bottom" },
            () => (string?)o.El.Attribute("data-valign") ?? "",
            v => SetAttr(o, "data-valign", v is "" or "top" ? null : v, "set valign"));
        AddCombo("Overflow", new[] { "", "shrink", "clip", "wrap" },
            () => (string?)o.El.Attribute("data-overflow") ?? "",
            v => SetAttr(o, "data-overflow", v == "" ? null : v, "set overflow"));

        AddHeader("Data");
        AddCombo("Field", FieldNames(), () => (string?)o.El.Attribute("data-field") ?? "",
            v => SetAttr(o, "data-field", v == "" ? null : v, "bind field"), editable: true);
        AddNum("Line #", () => o.GetNum("data-line", -1),
            v => SetAttr(o, "data-line", v < 0 ? null : ((int)v).ToString(), "set line index"),
            allowEmpty: true, unset: -1,
            hint: "Line-stack element: show only line N (0-based) of the field's value");
    }

    private void BuildBarcode(EditorObject o)
    {
        string sym = (string?)o.El.Attribute("data-barcode") ?? "";
        AddNum("Width", () => o.GetNum("width"), v => SetAttr(o, "width", N(v), "set width"));
        AddNum("Height", () => o.GetNum("height"), v => SetAttr(o, "height", N(v), "set height"));

        AddHeader("Barcode");
        AddCombo("Symbology", Etiq.Core.EtiqTemplate.Symbologies,
            () => (string?)o.El.Attribute("data-barcode") ?? "",
            v =>
            {
                if (v == "") return;
                SetAttr(o, "data-barcode", v, "set symbology");
                // deferred: the rebuild disposes the combo raising this event
                BeginInvoke(new Action(Rebuild));
            });
        AddCombo("Field", FieldNames(), () => (string?)o.El.Attribute("data-field") ?? "",
            v => SetAttr(o, "data-field", v == "" ? null : v, "bind field"), editable: true);
        AddText("Fixed value", () => (string?)o.El.Attribute("data-value") ?? "",
            v => SetAttr(o, "data-value", v == "" ? null : v, "set value"));
        AddNum("Module mils", () => o.GetNum("data-module-mils"),
            v => SetAttr(o, "data-module-mils", v <= 0 ? null : N(v), "set module mils"),
            allowEmpty: true);

        if (sym == "qr")
        {
            AddHeader("QR");
            AddCombo("ECC", new[] { "", "L", "M", "Q", "H" },
                () => (string?)o.El.Attribute("data-ecc") ?? "",
                v => SetAttr(o, "data-ecc", v == "" ? null : v, "set qr ecc"));
            BuildQrLogoRows(o);
        }
        else if (sym == "pdf417")
        {
            AddHeader("PDF417");
            AddNum("Columns", () => o.GetNum("data-columns"),
                v => SetAttr(o, "data-columns",
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
    private void BuildQrLogoRows(EditorObject o)
    {
        string cur = (string?)o.El.Attribute("data-logo") ?? "";
        string mode = cur == "" ? LogoModeNone
                    : cur == "etiq" ? LogoModeEtiq : LogoModeCustom;
        bool embedded = cur.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        string? baseDir = _doc?.Path is { } dp ? Path.GetDirectoryName(dp) : null;

        AddCombo("Logo", new[] { LogoModeNone, LogoModeEtiq, LogoModeCustom },
            () => mode, v =>
            {
                if (v == mode) return;
                if (v == LogoModeNone)
                    Push(EditCommand.SetAttrs(o.El, new()
                        {
                            ("data-logo", cur == "" ? null : cur, null),
                            ("data-logo-scale", (string?)o.El.Attribute("data-logo-scale"), null),
                        }, "remove qr logo"));
                else if (v == LogoModeEtiq)
                    SetAttr(o, "data-logo", "etiq", "set qr logo");
                else
                {
                    // straight to the picker; cancel = stay in the old mode
                    string? picked = PickLogoFile(baseDir);
                    if (picked is null) { RefreshValues(); return; }
                    SetAttr(o, "data-logo", picked, "set qr logo");
                }
                BeginInvoke(new Action(Rebuild));   // rows depend on the mode
            }, hint: "A logo forces ECC H and QR version ≥2; sizing keeps the code scannable.");

        if (mode == LogoModeCustom && !embedded)
        {
            AddText("Source", () => (string?)o.El.Attribute("data-logo") ?? "",
                v =>
                {
                    if (v != "") SetAttr(o, "data-logo", v, "set qr logo");
                    BeginInvoke(new Action(Rebuild));
                },
                hint: "image file path (absolute, or relative to the label file) or an http(s) URL");
            AddButtons(
                ("Browse…", () =>
                {
                    string? picked = PickLogoFile(baseDir);
                    if (picked is null) return;
                    SetAttr(o, "data-logo", picked, "set qr logo");
                    BeginInvoke(new Action(Rebuild));
                }
                ),
                ("Embed into template", () =>
                {
                    string spec = (string?)o.El.Attribute("data-logo") ?? "";
                    var bytes = LabelRenderer.FetchLogoBytes(spec, baseDir);
                    if (bytes is null)
                    {
                        MessageBox.Show(FindForm(), "Could not read the logo image from its source.", "Embed");
                        return;
                    }
                    if (bytes.Length > 256 * 1024 && MessageBox.Show(FindForm(),
                            $"The image is {bytes.Length / 1024} KB — embedding grows the label file by ~{bytes.Length * 4 / 3 / 1024} KB. Continue?",
                            "Embed", MessageBoxButtons.OKCancel) != DialogResult.OK)
                        return;
                    SetAttr(o, "data-logo",
                        $"data:{SniffMime(bytes)};base64,{Convert.ToBase64String(bytes)}",
                        "embed qr logo");
                    BeginInvoke(new Action(Rebuild));
                }
                ));
        }
        else if (mode == LogoModeCustom && embedded)
        {
            int comma = cur.IndexOf(',');
            int kb = comma > 0 ? (cur.Length - comma) * 3 / 4 / 1024 : 0;
            AddButtons(($"Extract… (embedded, ~{Math.Max(1, kb)} KB)", () =>
            {
                var bytes = LabelRenderer.FetchLogoBytes(cur, baseDir);
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
                        SetAttr(o, "data-logo", Relativize(dlg.FileName, baseDir), "set qr logo");
                        BeginInvoke(new Action(Rebuild));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), ex.Message, "Extract failed");
                }
            }
            ));
        }

        if (mode != LogoModeNone)
            AddNum("Logo scale %", () => o.GetNum("data-logo-scale"),
                v => SetAttr(o, "data-logo-scale",
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
        AddHeader($"{_objs.Count} objects selected");
        AddNum("X", () => SelBounds().X, v => _doc!.MoveObjects(_objs, v - SelBounds().X, 0));
        AddNum("Y", () => SelBounds().Y, v => _doc!.MoveObjects(_objs, 0, v - SelBounds().Y));
        AddNum("Rotate by", () => 0, v =>
        {
            if (v % 360 != 0) _doc!.RotateObjects(_objs, v, SelBounds().Center);
        }, step: 15, hint: "degrees clockwise about the selection center; snaps back to 0");

        var texts = _objs.Where(x => x.Kind == ObjectKind.Text).ToList();
        if (texts.Count == 0) return;
        AddHeader($"Text ({texts.Count})");
        AddNum("Font size", () =>
        {
            var sizes = texts.Select(t => t.GetNum("font-size", 12)).Distinct().ToList();
            return sizes.Count == 1 ? sizes[0] : 0;
        }, v =>
        {
            if (v > 0) PushAll(texts, "font-size", N(v), "set font size");
        }, allowEmpty: true);
        AddCheck("Bold", () => texts.All(t => t.Bold),
            v => PushAll(texts, "font-weight", v ? "bold" : null, "set bold"));
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

    private void AddHeader(string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, Padding = new Padding(0, 8, 0, 2),
            Font = new Font(Font, FontStyle.Bold),
        };
        _table.Controls.Add(l, 0, _table.RowCount);
        _table.SetColumnSpan(l, 2);
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.RowCount++;
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
        }
        void Load()
        {
            string v = get();
            if (!editable && !cb.Items.Contains(v)) cb.Items.Add(v);
            cb.Text = v;
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
