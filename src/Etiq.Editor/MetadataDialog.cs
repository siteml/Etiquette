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
    private readonly FieldPane _fieldPane = new() { Dock = DockStyle.Fill };
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
    private readonly CheckBox _mapBlank = new() { Text = "blank", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly DataGridView _whenGrid = GridTools.NewGrid();

    // lists tab
    private readonly ListBox _listList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _listName = new() { Width = 140 };
    private readonly TextBox _listCaption = new() { Width = 220 };
    private readonly TextBox _listKey = new() { Width = 100 };
    private readonly TextBox _listDefault = new() { Width = 120 };
    private readonly TextBox _listFrom = new() { Width = 160 };
    private readonly ComboBox _listDisplay = new()
        { Width = 170, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _listFilterCol = new() { Width = 110 };
    private readonly ComboBox _listFilterRef = new()
        { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ListBox _colList = new() { Width = 220, Height = 86 };
    private readonly DataGridView _rowGrid = GridTools.NewGrid();

    // sources tab (declared remote fetches — one BAQ row per label)
    private readonly ListBox _srcList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _srcName = new() { Width = 160 };
    private readonly TextBox _srcConn = new() { Width = 160 };
    private readonly TextBox _srcDataset = new() { Width = 160 };
    private readonly TextBox _srcBaq = new() { Width = 240 };
    private readonly TextBox _srcQuery = new() { Width = 240 };
    private readonly DataGridView _srcArgGrid = GridTools.NewGrid();

    // panel tab (data-mode presentation)
    private readonly ComboBox _pnPrint = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _pnPrinter = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _pnCopies = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _pnFixed = new() { Width = 70, Minimum = 1, Maximum = 999, Value = 1, Visible = false };
    private readonly ComboBox _pnCollate = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _pnBtnPreview = new() { Text = "Refresh Preview", AutoSize = true, Checked = true };
    private readonly CheckBox _pnBtnPrint = new() { Text = "Print", AutoSize = true, Checked = true };
    private readonly CheckBox _pnBtnPrintAll = new() { Text = "Print All", AutoSize = true, Checked = true };
    private readonly CheckBox _pnBtnClear = new() { Text = "Clear", AutoSize = true, Checked = true };
    private readonly CheckBox _pnBtnLog = new() { Text = "Log", AutoSize = true };
    private readonly ComboBox _pnButtonsAt = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _pnInputs = new() { Width = 300, Height = 170, CheckOnClick = true };
    private List<string> _pnNaturalOrder = new();   // tokens in declaration order

    private XElement? _curMap, _curList, _curSrc;   // elements the grids currently show

    /// <summary>Sample values per field name for the compose preview: the
    /// canvas's last resolved values, else the elements' design-time text.</summary>
    private readonly IReadOnlyDictionary<string, string>? _samples;

    public MetadataDialog(EditorDoc doc, Action<XElement>? apply = null,
                          IReadOnlyDictionary<string, string>? samples = null)
    {
        _doc = doc;
        _apply = apply;
        _samples = samples;
        Result = doc.GetOrCreateEtiqLabelClone();
        _appliedSnapshot = new XElement(Result);

        Text = "Fields, Maps & Lists";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Ui.AutoScale(this);
        ClientSize = new Size(800, 560);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildFieldsTab());
        tabs.TabPages.Add(BuildMapsTab());
        tabs.TabPages.Add(BuildListsTab());
        tabs.TabPages.Add(BuildSourcesTab());
        tabs.TabPages.Add(BuildPanelTab());

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
        right.Controls.Add(_fieldPane, 0, 0);
        right.Controls.Add(composeBar, 0, 1);

        page.Controls.Add(SplitPage(_fieldList, right,
            ("Add", (Action)AddField),
            ("Remove", (Action)(() => RemoveSelected(_fieldList, () => RefreshFieldList())))));
        _fieldList.SelectedIndexChanged += (_, _) =>
        {
            _curField = _fieldList.SelectedItem as XElement;
            _fieldPane.SetField(_curField, FieldChoices, () =>
            {
                // reformat in place — a full list rebuild would reselect and
                // tear the pane down under the operator's caret
                int i = _fieldList.SelectedIndex;
                if (i >= 0) _fieldList.Items[i] = _fieldList.Items[i];
                UpdateComposeUi();
            });
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

    /// <summary>Live combo content for the field pane, read from the
    /// working clone (and the machine's connections file) at drop-down
    /// build time — so newly added queries/lists show up immediately.</summary>
    private string[] FieldChoices(string key) => key switch
    {
        "queries" => Result.Elements(NS + "query")
            .Select(q => (string?)q.Attribute("name") ?? "").Where(n => n != "").ToArray(),
        "lists" => Result.Elements(NS + "list")
            .Select(l => (string?)l.Attribute("name") ?? "").Where(n => n != "").ToArray(),
        "columns" => (string?)_curField?.Attribute("list") is { } lr
            ? Result.Elements(NS + "list")
                .FirstOrDefault(l => (string?)l.Attribute("name") == lr)
                ?.Attribute("columns")?.Value.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? Array.Empty<string>()
            : Array.Empty<string>(),
        "connections" => SafeConnectionNames(),
        _ => Array.Empty<string>(),
    };

    private static string[] SafeConnectionNames()
    {
        try
        {
            return ConnectionsStore.Load(MainForm.ConnectionsPath)
                .Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray()!;
        }
        catch { return Array.Empty<string>(); }
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
        using var dlg = new ComposeDialog(_curField, candidates, PreviewCompose);
        dlg.ShowDialog(this);
        UpdateComposeUi();
    }

    /// <summary>Resolve one compose field (a scratch copy from the dialog)
    /// against the working label, with every NON-compose field pinned to
    /// its sample value — so lists, prompts and remote pulls preview
    /// without touching the network. Maps stay live. Throws with the
    /// resolver's message on a broken definition.</summary>
    private string PreviewCompose(XElement tmpField)
    {
        string name = (string?)tmpField.Attribute("name") ?? "";
        var label = new XElement(Result);
        var existing = label.Elements(NS + "field")
            .FirstOrDefault(f => (string?)f.Attribute("name") == name);
        if (existing is not null) existing.ReplaceWith(new XElement(tmpField));
        else label.Add(new XElement(tmpField));
        foreach (var f in label.Elements(NS + "field").ToList())
        {
            if ((string?)f.Attribute("source") == "compose") continue;
            string fn = (string?)f.Attribute("name") ?? "";
            f.ReplaceWith(new XElement(NS + "field",
                new XAttribute("name", fn),
                new XAttribute("source", "fixed"),
                new XAttribute("value", _samples?.GetValueOrDefault(fn) ?? "")));
        }
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = new XElement(svg + "svg",
            new XAttribute("width", "1in"), new XAttribute("height", "1in"),
            new XAttribute("viewBox", "0 0 1000 1000"),
            new XElement(svg + "metadata", label));
        var t = EtiqTemplate.Parse(root.ToString());
        try { return new FieldResolver(t, new ResolveContext()).Resolve(name); }
        catch (ResolveException ex) { return "⚠ " + ex.Message; }
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
        _mapName.Dock = DockStyle.Fill;
        _mapName.Margin = new Padding(2, 4, 8, 2);
        _mapDefault.Width = 260; _mapDefault.Margin = new Padding(2, 4, 4, 2);
        var mapDflWrap = new FlowLayoutPanel { WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        mapDflWrap.Controls.Add(_mapDefault); mapDflWrap.Controls.Add(_mapBlank);
        _mapBlank.Margin = new Padding(6, 8, 0, 0);
        _mapBlank.CheckedChanged += (_, _) => _mapDefault.Enabled = !_mapBlank.Checked;
        new ToolTip().SetToolTip(_mapDefault,
            "Result when no row matches. Empty = NO default (unmatched blocks the print); tick blank to fall back to empty.");
        right.Controls.Add(_mapName, 1, 0);
        right.Controls.Add(new Label { Text = "Default", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        right.Controls.Add(mapDflWrap, 1, 1);
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
        string? mapDflt = (string?)_curMap?.Attribute("default");
        _mapBlank.Checked = mapDflt == "";
        _mapDefault.Text = mapDflt is null or "" ? "" : mapDflt;
        _mapDefault.Enabled = !_mapBlank.Checked;
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
        _curMap.SetAttributeValue("default",
            _mapBlank.Checked ? "" : _mapDefault.Text == "" ? null : _mapDefault.Text);
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

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        void Row(int r, string label, Control c)
        {
            right.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, r);
            right.Controls.Add(c, 1, r);
        }
        foreach (var c in new Control[] { _listName, _listCaption, _listDisplay })
        { c.Dock = DockStyle.Fill; c.Margin = new Padding(2, 4, 8, 2); }
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

        var fromRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        fromRow.Controls.Add(_listFrom);
        fromRow.Controls.Add(new Label
        {
            Text = "  (query-fed picker: ALL rows of that etiq:query; embedded rows below are ignored)",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText,
        });
        Row(5, "Rows from query", fromRow);

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
        Row(6, "Columns", colPanel);

        right.Controls.Add(_rowGrid, 0, 7);
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
        _listFrom.Text = (string?)_curList?.Attribute("from") ?? "";
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
        _curList.SetAttributeValue("from", _listFrom.Text.Trim() is { Length: > 0 } lfrom ? lfrom : null);
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
        if (ReferenceEquals(el, _curField)) { _curField = null; _fieldPane.SetField(null, FieldChoices, () => { }); UpdateComposeUi(); }
        el.Remove();
        refresh();
    }

    // ---------- sources ----------

    private TabPage BuildSourcesTab()
    {
        var page = new TabPage("Queries");
        _srcArgGrid.Columns.Add(NewComboCol("Kind", "param", "filter"));
        _srcArgGrid.Columns.Add("Name", "Name");
        _srcArgGrid.Columns.Add("Value", "Value");

        var right = new Panel { Dock = DockStyle.Fill };
        void Row(int top, string label, TextBox tb)
        {
            right.Controls.Add(new Label
                { Text = label, Left = 6, Top = top + 3, AutoSize = true });
            tb.Left = 116; tb.Top = top;
            right.Controls.Add(tb);
        }
        Row(8, "Name", _srcName);
        Row(38, "Connection", _srcConn);
        Row(68, "Dataset pin", _srcDataset);
        Row(98, "BAQ", _srcBaq);
        Row(128, "Item type", _srcQuery);
        var tips = new ToolTip();
        tips.SetToolTip(_srcBaq, "epicor connections: the BAQ id");
        tips.SetToolTip(_srcQuery, "glpi connections: the item type (Computer, Monitor, NetworkEquipment, Printer, …)");
        var hint = new Label
        {
            Text = "Params/filters — Value is a literal, or {FieldName} to feed a field's " +
                   "resolved value in. Fields consume this query via From + Column. " +
                   "Epicor: param-<BAQ parameter>, filter-<display column>. GLPI: param-id, or filter-<column> (serial, otherserial, name…).",
            Left = 6, Top = 158, AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        right.Controls.Add(hint);
        _srcArgGrid.Dock = DockStyle.None;   // NewGrid() docks Fill — here it's positioned
        right.Controls.Add(_srcArgGrid);
        right.Resize += (_, _) =>
        {
            // the hint's height depends on wrap (width, DPI, font) — measure
            // it and put the grid BELOW it, never at a guessed fixed offset
            hint.Width = right.Width - 12;
            hint.Height = TextRenderer.MeasureText(hint.Text, hint.Font,
                new Size(hint.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4;
            int top = hint.Bottom + 6;
            _srcArgGrid.SetBounds(6, top, right.Width - 12, right.Height - top - 6);
        };

        page.Controls.Add(SplitPage(_srcList, right,
            ("Add", (Action)AddSource),
            ("Remove", (Action)(() => { RemoveSelected(_srcList, RefreshSourceList); LoadSource(); }))));
        _srcList.SelectedIndexChanged += (_, _) => { CommitSource(); LoadSource(); };
        _srcList.Format += (_, e) =>
        {
            if (e.ListItem is XElement el)
                e.Value = $"{(string?)el.Attribute("name") ?? "(unnamed)"}  [{(string?)el.Attribute("connection") ?? "?"}]";
        };
        _srcList.FormattingEnabled = true;
        RefreshSourceList();
        if (_srcList.Items.Count > 0) _srcList.SelectedIndex = 0; else LoadSource();
        return page;
    }

    private void AddSource()
    {
        CommitSource();
        var el = new XElement(NS + "query",
            new XAttribute("name", UniqueName("Query", "query")),
            new XAttribute("connection", ""));
        Result.Add(el);
        RefreshSourceList();
        _srcList.SelectedItem = el;
    }

    private void RefreshSourceList()
    {
        _srcList.Items.Clear();
        foreach (var el in Result.Elements(NS + "query")
                     .Concat(Result.Elements(NS + "source")))
            _srcList.Items.Add(el);
    }

    private void LoadSource()
    {
        _curSrc = _srcList.SelectedItem as XElement;
        _srcName.Text = (string?)_curSrc?.Attribute("name") ?? "";
        _srcConn.Text = (string?)_curSrc?.Attribute("connection") ?? "";
        _srcDataset.Text = (string?)_curSrc?.Attribute("dataset") ?? "";
        _srcBaq.Text = (string?)_curSrc?.Attribute("baq") ?? "";
        _srcQuery.Text = (string?)_curSrc?.Attribute("query") ?? "";
        _srcArgGrid.Rows.Clear();
        bool en = _curSrc is not null;
        _srcName.Enabled = _srcConn.Enabled = _srcDataset.Enabled =
            _srcBaq.Enabled = _srcQuery.Enabled = _srcArgGrid.Enabled = en;
        if (_curSrc is null) return;
        foreach (var a in _curSrc.Attributes())
        {
            string n = a.Name.LocalName;
            if (n.StartsWith("param-")) _srcArgGrid.Rows.Add("param", n["param-".Length..], a.Value);
            else if (n.StartsWith("filter-")) _srcArgGrid.Rows.Add("filter", n["filter-".Length..], a.Value);
        }
    }

    private void CommitSource()
    {
        if (_curSrc is null) return;
        _curSrc.SetAttributeValue("name", _srcName.Text.Trim());
        _curSrc.SetAttributeValue("connection", _srcConn.Text.Trim());
        _curSrc.SetAttributeValue("dataset",
            _srcDataset.Text.Trim() is { Length: > 0 } ds ? ds : null);
        _curSrc.SetAttributeValue("baq",
            _srcBaq.Text.Trim() is { Length: > 0 } bq ? bq : null);
        _curSrc.SetAttributeValue("query",
            _srcQuery.Text.Trim() is { Length: > 0 } qy ? qy : null);
        // rebuild the prefixed attribute pairs from the grid
        foreach (var a in _curSrc.Attributes().Where(a =>
                     a.Name.LocalName.StartsWith("param-") ||
                     a.Name.LocalName.StartsWith("filter-")).ToList())
            a.Remove();
        foreach (DataGridViewRow r in _srcArgGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string kind = r.Cells[0].Value?.ToString() ?? "param";
            string nm = r.Cells[1].Value?.ToString()?.Trim() ?? "";
            if (nm == "") continue;
            _curSrc.SetAttributeValue($"{kind}-{nm}", r.Cells[2].Value?.ToString() ?? "");
        }
    }

    // ---------- panel (data-mode presentation) ----------

    private TabPage BuildPanelTab()
    {
        var page = new TabPage("Panel");
        var p = new Panel { Dock = DockStyle.Fill };
        int y = 12;
        void Row(string label, Control c, int extra = 0)
        {
            p.Controls.Add(new Label { Text = label, Left = 10, Top = y + 3, AutoSize = true });
            c.Left = 150; c.Top = y;
            p.Controls.Add(c);
            y += 32 + extra;
        }
        _pnPrint.Items.AddRange(new object[]
            { "dialog (system print dialog)", "direct (straight to printer)" });
        Row("Print behavior", _pnPrint);
        _pnPrinter.Items.AddRange(new object[] { "(machine default)", "(embedded picker)" });
        try
        {
            foreach (string pr in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
                _pnPrinter.Items.Add(pr);
        }
        catch { /* spooler trouble */ }
        Row("Printer (direct)", _pnPrinter);
        _pnCopies.Items.AddRange(new object[]
            { "ask (dialog on batch)", "embedded (on the form)", "fixed" });
        Row("Copies", _pnCopies);
        _pnFixed.Left = 360; _pnFixed.Top = _pnCopies.Top; p.Controls.Add(_pnFixed);
        _pnCopies.SelectedIndexChanged += (_, _) => _pnFixed.Visible = _pnCopies.SelectedIndex == 2;
        _pnCollate.Items.AddRange(new object[]
            { "choose (on the form/dialog)", "grouped (1-1-2-2)", "sequenced (1-2-1-2)",
              "ask (no selector; popup only when it matters)" });
        Row("Collation", _pnCollate);
        var btns = new FlowLayoutPanel { Left = 150, Top = y, Width = 480, Height = 28 };
        btns.Controls.AddRange(new Control[] { _pnBtnPreview, _pnBtnPrint, _pnBtnPrintAll, _pnBtnClear, _pnBtnLog });
        p.Controls.Add(new Label { Text = "Buttons", Left = 10, Top = y + 3, AutoSize = true });
        p.Controls.Add(btns);
        y += 34;
        _pnButtonsAt.Items.AddRange(new object[] { "bottom (after the fields)", "top" });
        Row("Buttons placement", _pnButtonsAt);
        p.Controls.Add(new Label
        {
            Text = "Inputs shown on the data panel (unchecked = panel=\"hide\" — the field still resolves):",
            Left = 10, Top = y + 4, AutoSize = true,
        });
        _pnInputs.Left = 10; _pnInputs.Top = y + 26;
        p.Controls.Add(_pnInputs);
        // reorder: the panel shows inputs in THIS order (etiq:panel order=)
        var up = new Button { Text = "Move Up", Left = 320, Top = y + 26, Width = 90 };
        var down = new Button { Text = "Move Down", Left = 320, Top = y + 56, Width = 90 };
        void Move(int dir)
        {
            int i = _pnInputs.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= _pnInputs.Items.Count) return;
            var item = _pnInputs.Items[i]; bool chk = _pnInputs.GetItemChecked(i);
            var other = _pnInputs.Items[j]; bool ochk = _pnInputs.GetItemChecked(j);
            _pnInputs.Items[j] = item; _pnInputs.SetItemChecked(j, chk);
            _pnInputs.Items[i] = other; _pnInputs.SetItemChecked(i, ochk);
            _pnInputs.SelectedIndex = j;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        p.Controls.Add(up); p.Controls.Add(down);
        page.Controls.Add(p);
        LoadPanel();
        return page;
    }

    private void LoadPanel()
    {
        var el = Result.Element(NS + "panel");
        string? A(string n) => el is null ? null : (string?)el.Attribute(n);
        _pnPrint.SelectedIndex = A("print") == "direct" ? 1 : 0;
        string? printer = A("printer");
        _pnPrinter.Text = printer switch
        {
            null => "(machine default)",
            "embedded" => "(embedded picker)",
            _ => printer,
        };
        string copies = A("copies") ?? "ask";
        _pnCopies.SelectedIndex = copies == "embedded" ? 1 : copies.StartsWith("fixed:") ? 2 : 0;
        if (copies.StartsWith("fixed:") && int.TryParse(copies["fixed:".Length..], out int n) && n > 0)
            { _pnFixed.Value = n; _pnFixed.Visible = true; }
        _pnCollate.SelectedIndex = (A("collate") ?? "choose") switch
            { "grouped" => 1, "sequenced" => 2, "ask" => 3, _ => 0 };
        var btns = (A("buttons") ?? "preview,print,printall,clear")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _pnBtnPreview.Checked = btns.Contains("preview");
        _pnBtnPrint.Checked = btns.Contains("print");
        _pnBtnPrintAll.Checked = btns.Contains("printall");
        _pnBtnClear.Checked = btns.Contains("clear");
        _pnBtnLog.Checked = btns.Contains("log");
        _pnButtonsAt.SelectedIndex = A("buttons-at") == "top" ? 1 : 0;

        _pnInputs.Items.Clear();
        var entries = new List<(string Text, bool Show)>();
        foreach (var f in Result.Elements(NS + "field"))
        {
            string src = (string?)f.Attribute("source") ?? "";
            bool input = src == "prompt" ||
                         (src is ("epicor" or "rest") && (string?)f.Attribute("override") == "true");
            if (!input) continue;
            entries.Add(($"field: {(string?)f.Attribute("name")}",
                (string?)f.Attribute("panel") != "hide"));
        }
        foreach (var l in Result.Elements(NS + "list"))
            entries.Add(($"list: {(string?)l.Attribute("name")}",
                (string?)l.Attribute("panel") != "hide"));
        _pnNaturalOrder = entries.Select(e => e.Text.Replace(": ", ":")).ToList();
        var saved = (A("order") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (saved.Length > 0)
            entries = entries.OrderBy(e =>
            {
                int idx = Array.IndexOf(saved, e.Text.Replace(": ", ":"));
                return idx < 0 ? int.MaxValue : idx;
            }).ToList();
        foreach (var (text, show) in entries)
            _pnInputs.Items.Add(text, show);
    }

    private void CommitPanel()
    {
        // element carries only non-default values; all-defaults = no element
        var el = Result.Element(NS + "panel");
        string? print = _pnPrint.SelectedIndex == 1 ? "direct" : null;
        string? printer = _pnPrinter.Text switch
        {
            "(machine default)" or "" => null,
            "(embedded picker)" => "embedded",
            var t => t.Trim(),
        };
        string? copies = _pnCopies.SelectedIndex switch
        {
            1 => "embedded", 2 => $"fixed:{(int)_pnFixed.Value}", _ => null,
        };
        string? collate = _pnCollate.SelectedIndex switch
            { 1 => "grouped", 2 => "sequenced", 3 => "ask", _ => null };
        var picked = new List<string>();
        if (_pnBtnPreview.Checked) picked.Add("preview");
        if (_pnBtnPrint.Checked) picked.Add("print");
        if (_pnBtnPrintAll.Checked) picked.Add("printall");
        if (_pnBtnClear.Checked) picked.Add("clear");
        if (_pnBtnLog.Checked) picked.Add("log");
        // default set = preview,print,printall,clear (log is opt-in)
        string? buttons = picked.Count == 4 && !_pnBtnLog.Checked
            ? null : string.Join(",", picked);
        string? at = _pnButtonsAt.SelectedIndex == 1 ? "top" : null;
        var tokens = _pnInputs.Items.Cast<string>()
            .Select(t => t.Replace(": ", ":")).ToList();
        string? order = tokens.SequenceEqual(_pnNaturalOrder)
            ? null : string.Join(",", tokens);   // declaration order = no attr

        bool any = print is not null || printer is not null || copies is not null ||
                   collate is not null || buttons is not null || at is not null ||
                   order is not null;
        if (!any) { el?.Remove(); }
        else
        {
            if (el is null) { el = new XElement(NS + "panel"); Result.Add(el); }
            el.SetAttributeValue("print", print);
            el.SetAttributeValue("printer", printer);
            el.SetAttributeValue("copies", copies);
            el.SetAttributeValue("collate", collate);
            el.SetAttributeValue("buttons", buttons);
            el.SetAttributeValue("buttons-at", at);
            el.SetAttributeValue("order", order);
        }

        // input visibility → panel="hide" on the named field/list
        foreach (var item in _pnInputs.Items.Cast<string>()
                     .Select((text, i) => (text, hide: !_pnInputs.GetItemChecked(i))))
        {
            int sep = item.text.IndexOf(": ");
            if (sep < 0) continue;
            string kind = item.text[..sep] == "field" ? "field" : "list";
            string name = item.text[(sep + 2)..];
            var target = Result.Elements(NS + kind)
                .FirstOrDefault(e => (string?)e.Attribute("name") == name);
            target?.SetAttributeValue("panel", item.hide ? "hide" : null);
        }
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
        CommitSource();
        CommitPanel();
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
        _curSrc = null;
        _fieldPane.SetField(null, FieldChoices, () => { });
        var fresh = new XElement(_appliedSnapshot);
        Result.ReplaceAll(fresh.Attributes().Cast<object>().Concat(fresh.Nodes()).ToArray());
        RefreshFieldList();
        RefreshMapList();
        RefreshListList();
        RefreshSourceList();
        LoadSource();
        LoadPanel();
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
