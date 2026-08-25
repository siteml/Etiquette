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

    public ComposeDialog(XElement field, IEnumerable<string> switchCandidates)
    {
        _field = field;

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
                   "row — emitted before it, only when both sides are non-empty " +
                   "(\", \" on the State row → \"City, State\"). Right-click rows to insert/reorder.",
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
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        _conditional.CheckedChanged += (_, _) => OnConditionalToggled();
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
        GridTools.AddSegColumns(p.Grid);
        GridTools.AttachRowTools(p.Grid);
        if (segSource is not null) GridTools.LoadSegs(p.Grid, segSource);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(strip, 0, 0);
        layout.Controls.Add(p.Grid, 0, 1);

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

    // ---------- commit ----------

    /// <summary>Write the dialog state back to the field element. Returns
    /// false (and keeps the dialog open) on obviously broken input.</summary>
    private bool CommitBack()
    {
        if (_conditional.Checked && _switchOn.Text.Trim() == "")
        {
            MessageBox.Show(this, "Conditional compose needs a switch-on field " +
                "(the field whose value picks the variant).", "Compose");
            return false;
        }
        _field.SetAttributeValue("collapse-blank-lines", _collapse.Checked ? "true" : null);
        _field.Elements(NS + "seg").Remove();
        _field.Elements(NS + "variant").Remove();
        if (_conditional.Checked)
        {
            _field.SetAttributeValue("switch-on", _switchOn.Text.Trim());
            foreach (var p in _pages)
            {
                var v = new XElement(NS + "variant");
                string kind = (string?)p.Kind.SelectedItem ?? "default";
                if (kind == "exact") v.SetAttributeValue("when", p.Match.Text);
                else if (kind == "prefix") v.SetAttributeValue("prefix", p.Match.Text);
                GridTools.CommitSegs(p.Grid, v);
                _field.Add(v);
            }
        }
        else
        {
            _field.SetAttributeValue("switch-on", null);
            GridTools.CommitSegs(_pages[0].Grid, _field);
        }
        return true;
    }
}
