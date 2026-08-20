using System.ComponentModel;
using System.Xml.Linq;
using Etiq.Core;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>
/// Fields / Maps / Lists editor (the template's etiq:label metadata).
/// Works on a CLONE of the element; OK hands the clone back and the caller
/// installs it as ONE undoable step (EditorDoc.ReplaceEtiqLabel). Unknown
/// attributes are preserved untouched. Compose segments/variants are edited
/// in their own resizable window (ComposeDialog) opened from the Fields tab.
/// </summary>
public sealed class MetadataDialog : Form
{
    private static readonly XNamespace NS = EditorDoc.EtiqNs;

    private readonly EditorDoc _doc;
    private readonly Action<XElement>? _apply;   // installs a clone into the doc
    private XElement _appliedSnapshot;           // Result state at open / last Apply
    public XElement Result { get; }

    /// <summary>True when Result differs from what was last applied (or from
    /// the opening state if Apply was never used). The caller skips the
    /// closing install when everything is already applied.</summary>
    public bool HasUnappliedChanges => !XNode.DeepEquals(Result, _appliedSnapshot);

    // fields tab
    private readonly ListBox _fieldList = new() { Dock = DockStyle.Fill };
    private readonly PropertyGrid _fieldProps = new()
        { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
    private readonly Button _editCompose = new()
        { Text = "Edit Compose…", Width = 120, Enabled = false, Anchor = AnchorStyles.Left };
    private readonly Label _composeSummary = new()
        { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText,
          Margin = new Padding(8, 8, 0, 0) };
    private XElement? _curField;   // field currently shown in the props grid

    // maps tab
    private readonly ListBox _mapList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _mapName = new() { Width = 180 };
    private readonly TextBox _mapDefault = new() { Width = 380 };
    private readonly DataGridView _whenGrid = GridTools.NewGrid();

    // lists tab
    private readonly ListBox _listList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _listName = new() { Width = 140 };
    private readonly TextBox _listCaption = new() { Width = 220 };
    private readonly TextBox _listKey = new() { Width = 100 };
    private readonly TextBox _listDefault = new() { Width = 120 };
    private readonly ComboBox _listDisplay = new()
        { Width = 170, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _listFilterCol = new() { Width = 110 };
    private readonly ComboBox _listFilterRef = new()
        { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ListBox _colList = new() { Width = 220, Height = 86 };
    private readonly DataGridView _rowGrid = GridTools.NewGrid();

    private XElement? _curMap, _curList;   // elements the grids currently show

    public MetadataDialog(EditorDoc doc, Action<XElement>? apply = null)
    {
        _doc = doc;
        _apply = apply;
        Result = doc.GetOrCreateEtiqLabelClone();
        _appliedSnapshot = new XElement(Result);

        Text = "Fields, Maps & Lists";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(800, 560);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildFieldsTab());
        tabs.TabPages.Add(BuildMapsTab());
        tabs.TabPages.Add(BuildListsTab());

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Height = 40, Padding = new Padding(6),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        var applyBtn = new Button { Text = "Apply", Width = 80, Enabled = _apply is not null };
        applyBtn.Click += (_, _) => ApplyNow();
        var revert = new Button { Text = "Revert", Width = 80 };
        revert.Click += (_, _) => RevertNow();
        var validate = new Button { Text = "Validate", Width = 80 };
        validate.Click += (_, _) => ValidateNow();
        var insertTpl = new Button { Text = "Insert Template ▾", Width = 130 };
        insertTpl.Click += (_, _) => ShowTemplateMenu(insertTpl);
        var saveTpl = new Button { Text = "Save as Template…", Width = 130 };
        saveTpl.Click += (_, _) => SaveSelectedAsTemplate();
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(applyBtn);
        bottom.Controls.Add(revert);
        bottom.Controls.Add(validate);
        bottom.Controls.Add(saveTpl);
        bottom.Controls.Add(insertTpl);

        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK) CommitPendingEdits();
        };

        RefreshFieldList();
        RefreshMapList();
        RefreshListList();

        // row order is meaningful (map rules match in doc order, list rows
        // display in order) - give the grids insert/reorder tools
        GridTools.AttachRowTools(_whenGrid);
        GridTools.AttachRowTools(_rowGrid);

        // declaration order is meaningful too: the data panel shows entries
        // in field order - right-click any name list to reorder
        AttachListReorder(_fieldList, "field", () => RefreshFieldList());
        AttachListReorder(_mapList, "map", RefreshMapList);
        AttachListReorder(_listList, "list", RefreshListList);
    }

    /// <summary>Right-click Move Up/Down on a declaration listbox: moves the
    /// XElement among its same-kind siblings (doc order = panel order).</summary>
    private void AttachListReorder(ListBox list, string kind, Action refresh)
    {
        var menu = new ContextMenuStrip();
        void MoveBy(int dir)
        {
            if (list.SelectedItem is not XElement el) return;
            var siblings = Result.Elements(NS + kind).ToList();
            int i = siblings.IndexOf(el);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= siblings.Count) return;
            el.Remove();
            if (dir < 0) siblings[j].AddBeforeSelf(el);
            else siblings[j].AddAfterSelf(el);
            refresh();
            list.SelectedItem = el;
        }
        menu.Items.Add("Move &Up", null, (_, _) => MoveBy(-1));
        menu.Items.Add("Move &Down", null, (_, _) => MoveBy(+1));
        list.ContextMenuStrip = menu;
        list.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int idx = list.IndexFromPoint(e.Location);
            if (idx >= 0) list.SelectedIndex = idx;
        };
    }

    private static TableLayoutPanel SplitPage(Control left, Control right,
                                              params (string Text, Action On)[] buttons)
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        page.Controls.Add(left, 0, 0);
        page.Controls.Add(right, 1, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var (text, on) in buttons)
        {
            var b = new Button { Text = text, Width = 90 };
            b.Click += (_, _) => on();
            bar.Controls.Add(b);
        }
        page.Controls.Add(bar, 0, 1);
        page.SetColumnSpan(bar, 2);
        return page;
    }

    // ---------- fields ----------

    private TabPage BuildFieldsTab()
    {
        var page = new TabPage("Fields");

        var composeBar = new FlowLayoutPanel
            { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
        composeBar.Controls.Add(_editCompose);
        composeBar.Controls.Add(_composeSummary);
        _editCompose.Click += (_, _) => OpenComposeDialog();

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.Controls.Add(_fieldProps, 0, 0);
        right.Controls.Add(composeBar, 0, 1);

        page.Controls.Add(SplitPage(_fieldList, right,
            ("Add", (Action)AddField),
            ("Remove", (Action)(() => RemoveSelected(_fieldList, () => RefreshFieldList())))));
        _fieldList.SelectedIndexChanged += (_, _) =>
        {
            _curField = _fieldList.SelectedItem as XElement;
            _fieldProps.SelectedObject = _curField is not null
                ? new FieldMetaProps(_curField, () =>
                    { RefreshFieldList(keepSelection: true); UpdateComposeUi(); })
                : null;
            UpdateComposeUi();
        };
        _fieldList.Format += (_, e) =>
        {
            if (e.ListItem is XElement el)
                e.Value = $"{(string?)el.Attribute("name") ?? "(unnamed)"}  [{(string?)el.Attribute("source") ?? "?"}]";
        };
        _fieldList.FormattingEnabled = true;
        return page;
    }

    /// <summary>Enable the Edit Compose button for compose fields and show
    /// a one-line summary of what the field composes.</summary>
    private void UpdateComposeUi()
    {
        bool isCompose = (string?)_curField?.Attribute("source") == "compose";
        _editCompose.Enabled = isCompose;
        if (!isCompose) { _composeSummary.Text = ""; return; }
        var variants = _curField!.Elements(NS + "variant").ToList();
        _composeSummary.Text = variants.Count > 0
            ? $"switch on {(string?)_curField.Attribute("switch-on") ?? "?"} — {variants.Count} variant(s)"
            : $"{_curField.Elements(NS + "seg").Count()} segment(s)";
    }

    /// <summary>Open the full-size compose editor for the selected field.
    /// It edits the field element inside this dialog's working clone, so
    /// its OK still lands inside this dialog's single undo step.</summary>
    private void OpenComposeDialog()
    {
        if (_curField is null || (string?)_curField.Attribute("source") != "compose") return;
        string self = (string?)_curField.Attribute("name") ?? "";
        var candidates = Result.Elements(NS + "field")
            .Where(f => (string?)f.Attribute("source") != "compose")
            .Select(f => (string?)f.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n) && n != self)
            .Select(n => n!);
        using var dlg = new ComposeDialog(_curField, candidates);
        dlg.ShowDialog(this);
        UpdateComposeUi();
    }

    private void AddField()
    {
        var el = new XElement(NS + "field",
            new XAttribute("name", UniqueName("Field", "field")),
            new XAttribute("source", "prompt"));
        Result.Add(el);
        RefreshFieldList();
        _fieldList.SelectedItem = el;
    }

    private void RefreshFieldList(bool keepSelection = false)
    {
        var sel = keepSelection ? _fieldList.SelectedItem : null;
        _fieldList.Items.Clear();
        foreach (var el in Result.Elements(NS + "field")) _fieldList.Items.Add(el);
        if (sel is not null && _fieldList.Items.Contains(sel)) _fieldList.SelectedItem = sel;
    }

    // ---------- maps ----------

    private TabPage BuildMapsTab()
    {
        var page = new TabPage("Maps");
        _whenGrid.Columns.Add(NewComboCol("Kind", "exact (from)", "prefix"));
        _whenGrid.Columns.Add("Match", "Match");
        _whenGrid.Columns.Add("To", "To");

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(new Label { Text = "Name", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        right.Controls.Add(_mapName, 1, 0);
        right.Controls.Add(new Label { Text = "Default", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        right.Controls.Add(_mapDefault, 1, 1);
        right.Controls.Add(_whenGrid, 0, 2);
        right.SetColumnSpan(_whenGrid, 2);

        page.Controls.Add(SplitPage(_mapList, right,
            ("Add", (Action)AddMap),
            ("Remove", (Action)(() => { RemoveSelected(_mapList, RefreshMapList); LoadMap(); }))));
        _mapList.SelectedIndexChanged += (_, _) => { CommitMap(); LoadMap(); };
        _mapList.Format += (_, e) =>
        {
            if (e.ListItem is XElement el) e.Value = (string?)el.Attribute("name") ?? "(unnamed)";
        };
        _mapList.FormattingEnabled = true;
        return page;
    }

    private static DataGridViewComboBoxColumn NewComboCol(string name, params string[] items)
    {
        var c = new DataGridViewComboBoxColumn { Name = name, HeaderText = name };
        c.Items.AddRange(items);
        return c;
    }

    private void AddMap()
    {
        CommitMap();
        var el = new XElement(NS + "map", new XAttribute("name", UniqueName("Map", "map")));
        Result.Add(el);
        RefreshMapList();
        _mapList.SelectedItem = el;
    }

    private void RefreshMapList()
    {
        _mapList.Items.Clear();
        foreach (var el in Result.Elements(NS + "map")) _mapList.Items.Add(el);
    }

    private void LoadMap()
    {
        _curMap = _mapList.SelectedItem as XElement;
        _whenGrid.Rows.Clear();
        _mapName.Text = (string?)_curMap?.Attribute("name") ?? "";
        _mapDefault.Text = (string?)_curMap?.Attribute("default") ?? "";
        if (_curMap is null) return;
        foreach (var w in _curMap.Elements(NS + "when"))
        {
            bool prefix = w.Attribute("prefix") is not null;
            _whenGrid.Rows.Add(prefix ? "prefix" : "exact (from)",
                (string?)(prefix ? w.Attribute("prefix") : w.Attribute("from")) ?? "",
                (string?)w.Attribute("to") ?? "");
        }
    }

    /// <summary>Write the visible map editor back to its element.</summary>
    private void CommitMap()
    {
        if (_curMap is null) return;
        if (_mapName.Text.Trim() is { Length: > 0 } nm) _curMap.SetAttributeValue("name", nm);
        _curMap.SetAttributeValue("default", _mapDefault.Text == "" ? null : _mapDefault.Text);
        _curMap.Elements(NS + "when").Remove();
        foreach (DataGridViewRow row in _whenGrid.Rows)
        {
            if (row.IsNewRow) continue;
            string match = row.Cells["Match"].Value?.ToString() ?? "";
            string to = row.Cells["To"].Value?.ToString() ?? "";
            if (match == "" && to == "") continue;
            bool prefix = row.Cells["Kind"].Value?.ToString() == "prefix";
            _curMap.Add(new XElement(NS + "when",
                new XAttribute(prefix ? "prefix" : "from", match),
                new XAttribute("to", to)));
        }
    }

    // ---------- lists ----------

    private TabPage BuildListsTab()
    {
        var page = new TabPage("Lists");

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        void Row(int r, string label, Control c)
        {
            right.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, r);
            right.Controls.Add(c, 1, r);
        }
        Row(0, "Name", _listName);
        Row(1, "Data panel caption", _listCaption);

        var keyRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        keyRow.Controls.Add(_listKey);
        keyRow.Controls.Add(new Label { Text = "  Default key:", AutoSize = true, Anchor = AnchorStyles.Left });
        keyRow.Controls.Add(_listDefault);
        Row(2, "Key column", keyRow);

        Row(3, "Picker shows field", _listDisplay);
        _listDisplay.Items.Add("");   // empty = key — Name heuristic

        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        filterRow.Controls.Add(new Label { Text = "column", AutoSize = true, Anchor = AnchorStyles.Left });
        filterRow.Controls.Add(_listFilterCol);
        filterRow.Controls.Add(new Label { Text = " equals field", AutoSize = true, Anchor = AnchorStyles.Left });
        filterRow.Controls.Add(_listFilterRef);
        Row(4, "Filter rows where", filterRow);

        // columns: add / remove / reorder (order = row grid + display order)
        var colPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        colPanel.Controls.Add(_colList);
        var colBtns = new FlowLayoutPanel
            { FlowDirection = FlowDirection.TopDown, Width = 96, Height = 92, WrapContents = false };
        void ColBtn(string text, Action on)
        {
            var b = new Button { Text = text, Width = 88, Height = 22, Margin = new Padding(2, 0, 0, 1) };
            b.Click += (_, _) => { on(); RebuildRowGridColumns(); };
            colBtns.Controls.Add(b);
        }
        ColBtn("Add…", () =>
        {
            string? c = Prompts.PromptText(this, "New column name", "");
            if (!string.IsNullOrWhiteSpace(c) && !_colList.Items.Contains(c.Trim()))
                _colList.Items.Add(c.Trim());
        });
        ColBtn("Remove", () =>
        {
            if (_colList.SelectedItem is not string c) return;
            if (c == _listKey.Text.Trim())
            { MessageBox.Show(this, "That is the key column — change Key column first.", "Columns"); return; }
            _colList.Items.Remove(c);
        });
        ColBtn("Move Up", () => MoveColumn(-1));
        ColBtn("Move Down", () => MoveColumn(+1));
        colPanel.Controls.Add(colBtns);
        Row(5, "Columns", colPanel);

        right.Controls.Add(_rowGrid, 0, 6);
        right.SetColumnSpan(_rowGrid, 2);

        page.Controls.Add(SplitPage(_listList, right,
            ("Add", (Action)AddList),
            ("Remove", (Action)(() => { RemoveSelected(_listList, RefreshListList); LoadList(); }))));
        _listList.SelectedIndexChanged += (_, _) => { CommitList(); LoadList(); };
        _listList.Format += (_, e) =>
        {
            if (e.ListItem is XElement el) e.Value = (string?)el.Attribute("name") ?? "(unnamed)";
        };
        _listList.FormattingEnabled = true;
        return page;
    }

    private void AddList()
    {
        CommitList();
        var el = new XElement(NS + "list",
            new XAttribute("name", UniqueName("List", "list")),
            new XAttribute("key", "Name"));
        Result.Add(el);
        RefreshListList();
        _listList.SelectedItem = el;
    }

    private void RefreshListList()
    {
        _listList.Items.Clear();
        foreach (var el in Result.Elements(NS + "list")) _listList.Items.Add(el);
    }

    private void MoveColumn(int dir)
    {
        int i = _colList.SelectedIndex;
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _colList.Items.Count) return;
        var it = _colList.Items[i];
        _colList.Items.RemoveAt(i);
        _colList.Items.Insert(j, it);
        _colList.SelectedIndex = j;
    }

    private void LoadList()
    {
        _curList = _listList.SelectedItem as XElement;
        _rowGrid.Columns.Clear();
        _rowGrid.Rows.Clear();
        _colList.Items.Clear();
        _listName.Text = (string?)_curList?.Attribute("name") ?? "";
        _listCaption.Text = (string?)_curList?.Attribute("caption") ?? "";
        _listKey.Text = (string?)_curList?.Attribute("key") ?? "";
        _listDefault.Text = (string?)_curList?.Attribute("default") ?? "";
        _listDisplay.Text = (string?)_curList?.Attribute("display") ?? "";
        _listFilterCol.Text = (string?)_curList?.Attribute("filter-column") ?? "";
        _listFilterRef.Text = (string?)_curList?.Attribute("filter-ref") ?? "";
        // declared field names for the display / filter-ref dropdowns
        foreach (var combo in new[] { _listDisplay, _listFilterRef })
        {
            string keep = combo.Text;
            combo.Items.Clear();
            combo.Items.Add("");
            foreach (var f in Result.Elements(NS + "field"))
                if ((string?)f.Attribute("name") is { } fn) combo.Items.Add(fn);
            combo.Text = keep;
        }
        if (_curList is null) return;
        // column order: the columns= attribute is the truth (rows are
        // SPARSE — empty cells carry no attribute, so deriving order from
        // row attributes reshuffles after a reorder); fall back to key +
        // first-seen row attributes, and append anything not yet listed
        var cols = new List<string>();
        var declared = ((string?)_curList.Attribute("columns"))
            ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (declared is not null)
            foreach (var c in declared)
                if (!cols.Contains(c)) cols.Add(c);
        if (_listKey.Text != "" && !cols.Contains(_listKey.Text)) cols.Insert(0, _listKey.Text);
        foreach (var r in _curList.Elements(NS + "row"))
            foreach (var a in r.Attributes())
                if (!cols.Contains(a.Name.LocalName)) cols.Add(a.Name.LocalName);
        foreach (var c in cols) _colList.Items.Add(c);
        RebuildRowGridColumns();
        foreach (var r in _curList.Elements(NS + "row"))
        {
            var vals = cols.Select(c => (object)((string?)r.Attribute(c) ?? "")).ToArray();
            _rowGrid.Rows.Add(vals);
        }
    }

    private void RebuildRowGridColumns()
    {
        // preserve typed rows when only columns change
        var oldCols = _rowGrid.Columns.Cast<DataGridViewColumn>().Select(c => c.Name).ToList();
        var data = _rowGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => oldCols.ToDictionary(c => c, c => r.Cells[c].Value?.ToString() ?? ""))
            .ToList();
        _rowGrid.Columns.Clear();
        foreach (var c in ColumnNames())
            _rowGrid.Columns.Add(c, c);
        foreach (var d in data)
            _rowGrid.Rows.Add(ColumnNames().Select(c => (object)d.GetValueOrDefault(c, "")).ToArray());
    }

    private List<string> ColumnNames() =>
        _colList.Items.Cast<string>().Distinct().ToList();

    private void CommitList()
    {
        if (_curList is null) return;
        if (_listName.Text.Trim() is { Length: > 0 } nm) _curList.SetAttributeValue("name", nm);
        _curList.SetAttributeValue("key", _listKey.Text == "" ? null : _listKey.Text);
        _curList.SetAttributeValue("default", _listDefault.Text == "" ? null : _listDefault.Text);
        _curList.SetAttributeValue("caption", _listCaption.Text.Trim() is { Length: > 0 } cap ? cap : null);
        _curList.SetAttributeValue("display", _listDisplay.Text.Trim() is { Length: > 0 } dsp ? dsp : null);
        _curList.SetAttributeValue("filter-column", _listFilterCol.Text.Trim() is { Length: > 0 } fc ? fc : null);
        _curList.SetAttributeValue("filter-ref", _listFilterRef.Text.Trim() is { Length: > 0 } fr ? fr : null);
        _curList.Elements(NS + "row").Remove();
        var cols = ColumnNames();
        // persist the order explicitly — sparse rows can't carry it
        _curList.SetAttributeValue("columns", cols.Count > 0 ? string.Join(",", cols) : null);
        foreach (DataGridViewRow row in _rowGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var el = new XElement(NS + "row");
            bool any = false;
            foreach (var c in cols)
            {
                string v = row.Cells[c].Value?.ToString() ?? "";
                if (v == "") continue;
                el.SetAttributeValue(c, v);
                any = true;
            }
            if (any) _curList.Add(el);
        }
    }

    // ---------- shared ----------

    private void RemoveSelected(ListBox list, Action refresh)
    {
        if (list.SelectedItem is not XElement el) return;
        if (ReferenceEquals(el, _curMap)) _curMap = null;
        if (ReferenceEquals(el, _curList)) _curList = null;
        if (ReferenceEquals(el, _curField)) { _curField = null; _fieldProps.SelectedObject = null; UpdateComposeUi(); }
        el.Remove();
        refresh();
    }

    private string UniqueName(string stem, string kind)
    {
        var taken = Result.Elements(NS + kind)
            .Select(e => (string?)e.Attribute("name")).ToHashSet();
        for (int i = 1; ; i++)
            if (!taken.Contains(stem + i)) return stem + i;
    }

    private void CommitPendingEdits()
    {
        // compose segs commit inside ComposeDialog on its own OK
        CommitMap();
        CommitList();
    }

    /// <summary>Push the current edits into the document WITHOUT closing —
    /// one undo step per Apply. The dialog keeps editing its own copy.</summary>
    private void ApplyNow()
    {
        CommitPendingEdits();
        if (_apply is null || !HasUnappliedChanges) return;
        _apply(new XElement(Result));
        _appliedSnapshot = new XElement(Result);
    }

    /// <summary>Throw away edits made since the last Apply (or since the
    /// dialog opened) and reload the working copy from that state.</summary>
    private void RevertNow()
    {
        CommitPendingEdits();
        if (!HasUnappliedChanges) return;
        if (MessageBox.Show(this,
                "Discard all changes made since the dialog opened" +
                (_apply is not null ? " (or since the last Apply)" : "") + "?",
                "Revert", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        // drop stale element references BEFORE the tree is replaced
        _curField = null;
        _curMap = null;
        _curList = null;
        _fieldProps.SelectedObject = null;
        var fresh = new XElement(_appliedSnapshot);
        Result.ReplaceAll(fresh.Attributes().Cast<object>().Concat(fresh.Nodes()).ToArray());
        RefreshFieldList();
        RefreshMapList();
        RefreshListList();
        LoadMap();
        LoadList();
        UpdateComposeUi();
    }

    // ---------- templates (snippets) ----------

    private static string UserSnippetDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Etiquette", "snippets");

    private static List<Snippet> LoadSnippets() => SnippetLibrary.Load(
        System.IO.Path.Combine(AppContext.BaseDirectory, "snippets"),
        UserSnippetDir);

    /// <summary>Insert a snippet's fields/maps/lists into the working clone;
    /// colliding names get suffixed and internal refs rewritten.</summary>
    private void ShowTemplateMenu(Control anchor)
    {
        var snippets = LoadSnippets();
        var menu = new ContextMenuStrip();
        if (snippets.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(
                "(no templates found — ship dir 'snippets\\' or save one)") { Enabled = false });
        foreach (var s in snippets)
        {
            var it = new ToolStripMenuItem(s.Name) { ToolTipText = s.Description };
            var snip = s;
            it.Click += (_, _) =>
            {
                CommitPendingEdits();
                bool Taken(string n) => Result.Elements()
                    .Any(e => (string?)e.Attribute("name") == n);
                foreach (var el in SnippetLibrary.Materialize(snip, Taken))
                    Result.Add(el);
                RefreshFieldList();
                RefreshMapList();
                RefreshListList();
            };
            menu.Items.Add(it);
        }
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    /// <summary>Save the selected field plus everything it references
    /// (helper fields, maps, lists) to the user snippet folder.</summary>
    private void SaveSelectedAsTemplate()
    {
        CommitPendingEdits();
        if (_fieldList.SelectedItem is not XElement fieldEl)
        {
            MessageBox.Show(this, "Select a field on the Fields tab first — it is saved together with everything it references.", "Save as Template");
            return;
        }
        string suggested = (string?)fieldEl.Attribute("name") ?? "Template";
        string? name = Prompts.PromptText(this, "Template name", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var snip = SnippetLibrary.Package(name, fieldEl, Result);
            Directory.CreateDirectory(UserSnippetDir);
            string file = System.IO.Path.Combine(UserSnippetDir,
                string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'))
                + ".snippet.xml");
            snip.Save(file);
            MessageBox.Show(this, $"Saved. It now appears under Insert Template on every label.\n{file}", "Save as Template");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save as Template failed");
        }
    }

    private void ValidateNow()
    {
        CommitPendingEdits();
        try
        {
            // probe: this document with the edited metadata swapped in
            var probe = new XDocument(_doc.Xml);
            var meta = probe.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var label = meta?.Element(NS + "label");
            if (label is not null) label.ReplaceWith(new XElement(Result));
            else if (meta is not null) meta.Add(new XElement(Result));
            else probe.Root!.AddFirst(new XElement(probe.Root!.Name.Namespace + "metadata", new XElement(Result)));
            var findings = TemplateValidator.Validate(EtiqTemplate.Parse(probe.ToString()));
            MessageBox.Show(this,
                findings.Count == 0 ? "No findings — metadata is clean."
                    : string.Join(Environment.NewLine, findings.Select(f => f.ToString())),
                $"Validate — {findings.Count} finding(s)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Validate failed");
        }
    }
}

/// <summary>PropertyGrid adapter for one etiq:field element (edits the
/// dialog's working clone directly, so unknown attributes survive).</summary>
public sealed class FieldMetaProps
{
    private readonly XElement _el;
    private readonly Action _changed;
    public FieldMetaProps(XElement el, Action changed) { _el = el; _changed = changed; }

    private string Get(string a) => (string?)_el.Attribute(a) ?? "";
    private void Set(string a, string? v)
    {
        _el.SetAttributeValue(a, string.IsNullOrEmpty(v) ? null : v);
        _changed();
    }

    [Category("Field"), Description("Unique field name; bind elements to it via data-field")]
    public string Name { get => Get("name"); set => Set("name", value); }

    [Category("Field"), TypeConverter(typeof(SourceKindConverter)),
     Description("epicor | rest | prompt | serial | auto | fixed | compose | list (db/file/device reserved)")]
    public string Source { get => Get("source"); set => Set("source", value); }

    [Category("Field"), Description("Prompt caption shown to the operator (source=prompt)")]
    public string Caption { get => Get("caption"); set => Set("caption", value); }

    [Category("Field"), Description("Fixed/auto value (source=fixed, or auto e.g. date:dd-MMM-yyyy)")]
    public string Value { get => Get("value"); set => Set("value", value); }

    [Category("Field"), Description("Result casing: (empty=normal) | upper | lower | title")]
    public string Case { get => Get("case"); set => Set("case", value); }

    [Category("Field"), Description("compose only: drop lines that end up empty (address-block blank suppression)")]
    public bool CollapseBlankLines
    {
        get => Get("collapse-blank-lines") == "true";
        set => Set("collapse-blank-lines", value ? "true" : null);
    }

    [Category("Field")]
    public bool Required
    {
        get => Get("required") == "true";
        set => Set("required", value ? "true" : null);
    }

    [Category("Field"), Description("block (default) | cached | use:VALUE")]
    public string OnFail { get => Get("on-fail"); set => Set("on-fail", value); }

    [Category("Field"), Description("Value used when the source resolves empty")]
    public string IfEmpty { get => Get("if-empty"); set => Set("if-empty", value); }

    [Category("Field"), Description("BAQ column (source=epicor) or list row column (source=list)")]
    public string Column { get => Get("column"); set => Set("column", value); }

    [Category("Serial"), Description("Counter name (source=serial)")]
    public string Counter { get => Get("counter"); set => Set("counter", value); }

    [Category("Serial"), Description("Serial format, e.g. 0000 or base36:4")]
    public string Format { get => Get("format"); set => Set("format", value); }

    [Category("List"), Description("Embedded list this field reads (source=list); value comes from Column")]
    public string List { get => Get("list"); set => Set("list", value); }

    [Category("Rest"), Description("Connection profile name (source=rest)")]
    public string Connection { get => Get("connection"); set => Set("connection", value); }

    [Category("Rest"), Description("Query / endpoint path")]
    public string Query { get => Get("query"); set => Set("query", value); }

    [Category("Rest"), Description("Dotted JSON path into the response, e.g. assets.0.name")]
    public string Pick { get => Get("pick"); set => Set("pick", value); }

    public override string ToString() => Get("name");
}

public sealed class AlignConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
        new(new[] { "left", "center", "right" });
}

public sealed class FitConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
        new(new[] { "", "none", "width", "box" });
}

public sealed class VAlignConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
        new(new[] { "top", "middle", "bottom" });
}

/// <summary>Dropdown of valid source kinds in the PropertyGrid.</summary>
public sealed class SourceKindConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
        new(new[] { "epicor", "rest", "prompt", "serial", "auto", "fixed", "compose", "list" });
}

/// <summary>Dropdown of the template's declared field names for the
/// data-field property; MainForm refreshes Names when metadata changes.</summary>
public sealed class FieldNameConverter : StringConverter
{
    public static string[] Names = Array.Empty<string>();
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
        new(Names);
}
