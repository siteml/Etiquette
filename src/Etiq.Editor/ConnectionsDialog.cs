using Etiq.Core;

namespace Etiq.Editor;

/// <summary>
/// File > Connections… — manage this machine's named connections, their
/// datasets, and the machine-default dataset. Secrets typed here are
/// DPAPI-wrapped when the store is saved (they display as "dpapi:…"
/// afterwards; retype to change). Export/Import moves the whole store
/// between machines as a password-protected *.etiqcreds bundle.
/// </summary>
public sealed class ConnectionsDialog : Form
{
    private List<ConnectionDef> _list;

    private readonly ListBox _names = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly DataGridView _settings = NewGrid();
    private readonly ComboBox _dataset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly DataGridView _overrides = NewGrid();
    private readonly ComboBox _defaultDs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _machineDs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };

    private ConnectionDef? _cur;
    private string? _curDataset;
    private bool _loading;

    /// <summary>Keys each connection type understands — pre-seeded so
    /// users see what to fill instead of guessing.</summary>
    private static readonly string[] EpicorKeys =
        { "baseUrl", "company", "apiKey", "username", "password" };
    private static readonly string[] RestKeys =
        { "baseUrl", "kind", "username", "password" };
    private static string[] KeysFor(string type) =>
        type.Equals("rest", StringComparison.OrdinalIgnoreCase) ? RestKeys : EpicorKeys;

    /// <summary>Shown as a cell tooltip so each setting explains itself.</summary>
    private static readonly Dictionary<string, string> KeyHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["baseUrl"] = "epicor: server root, e.g. https://host/EpicorERP (no /api/v2 — added automatically).\nrest: the API base URL",
        ["company"] = "Epicor Company ID (not its display name)",
        ["apiKey"] = "Epicor REST API key (sent as x-api-key). Stored protected.",
        ["username"] = "Service account user (basic auth). Prefer a dedicated account scoped to label BAQs.",
        ["password"] = "Stored protected — shows as dpapi:… after OK; retype to change.",
        ["kind"] = "rest auth kind: none | headers | basic | glpi",
    };

    public ConnectionsDialog()
    {
        Text = "Connections";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Ui.AutoScale(this);
        ClientSize = new Size(760, 470);

        _list = SafeLoad();

        // ----- left: connection list + add/remove -----
        var left = new Panel { Left = 10, Top = 10, Width = 180, Height = 400 };
        _names.Height = 360;
        var add = new Button { Text = "Add", Left = 0, Top = 366, Width = 55 };
        var ren = new Button { Text = "Rename", Left = 60, Top = 366, Width = 62 };
        var del = new Button { Text = "Remove", Left = 127, Top = 366, Width = 53 };
        left.Controls.AddRange(new Control[] { _names, add, ren, del });
        _names.Dock = DockStyle.None; _names.SetBounds(0, 0, 180, 360);
        Controls.Add(left);

        // ----- right: type, base settings, datasets -----
        int rx = 205;
        Controls.Add(new Label { Text = "Type:", Left = rx, Top = 14, AutoSize = true });
        _type.Items.AddRange(new object[] { "epicor", "rest" });
        _type.Left = rx + 55; _type.Top = 10; Controls.Add(_type);
        Controls.Add(new Label
        {
            Text = "Base settings (all datasets share these):",
            Left = rx, Top = 42, AutoSize = true,
        });
        _settings.SetBounds(rx, 62, 545, 150); Controls.Add(_settings);

        Controls.Add(new Label { Text = "Dataset:", Left = rx, Top = 224, AutoSize = true });
        _dataset.Left = rx + 60; _dataset.Top = 220; Controls.Add(_dataset);
        var dsAdd = new Button { Text = "Add", Left = rx + 206, Top = 219, Width = 50 };
        var dsDel = new Button { Text = "Remove", Left = rx + 260, Top = 219, Width = 62 };
        Controls.Add(dsAdd); Controls.Add(dsDel);
        Controls.Add(new Label { Text = "Default:", Left = rx + 335, Top = 224, AutoSize = true });
        _defaultDs.Left = rx + 392; _defaultDs.Top = 220; _defaultDs.Width = 150;
        Controls.Add(_defaultDs);
        Controls.Add(new Label
        {
            Text = "Dataset overrides (e.g. epicor: baseUrl of the pilot instance):",
            Left = rx, Top = 250, AutoSize = true,
        });
        _overrides.SetBounds(rx, 270, 545, 140); Controls.Add(_overrides);

        // ----- bottom row -----
        Controls.Add(new Label { Text = "Machine default dataset:", Left = 10, Top = 432, AutoSize = true });
        _machineDs.Left = 155; _machineDs.Top = 428; Controls.Add(_machineDs);
        var test = new Button { Text = "Test", Left = 330, Top = 427, Width = 60 };
        test.Click += async (_, _) => await TestCurrentAsync(test);
        Controls.Add(test);
        var export = new Button { Text = "Export…", Left = 395, Top = 427, Width = 75 };
        var import = new Button { Text = "Import…", Left = 475, Top = 427, Width = 75 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 590, Top = 427, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 672, Top = 427, Width = 75 };
        Controls.AddRange(new Control[] { export, import, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;

        // ----- behavior -----
        _names.SelectedIndexChanged += (_, _) => { CommitCurrent(); ShowConnection(); };
        add.Click += (_, _) =>
        {
            string? nm = Prompts.PromptText(this, "New connection name", "");
            if (string.IsNullOrWhiteSpace(nm)) return;
            if (_list.Any(c => c.Name.Equals(nm, StringComparison.OrdinalIgnoreCase)))
                { MessageBox.Show(this, $"'{nm}' already exists.", "Connections"); return; }
            var c = new ConnectionDef { Name = nm.Trim(), Type = "epicor" };
            foreach (var k in EpicorKeys) c.Settings[k] = "";
            _list.Add(c);
            ReloadNames(select: nm.Trim());
        };
        ren.Click += (_, _) =>
        {
            if (_cur is null) return;
            string? nm = Prompts.PromptText(this, "Rename connection", _cur.Name);
            if (string.IsNullOrWhiteSpace(nm) || nm == _cur.Name) return;
            if (_list.Any(c => c != _cur && c.Name.Equals(nm, StringComparison.OrdinalIgnoreCase)))
                { MessageBox.Show(this, $"'{nm}' already exists.", "Connections"); return; }
            _cur.Name = nm.Trim();
            ReloadNames(select: _cur.Name);
        };
        del.Click += (_, _) =>
        {
            if (_cur is null) return;
            if (MessageBox.Show(this, $"Remove connection '{_cur.Name}' and its credentials?",
                    "Connections", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _list.Remove(_cur);
            _cur = null;
            ReloadNames();
        };
        _type.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cur is null) return;
            CommitCurrent();
            _cur.Type = _type.Text;
            ShowConnection();   // re-seed the keys the new type expects
        };
        _dataset.SelectedIndexChanged += (_, _) => { CommitOverrides(); ShowDataset(); };
        dsAdd.Click += (_, _) =>
        {
            if (_cur is null) return;
            string? nm = Prompts.PromptText(this, "New dataset name (e.g. production, pilot)", "");
            if (string.IsNullOrWhiteSpace(nm)) return;
            if (_cur.Datasets.ContainsKey(nm))
                { MessageBox.Show(this, $"'{nm}' already exists.", "Connections"); return; }
            CommitOverrides();
            _cur.Datasets[nm.Trim()] = new(StringComparer.OrdinalIgnoreCase);
            _cur.DefaultDataset ??= nm.Trim();   // first dataset becomes the default
            ReloadDatasets(select: nm.Trim());
        };
        dsDel.Click += (_, _) =>
        {
            if (_cur is null || _curDataset is null) return;
            _cur.Datasets.Remove(_curDataset);
            if (_cur.DefaultDataset == _curDataset)
                _cur.DefaultDataset = _cur.Datasets.Keys.FirstOrDefault();
            _curDataset = null;
            ReloadDatasets();
        };
        _defaultDs.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cur is null) return;
            _cur.DefaultDataset = _defaultDs.SelectedIndex <= 0 ? null : _defaultDs.Text;
        };
        export.Click += (_, _) => Export();
        import.Click += (_, _) => Import();
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            CommitCurrent();
            try
            {
                ConnectionsStore.Save(MainForm.ConnectionsPath, _list);
                UpdateChecker.SetSetting("dataset",
                    _machineDs.SelectedIndex <= 0 ? null : _machineDs.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Saving connections failed");
                e.Cancel = true;
            }
        };

        ReloadNames();
        ReloadMachineDatasets();
    }

    /// <summary>Round-trip check against the machine the CURRENT grid
    /// values describe (uncommitted edits included), using the connection's
    /// default dataset. epicor: GET the OData service root with the same
    /// auth the BAQ fetches use; rest: GET the base URL.</summary>
    private async Task TestCurrentAsync(Button btn)
    {
        if (_cur is null) return;
        CommitCurrent();
        btn.Enabled = false; btn.Text = "…";
        try
        {
            string report;
            try
            {
                var st = _cur.Resolved(_cur.DefaultDataset);
                using var http = new System.Net.Http.HttpClient
                    { Timeout = TimeSpan.FromSeconds(10) };
                string url;
                if (_cur.Type.Equals("epicor", StringComparison.OrdinalIgnoreCase))
                {
                    string b = st.GetValueOrDefault("baseUrl", "").TrimEnd('/');
                    url = $"{b}/api/v2/odata/{Uri.EscapeDataString(st.GetValueOrDefault("company", ""))}";
                    if (st.GetValueOrDefault("apiKey", "") is { Length: > 0 } key)
                        http.DefaultRequestHeaders.Add("x-api-key", key);
                    if (st.GetValueOrDefault("username", "") is { Length: > 0 } u)
                        http.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                    $"{u}:{st.GetValueOrDefault("password", "")}")));
                }
                else
                {
                    url = st.GetValueOrDefault("baseUrl", "");
                }
                if (url is "" or "/") { report = "baseUrl is empty."; }
                else
                {
                    using var resp = await http.GetAsync(url);
                    report = (int)resp.StatusCode switch
                    {
                        200 => "OK — connected and authenticated.",
                        401 => "Reached the server, but authentication FAILED (401) — check username/password/apiKey.",
                        403 => "Authenticated but FORBIDDEN (403) — check the account's access scope.",
                        404 => "Server reached but path not found (404) — check baseUrl/company.",
                        var c => $"Server answered HTTP {c}.",
                    };
                }
            }
            catch (Exception ex) { report = "Failed: " + ex.Message; }
            MessageBox.Show(this, report, $"Test — {_cur.Name}");
        }
        finally { btn.Enabled = true; btn.Text = "Test"; }
    }

    private static List<ConnectionDef> SafeLoad()
    {
        try { return ConnectionsStore.Load(MainForm.ConnectionsPath); }
        catch { return new(); }
    }

    private static DataGridView NewGrid()
    {
        var g = new DataGridView
        {
            AllowUserToResizeRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        g.Columns.Add("k", "Setting");
        g.Columns.Add("v", "Value");
        g.Columns[0].FillWeight = 30;
        return g;
    }

    // ---------- load/commit ----------

    private void ReloadNames(string? select = null)
    {
        _names.Items.Clear();
        foreach (var c in _list) _names.Items.Add(c.Name);
        if (select is not null) _names.SelectedItem = select;
        else if (_names.Items.Count > 0) _names.SelectedIndex = 0;
        else ShowConnection();
        ReloadMachineDatasets();
    }

    private void ShowConnection()
    {
        _loading = true;
        _cur = _names.SelectedItem is string nm
            ? _list.FirstOrDefault(c => c.Name == nm) : null;
        _type.SelectedItem = _cur?.Type ?? "epicor";
        _settings.Rows.Clear();
        bool en = _cur is not null;
        _type.Enabled = _settings.Enabled = _dataset.Enabled =
            _overrides.Enabled = _defaultDs.Enabled = en;
        if (_cur is not null)
        {
            // seed the expected keys for the type so blanks are visible
            foreach (var k in KeysFor(_cur.Type))
                if (!_cur.Settings.ContainsKey(k)) _cur.Settings[k] = "";
            foreach (var (k, v) in _cur.Settings)
            {
                int i = _settings.Rows.Add(k, v);
                if (KeyHints.TryGetValue(k, out var hint))
                {
                    _settings.Rows[i].Cells[0].ToolTipText = hint;
                    _settings.Rows[i].Cells[1].ToolTipText = hint;
                }
            }
        }
        _loading = false;
        _curDataset = null;
        ReloadDatasets();
    }

    private void ReloadDatasets(string? select = null)
    {
        _loading = true;
        _dataset.Items.Clear();
        _defaultDs.Items.Clear();
        _defaultDs.Items.Add("(none)");
        if (_cur is not null)
            foreach (var ds in _cur.Datasets.Keys)
            {
                _dataset.Items.Add(ds);
                _defaultDs.Items.Add(ds);
            }
        _defaultDs.SelectedIndex = _cur?.DefaultDataset is { } d && _defaultDs.Items.Contains(d)
            ? _defaultDs.Items.IndexOf(d) : 0;
        _loading = false;
        if (select is not null) _dataset.SelectedItem = select;
        else if (_dataset.Items.Count > 0) _dataset.SelectedIndex = 0;
        else { _curDataset = null; ShowDataset(); }
        ReloadMachineDatasets();
    }

    private void ShowDataset()
    {
        _overrides.Rows.Clear();
        _curDataset = _dataset.SelectedItem as string;
        if (_cur is null || _curDataset is null ||
            !_cur.Datasets.TryGetValue(_curDataset, out var over)) return;
        foreach (var (k, v) in over) _overrides.Rows.Add(k, v);
    }

    private void ReloadMachineDatasets()
    {
        string prev = _machineDs.SelectedIndex <= 0 ? "" : _machineDs.Text;
        _machineDs.Items.Clear();
        _machineDs.Items.Add("(per-connection default)");
        foreach (var c in _list)
            foreach (var ds in c.Datasets.Keys)
                if (!_machineDs.Items.Contains(ds)) _machineDs.Items.Add(ds);
        string? saved = prev != "" ? prev : UpdateChecker.GetSetting("dataset");
        _machineDs.SelectedIndex = saved is not null && _machineDs.Items.Contains(saved)
            ? _machineDs.Items.IndexOf(saved) : 0;
    }

    /// <summary>Grid → model for the selected connection (and dataset).</summary>
    private void CommitCurrent()
    {
        if (_cur is null) return;
        _cur.Settings = GridToDict(_settings);
        CommitOverrides();
    }

    private void CommitOverrides()
    {
        if (_cur is null || _curDataset is null) return;
        if (_cur.Datasets.ContainsKey(_curDataset))
            _cur.Datasets[_curDataset] = GridToDict(_overrides);
    }

    private static Dictionary<string, string> GridToDict(DataGridView g)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in g.Rows)
        {
            if (r.IsNewRow) continue;
            string k = r.Cells[0].Value?.ToString()?.Trim() ?? "";
            if (k == "") continue;
            d[k] = r.Cells[1].Value?.ToString() ?? "";
        }
        return d;
    }

    // ---------- bundle export/import ----------

    private static string? AskPassword(IWin32Window? owner, string title, bool confirm)
    {
        using var f = new Form
        {
            Text = title, ClientSize = new Size(360, confirm ? 156 : 100),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var l1 = new Label { Text = "Password:", Left = 12, Top = 12, AutoSize = true };
        var p1 = new TextBox { Left = 12, Top = 32, Width = 336, UseSystemPasswordChar = true };
        var l2 = new Label { Text = "Confirm password:", Left = 12, Top = 62, AutoSize = true };
        var p2 = new TextBox { Left = 12, Top = 82, Width = 336, UseSystemPasswordChar = true };
        l2.Visible = p2.Visible = confirm;
        f.Controls.Add(l1); f.Controls.Add(l2);
        int by = confirm ? 116 : 60;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = by, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Top = by, Width = 75 };
        f.Controls.AddRange(new Control[] { p1, p2, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        while ((owner is null ? f.ShowDialog() : f.ShowDialog(owner)) == DialogResult.OK)
        {
            if (p1.Text == "") { MessageBox.Show("Password cannot be empty.", title); continue; }
            if (confirm && p1.Text != p2.Text)
                { MessageBox.Show("Passwords do not match.", title); continue; }
            return p1.Text;
        }
        return null;
    }

    /// <summary>Headless-ish import for `etiqedit --import-connections
    /// <file>`: password prompt, decrypt, merge into the machine store,
    /// save (machine-wrapping secrets), report. Returns success.</summary>
    public static bool ImportInteractive(string path)
    {
        if (!File.Exists(path))
            { MessageBox.Show($"File not found: {path}", "Import connections"); return false; }
        string? pw = AskPassword(null, "Bundle password", confirm: false);
        if (pw is null) return false;
        try
        {
            var imported = ImportBundle(path, pw);
            var list = SafeLoad();
            foreach (var c in imported)
            {
                list.RemoveAll(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(c);
            }
            ConnectionsStore.Save(MainForm.ConnectionsPath, list);
            MessageBox.Show($"Imported {imported.Count} connection(s) for this machine.",
                "Import connections");
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show("Wrong password (or damaged bundle).", "Import connections");
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import connections");
            return false;
        }
    }

    private void Export()
    {
        CommitCurrent();
        if (_list.Count == 0) { MessageBox.Show(this, "Nothing to export.", "Export"); return; }
        using var dlg = new SaveFileDialog
            { Filter = "Etiquette connections bundle (*.etiqcreds)|*.etiqcreds", FileName = "connections.etiqcreds" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? pw = AskPassword(this, "Bundle password (share it out-of-band)", confirm: true);
        if (pw is null) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, CredsBundle.Export(_list, pw));
            MessageBox.Show(this, "Bundle exported. Deliver the password separately " +
                "(never alongside the file).", "Export");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }

    private void Import()
    {
        using var dlg = new OpenFileDialog
            { Filter = "Etiquette connections bundle (*.etiqcreds)|*.etiqcreds|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? pw = AskPassword(this, "Bundle password", confirm: false);
        if (pw is null) return;
        try
        {
            var imported = ImportBundle(dlg.FileName, pw);
            // replace-by-name merge: bundle wins for names it carries
            foreach (var c in imported)
            {
                _list.RemoveAll(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                _list.Add(c);
            }
            _cur = null;
            ReloadNames();
            MessageBox.Show(this, $"Imported {imported.Count} connection(s). " +
                "OK saves them protected for this machine.", "Import");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, "Wrong password (or damaged bundle).", "Import failed");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed"); }
    }

    /// <summary>Shared with Program.cs --import-connections: decrypt a
    /// bundle file and return its connections (secrets plaintext — caller
    /// saves via ConnectionsStore.Save, which machine-wraps them).</summary>
    public static List<ConnectionDef> ImportBundle(string path, string password) =>
        CredsBundle.Import(File.ReadAllBytes(path), password);
}
