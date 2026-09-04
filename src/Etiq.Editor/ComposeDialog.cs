using System.Xml.Linq;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>
/// Full-size editor for ONE compose field: its segment list, or — when
/// "conditional" is on — its switch-on field and one segment list per
/// etiq:variant (tab per variant). Opened from the F4 dialog's Fields tab;
/// edits are written back to the field element only on OK, and the field
/// itself lives in MetadataDialog's working clone, so Cancel anywhere
/// still discards everything and OK-OK lands as ONE undo step.
/// </summary>
public sealed class ComposeDialog : Form
{
    private static readonly XNamespace NS = EditorDoc.EtiqNs;

    private readonly XElement _field;

    private readonly CheckBox _collapse = new()
        { Text = "Collapse blank lines", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _conditional = new()
        { Text = "Conditional (variants), switch on:", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _switchOn = new()
        { Width = 170, DropDownStyle = ComboBoxStyle.DropDown, Enabled = false };
    private readonly Button _addVar = new() { Text = "Add Variant", Width = 100, Enabled = false };
    private readonly Button _delVar = new() { Text = "Remove Variant", Width = 115, Enabled = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true };

    /// <summary>One tab: variant match strip + its seg grid. In plain
    /// (non-conditional) mode there is exactly one page and its strip is
    /// hidden.</summary>
    private sealed class VarPage
    {
        public TabPage Page = null!;
        public Control Strip = null!;
        public ComboBox Kind = null!;
        public TextBox Match = null!;
        public DataGridView Grid = null!;
    }
    private readonly List<VarPage> _pages = new();

    /// <summary>preview: given a COPY of the field as the dialog would save
    /// it, return the composed text with sample values (or an error
    /// message). Null = no preview line.</summary>
    private readonly Func<XElement, string>? _preview;
    private readonly Label _previewLbl = new()
    {
        Dock = DockStyle.Bottom, Height = 26, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0),
        Font = new Font(FontFamily.GenericMonospace, 9.5f),
    };

    public ComposeDialog(XElement field, IEnumerable<string> switchCandidates,
                         Func<XElement, string>? preview = null)
    {
        _field = field;
        _preview = preview;

        Text = $"Compose — {(string?)field.Attribute("name") ?? "(unnamed)"}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Ui.AutoScale(this);
        ClientSize = new Size(960, 480);
        MinimumSize = new Size(640, 360);

        foreach (var c in switchCandidates) _switchOn.Items.Add(c);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 6, 6, 0),
            WrapContents = false,
        };
        top.Controls.Add(_collapse);
        top.Controls.Add(new Label { Text = "      ", AutoSize = true });
        top.Controls.Add(_conditional);
        top.Controls.Add(_switchOn);
        top.Controls.Add(_addVar);
        top.Controls.Add(_delVar);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 22, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0),
            Text = "Rows concatenate in order. newline ✓ = line break; sep goes on the FOLLOWING " +
                   "row — emitted before it, only when both sides are non-empty. " +
                   "Select a row to edit its transforms below. Right-click rows to insert/reorder.",
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Height = 40, Padding = new Padding(6),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);

        Controls.Add(_tabs);
        Controls.Add(hint);
        Controls.Add(top);
        if (_preview is not null) Controls.Add(_previewLbl);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        _conditional.CheckedChanged += (_, _) => { OnConditionalToggled(); RefreshPreview(); };
        _collapse.CheckedChanged += (_, _) => RefreshPreview();
        _switchOn.TextChanged += (_, _) => RefreshPreview();
        _addVar.Click += (_, _) =>
        {
            var p = AddPage("exact", "", null);
            UpdateStrips();
            _tabs.SelectedTab = p.Page;
        };
        _delVar.Click += (_, _) => RemoveCurrentVariant();
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && !CommitBack()) e.Cancel = true;
        };

        LoadFromField();
        RefreshPreview();
        ClientSize = new Size(960, 560);
    }

    /// <summary>Compose the field as it would be saved and show the sample
    /// result. Debounce-free: composing a handful of segs is instant.</summary>
    private void RefreshPreview()
    {
        if (_preview is null || _pages.Count == 0) return;
        try
        {
            var tmp = new XElement(_field);
            CommitInto(tmp, quiet: true);
            _previewLbl.Text = "Preview:  " + _preview(tmp).Replace("\n", " ⏎ ");
            _previewLbl.ForeColor = SystemColors.ControlText;
        }
        catch (Exception ex)
        {
            _previewLbl.Text = "Preview:  " + ex.Message;
            _previewLbl.ForeColor = Color.Firebrick;
        }
    }

    // ---------- load ----------

    private void LoadFromField()
    {
        _collapse.Checked = (string?)_field.Attribute("collapse-blank-lines") == "true";
        var variants = _field.Elements(NS + "variant").ToList();
        if (variants.Count > 0)
        {
            _conditional.Checked = true;   // no pages yet, so the toggle is a no-op
            _switchOn.Text = (string?)_field.Attribute("switch-on") ?? "";
            foreach (var v in variants)
            {
                bool prefix = v.Attribute("prefix") is not null;
                bool exact = v.Attribute("when") is not null;
                AddPage(exact ? "exact" : prefix ? "prefix" : "default",
                        (string?)(exact ? v.Attribute("when") : v.Attribute("prefix")) ?? "",
                        v);
            }
        }
        else
        {
            AddPage("default", "", _field);   // plain mode: segs live on the field
        }
        UpdateStrips();
    }

    /// <summary>Create one tab (match strip + seg grid), optionally loading
    /// segs from segSource (a variant element, or the field itself).</summary>
    private VarPage AddPage(string kind, string match, XElement? segSource)
    {
        var p = new VarPage();
        p.Kind = new ComboBox
            { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Left };
        p.Kind.Items.AddRange(new object[] { "exact", "prefix", "default" });
        p.Kind.SelectedItem = kind;
        p.Match = new TextBox { Width = 220, Anchor = AnchorStyles.Left, Text = match };

        var strip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        strip.Controls.Add(new Label
            { Text = "Match:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft });
        strip.Controls.Add(p.Kind);
        strip.Controls.Add(p.Match);
        strip.Controls.Add(new Label
        {
            Text = "(exact \"A|B|C\" matches any listed value and beats prefix; default = no match needed)",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText,
        });
        p.Strip = strip;

        p.Grid = GridTools.NewGrid();
        GridTools.AddSegColumns(p.Grid, compact: true);
        GridTools.AttachRowTools(p.Grid);
        if (segSource is not null) GridTools.LoadSegs(p.Grid, segSource);
        var details = BuildDetailsPane(p.Grid);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        layout.Controls.Add(strip, 0, 0);
        layout.Controls.Add(p.Grid, 0, 1);
        layout.Controls.Add(details, 0, 2);
        p.Grid.CellValueChanged += (_, _) => { GridTools.RefreshSummaries(p.Grid); RefreshPreview(); };
        p.Grid.RowsRemoved += (_, _) => RefreshPreview();
        p.Grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (p.Grid.IsCurrentCellDirty) p.Grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        p.Page = new TabPage();
        p.Page.Controls.Add(layout);
        _pages.Add(p);
        _tabs.TabPages.Add(p.Page);

        void Retitle()
        {
            p.Match.Enabled = (string?)p.Kind.SelectedItem != "default";
            UpdateTabText(p);
        }
        p.Kind.SelectedIndexChanged += (_, _) => Retitle();
        p.Match.TextChanged += (_, _) => UpdateTabText(p);
        Retitle();
        return p;
    }

    private void UpdateTabText(VarPage p)
    {
        if (!_conditional.Checked) { p.Page.Text = "Segments"; p.Page.ToolTipText = ""; return; }
        // multi-value when= ("DE|AT|CH|…") can get long — truncate the tab,
        // full list stays in the tab tooltip and the Match box
        string m = p.Match.Text == "" ? "?" : p.Match.Text;
        string shortM = m.Length > 18 ? m[..16] + "…" : m;
        p.Page.Text = (string?)p.Kind.SelectedItem switch
        {
            "exact" => $"= {shortM}",
            "prefix" => $"{shortM}…",
            _ => "(default)",
        };
        p.Page.ToolTipText = m;
    }

    // ---------- mode ----------

    private void OnConditionalToggled()
    {
        if (!_conditional.Checked && _pages.Count > 1)
        {
            // dropping to plain mode keeps ONE segment list; prefer the default
            var keep = _pages.FirstOrDefault(p => (string?)p.Kind.SelectedItem == "default")
                       ?? _pages[0];
            if (MessageBox.Show(this,
                    $"Turning off conditional keeps only the \"{keep.Page.Text}\" variant's " +
                    "segments and discards the rest. Continue?",
                    "Compose", MessageBoxButtons.OKCancel) != DialogResult.OK)
            {
                // declined: restore the checkbox; the nested toggle it fires
                // takes the cond=true path and just refreshes the strips
                _conditional.Checked = true;
                return;
            }
            foreach (var p in _pages.Where(p => p != keep).ToList())
            {
                _tabs.TabPages.Remove(p.Page);
                _pages.Remove(p);
            }
        }
        UpdateStrips();
    }

    private void UpdateStrips()
    {
        bool cond = _conditional.Checked;
        _switchOn.Enabled = _addVar.Enabled = cond;
        _delVar.Enabled = cond && _pages.Count > 1;
        foreach (var p in _pages)
        {
            p.Strip.Visible = cond;
            UpdateTabText(p);
        }
    }

    private void RemoveCurrentVariant()
    {
        if (_pages.Count <= 1) return;
        var p = _pages.FirstOrDefault(x => x.Page == _tabs.SelectedTab);
        if (p is null) return;
        if (MessageBox.Show(this, $"Remove variant \"{p.Page.Text}\" and its segments?",
                "Compose", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        _tabs.TabPages.Remove(p.Page);
        _pages.Remove(p);
        UpdateStrips();
    }

    // ---------- details pane (transforms of the selected row) ----------

    /// <summary>Editors for the hidden transform cells of the grid's current
    /// row, grouped the way the resolver applies them. Writes straight into
    /// the row's cells, so LoadSegs/CommitSegs stay the single XML path.</summary>
    private Control BuildDetailsPane(DataGridView g)
    {
        var pane = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 3, Padding = new Padding(4, 2, 4, 0),
        };
        for (int i = 0; i < 4; i++)
        {
            pane.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        for (int i = 0; i < 3; i++) pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var editors = new Dictionary<string, Control>();
        bool loading = false;
        void Add(int col, int row, string label, string attr, Control c, string tip)
        {
            var l = new Label { Text = label, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill };
            c.Dock = DockStyle.Fill; c.Margin = new Padding(2, 6, 8, 6);
            pane.Controls.Add(l, col * 2, row);
            pane.Controls.Add(c, col * 2 + 1, row);
            editors[attr] = c;
            new ToolTip().SetToolTip(c, tip);
            Control input = c is FlowLayoutPanel fl ? fl.Controls[0] : c;
            void Changed()
            {
                if (loading || g.CurrentRow is null || g.CurrentRow.IsNewRow) return;
                if (input.Parent is FlowLayoutPanel p2 && p2.Controls.OfType<CheckBox>().FirstOrDefault()?.Checked == true)
                    return;   // the blank checkbox owns the cell right now
                string v = input is ComboBox cb ? cb.Text : ((TextBox)input).Text;
                g.CurrentRow.Cells[attr].Value = v == "" ? null : v;
                GridTools.RefreshSummaries(g);
                RefreshPreview();
            }
            if (input is ComboBox combo) { combo.TextChanged += (_, _) => Changed(); combo.SelectedIndexChanged += (_, _) => Changed(); }
            else input.TextChanged += (_, _) => Changed();
        }
        // row 0: pick a piece
        Add(0, 0, "split on", "split", new TextBox(), "Delimiter to split the value on (\">\", \",\", \" \"). Pieces are trimmed.");
        Add(1, 0, "piece #", "part", new TextBox(), "0-based; negative counts from the end: -1 = last, -2 = one before. Default -1.");
        Add(2, 0, "start", "start", new TextBox(), "Substring start (0-based), after split.");
        Add(3, 0, "length", "len", new TextBox(), "Substring length; empty = to the end.");
        // row 1: format
        Add(0, 1, "format", "format", new TextBox(), "date:yyMM, number:0.00 … (same as data-format)");
        var caseBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        caseBox.Items.AddRange(new object[] { "", "upper", "lower", "title" });
        Add(1, 1, "case", "case", caseBox, "Letter case applied after format.");
        Add(2, 1, "pad", "pad", new TextBox(), "side:char:width, e.g. left:0:6");
        Add(3, 1, "if empty", "if-empty", new TextBox(), "Text used when this segment resolves empty. Type \"\" for explicitly blank.");
        // row 2: lookup — default gets a "blank" checkbox: an ABSENT default
        // blocks the print on an unmatched value, an explicitly BLANK one
        // (stored as default="", spelled "" in the grid) falls back to empty
        Add(0, 2, "map", "map", new TextBox(), "Name of an etiq:map to run the value through.");
        var dflText = new TextBox();
        var dflBlank = new CheckBox { Text = "blank", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 0, 0) };
        var dflWrap = new FlowLayoutPanel { WrapContents = false, Margin = new Padding(0) };
        dflText.Width = 72; dflText.Margin = new Padding(2, 6, 0, 6);
        new ToolTip().SetToolTip(dflBlank, "Unmatched values fall back to EMPTY instead of blocking the print");
        dflWrap.Controls.Add(dflText); dflWrap.Controls.Add(dflBlank);
        Add(1, 2, "default", "default", dflWrap, "Map result when no row matches. Empty = NO default (an unmatched value blocks the print); tick blank to fall back to empty.");
        // the wrap already carries the row margin — zero it so the inner
        // textbox lines up with the other rows' editors
        dflWrap.Margin = new Padding(0);
        dflText.Margin = new Padding(2, 6, 0, 6);
        dflBlank.Margin = new Padding(4, 9, 0, 0);
        dflBlank.CheckedChanged += (_, _) =>
        {
            dflText.Enabled = !dflBlank.Checked;
            if (loading || g.CurrentRow is null || g.CurrentRow.IsNewRow) return;
            g.CurrentRow.Cells["default"].Value = dflBlank.Checked ? "\"\""
                : dflText.Text == "" ? null : dflText.Text;
            GridTools.RefreshSummaries(g);
            RefreshPreview();
        };

        void LoadRow()
        {
            loading = true;
            bool has = g.CurrentRow is not null && !g.CurrentRow.IsNewRow &&
                       g.CurrentRow.Cells["newline"].Value is not true;
            foreach (var (attr, c) in editors)
            {
                c.Enabled = has;
                string v = has ? g.CurrentRow!.Cells[attr].Value?.ToString() ?? "" : "";
                if (c is FlowLayoutPanel wrap)
                {
                    var tb = (TextBox)wrap.Controls[0];
                    var chk = wrap.Controls.OfType<CheckBox>().First();
                    chk.Enabled = has;
                    chk.Checked = v == "\"\"";
                    tb.Text = chk.Checked ? "" : v;
                    tb.Enabled = has && !chk.Checked;
                }
                else if (c is ComboBox cb) cb.SelectedItem = cb.Items.Contains(v) ? v : "";
                else ((TextBox)c).Text = v;
            }
            loading = false;
        }
        g.CurrentCellChanged += (_, _) => LoadRow();
        g.CellValueChanged += (_, e) => { if (e.RowIndex == g.CurrentRow?.Index) LoadRow(); };
        LoadRow();
        return pane;
    }

    // ---------- commit ----------

    /// <summary>Write the dialog state back to the field element. Returns
    /// false (and keeps the dialog open) on obviously broken input.</summary>
    private bool CommitBack() => CommitInto(_field, quiet: false);

    /// <summary>Write the dialog state into target (the real field on OK,
    /// a scratch copy for the preview). quiet=true skips the message box
    /// and the EndEdit (the preview must not disturb an in-progress cell).</summary>
    private bool CommitInto(XElement target, bool quiet)
    {
        if (!quiet && _conditional.Checked && _switchOn.Text.Trim() == "")
        {
            MessageBox.Show(this, "Conditional compose needs a switch-on field " +
                "(the field whose value picks the variant).", "Compose");
            return false;
        }
        target.SetAttributeValue("collapse-blank-lines", _collapse.Checked ? "true" : null);
        target.Elements(NS + "seg").Remove();
        target.Elements(NS + "variant").Remove();
        if (_conditional.Checked)
        {
            target.SetAttributeValue("switch-on", _switchOn.Text.Trim());
            foreach (var p in _pages)
            {
                var v = new XElement(NS + "variant");
                string kind = (string?)p.Kind.SelectedItem ?? "default";
                if (kind == "exact") v.SetAttributeValue("when", p.Match.Text);
                else if (kind == "prefix") v.SetAttributeValue("prefix", p.Match.Text);
                GridTools.CommitSegs(p.Grid, v, endEdit: !quiet);
                target.Add(v);
            }
        }
        else
        {
            target.SetAttributeValue("switch-on", null);
            GridTools.CommitSegs(_pages[0].Grid, target, endEdit: !quiet);
        }
        return true;
    }
}
