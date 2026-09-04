using Etiq.Core;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>
/// Editor shell v1. Layout: outline tree (layers → objects) | canvas |
/// right panel (Design mode: PropertyGrid; Data mode: prompt entry +
/// preview + print stub). Design/Data mode is the accidental-edit guard:
/// Data mode locks all layout interaction and shows resolved values.
/// </summary>
public sealed class MainForm : Form
{
    private readonly CanvasControl _canvas = new() { Dock = DockStyle.Fill };
    private readonly TreeView _outline = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly InspectorPanel _props = new() { Dock = DockStyle.Fill };
    private readonly Panel _dataPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusPos = new("x: -, y: -");
    private readonly ToolStripStatusLabel _statusDoc = new("no document");
    private readonly ToolStripButton _modeButton = new("Mode: DESIGN") { CheckOnClick = true };

    private EditorDoc? _doc;
    // print-station lock (--station <file>): the editor opens straight in
    // Data mode with the design surface locked until explicitly unlocked
    private bool _stationLocked;
    private ToolStripMenuItem? _viewDesignItem, _viewDataItem;
    private MenuStrip? _menuStrip;
    private ToolStrip? _toolStrip;
    private readonly Dictionary<string, TextBox> _promptBoxes = new();
    private readonly Dictionary<string, ComboBox> _listCombos = new();
    // list row picker: display string ("key — Name") → key value, per list
    private readonly Dictionary<string, Dictionary<string, string>> _listDisplayToKey = new();
    private readonly Dictionary<string, string?> _listFilterVal = new(); // last applied filter
    private Label? _dataStatus;                       // inline resolve status
    private NumericUpDown? _panelCopies;              // etiq:panel copies="embedded"
    private ComboBox? _panelCollate;
    private CheckBox? _panelPrinterDefault;           // etiq:panel printer="embedded"
    private ComboBox? _panelPrinterBox;
    private System.Windows.Forms.Timer? _previewTimer; // debounced auto-preview

    // ---------- remote sources (etiq:source) ----------
    // machine connection store + session dataset override + one-row-per-
    // source cache (keyed by source name + resolved param signature so a
    // changed prompt re-fetches but keystroke-debounced previews don't
    // hammer the service)
    private ToolStripComboBox? _datasetCombo;
    private string? _sessionDataset;                  // null = machine default
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, Dictionary<string, System.Text.Json.JsonElement>> _sourceRows = new();
    // failed fetches, by the same signature: WITHOUT this every debounce
    // tick retried a failing call synchronously on the UI thread (up to the
    // HTTP timeout each time) — the app appeared frozen. A failure is only
    // retried when the inputs change (new signature) or the user forces it
    // (Refresh Preview / Clear / dataset change).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sourceFails = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _fetchingSources = new();   // cross-source cycle guard
    // query-fed pick lists (etiq:list from=): fetched row SETS by query
    // signature (query + dataset + resolved target/params/filters), the
    // rows each list currently shows, and the signature they came from
    private readonly Dictionary<string, List<Dictionary<string, string>>> _listRowSets = new();
    private readonly Dictionary<string, string> _listRowFails = new();
    private readonly HashSet<string> _listRowFetching = new();
    private readonly Dictionary<string, string?> _listRowSig = new();
    private readonly Dictionary<string, List<Dictionary<string, string>>> _listRowsLive = new();
    // last word about each query-fed list (loading / failed / N rows, M skipped):
    // shown AHEAD of a resolve error, which would otherwise hide it
    private readonly Dictionary<string, (string Text, bool Error)> _listNotes = new();

    internal static string ConnectionsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Etiquette", "connections.json");

    /// <summary>Dataset used when nothing pins one: session picker wins,
    /// then the machine default (settings.json "dataset"), then each
    /// connection's own default (null).</summary>
    private string? ActiveDataset =>
        _sessionDataset ?? UpdateChecker.GetSetting("dataset");

    public MainForm(string? openPath) : this(openPath, station: false) { }

    public MainForm(string? openPath, bool station)
    {
        Text = "Etiquette Designer";
        Ui.AutoScale(this);
        Width = 1280; Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // print log: on by default, %APPDATA%\Etiquette\logs; settings
        // printLog=off disables, printLogDir relocates (e.g. a UNC share so
        // stations log centrally)
        PrintLog.Directory = UpdateChecker.GetSetting("printLog") == "off" ? null
            : UpdateChecker.GetSetting("printLogDir") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Etiquette", "logs");

        // window/taskbar icon = the exe's own icon (assets/etiq.ico via
        // <ApplicationIcon>); never fatal if extraction fails
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* default icon */ }

        // Dock layout runs in REVERSE Controls order: the Fill split must
        // be added first and the MenuStrip last, or the menu overlaps the
        // top of the panels (and renders under the toolbar).
        BuildLayout();
        BuildMenu();
        UpdateTitle();

        // this machine's persisted station role wins over any argument;
        // --station <file> is the transient (this-run-only) variant
        string? stationFile = UpdateChecker.GetSetting(StationFileKey);
        if (stationFile is not null)
        {
            if (File.Exists(stationFile)) OpenFile(stationFile);
            // LOCK REGARDLESS of whether the template loaded: a machine
            // designated as a print station must never silently fall open
            // into the full editor because the file went missing — the
            // station view shows what's wrong instead, and the only way
            // out stays Ctrl+Shift+F12 + UNLOCK.
            if (_doc is null)
                Shown += (_, _) => MessageBox.Show(this,
                    "This machine is set up as a print station, but its template " +
                    $"could not be loaded:\n\n    {stationFile}\n\n" +
                    "Restore the file and restart — or press Ctrl+Shift+F12 and type " +
                    "UNLOCK to leave print-station mode.",
                    "Print Station", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Shown += (_, _) => StationLock();
        }
        else
        {
            if (openPath is not null && File.Exists(openPath)) OpenFile(openPath);
            if (station && _doc is not null) Shown += (_, _) => StationLock();
        }

        // silent startup update check, only when the build ships a repo
        // and the user hasn't turned it off (Help → Options)
        Shown += async (_, _) =>
        {
            // NEVER on a print station: an operator can't judge an update
            // prompt, and a station values stability over currency. (The
            // station Shown handler runs first, so the lock is already
            // set.) Updating a station = exit station mode, update
            // manually, re-enter. A pinning mechanism (update server /
            // version file) for production fleets is a planned follow-up.
            if (_stationLocked) return;
            if (UpdateChecker.Configured && UpdateChecker.AutoCheck)
                await CheckForUpdates(interactive: false);
        };

        // never lose work silently: closing with unsaved changes prompts
        // (station mode can't edit, so it never triggers this)
        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
    }

    /// <summary>Pick + offer the right download for an available update.
    /// Preference (settings.json updateFlavor): "standalone"/"framework"
    /// pin a flavor; "auto" matches the running build — EXCEPT a standalone
    /// install on a machine that has the .NET 8 Desktop Runtime, which is
    /// offered the much lighter framework-dependent build (with a
    /// remember-my-choice option feeding the setting). When the install
    /// folder is writable, the update is downloaded and applied IN PLACE
    /// (UpdateApplier) and etiqedit restarts on the new version; when it
    /// isn't (Program Files without elevation), falls back to a plain
    /// browser download.</summary>
    private async Task OfferUpdate(UpdateChecker.Release rel)
    {
        // the update-available window shows the rendered CHANGELOG.md with
        // Install / Skip this version / Later — the changelog IS the pitch
        string? changelog = await UpdateChecker.FetchChangelogAsync();
        switch (UpdateDialogs.ShowChangelog(this, rel.Tag,
                    UpdateChecker.Current.ToString(3), changelog))
        {
            case UpdateChoice.SkipVersion:
                UpdateChecker.SkipVersion = rel.Version.ToString(3);
                return;
            case UpdateChoice.Later:
                return;
        }

        string pref = UpdateChecker.UpdateFlavor;
        bool wantStandalone;
        if (pref == "standalone") wantStandalone = true;
        else if (pref == "framework") wantStandalone = false;
        else if (UpdateChecker.IsSelfContained &&
                 UpdateChecker.DesktopRuntimeInstalled() && rel.FrameworkUrl is not null)
        {
            // standalone install, but the light build would work here: ask
            var pick = AskUpdateFlavor(rel.Tag);
            if (pick is null) return;   // cancelled
            (wantStandalone, bool remember) = pick.Value;
            if (remember)
                UpdateChecker.UpdateFlavor = wantStandalone ? "standalone" : "framework";
        }
        else wantStandalone = UpdateChecker.IsSelfContained;

        await InstallAssetAsync(rel,
            wantStandalone ? rel.StandaloneUrl : rel.FrameworkUrl,
            wantStandalone ? rel.StandaloneName : rel.FrameworkName);
    }

    /// <summary>Same-version PACKAGE TYPE swap (standalone ↔ framework-
    /// dependent), driven by the Options preference. Framework-dependent
    /// needs the .NET 8 Desktop Runtime — checked BEFORE offering, since a
    /// swapped install that can't start is a bricked station.</summary>
    private async Task OfferFlavorSwitch(UpdateChecker.Release rel, bool toStandalone)
    {
        if (!toStandalone && !UpdateChecker.DesktopRuntimeInstalled())
        {
            MessageBox.Show(this,
                "Your update preference is the framework-dependent package, but the " +
                ".NET 8 Desktop Runtime was not found on this machine.\n\n" +
                "Install the runtime first (aka.ms/dotnet — \"Desktop Runtime\"), " +
                "or set Help > Options > Update download back to standalone/auto.",
                "Switch package type");
            return;
        }
        string? assetUrl = toStandalone ? rel.StandaloneUrl : rel.FrameworkUrl;
        string? assetName = toStandalone ? rel.StandaloneName : rel.FrameworkName;
        if (assetUrl is null)
        {
            MessageBox.Show(this,
                $"Release {rel.Tag} has no {(toStandalone ? "standalone" : "framework-dependent")} asset to switch to.",
                "Switch package type");
            return;
        }
        string cur = UpdateChecker.IsSelfContained ? "standalone" : "framework-dependent";
        string tgt = toStandalone ? "standalone" : "framework-dependent";
        if (MessageBox.Show(this,
                $"You're running the {cur} package of v{UpdateChecker.Current.ToString(3)}, " +
                $"but your update preference is {tgt}.\n\n" +
                $"Switch to the {tgt} package now (same version)?\n\n" +
                "(Choosing No leaves everything as is — set Help > Options > Update " +
                "download to \"auto\" if you don't want this offer.)",
                "Switch package type", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        await InstallAssetAsync(rel, assetUrl, assetName);
    }

    /// <summary>Shared install tail: in-place when possible, browser
    /// download otherwise.</summary>
    private async Task InstallAssetAsync(UpdateChecker.Release rel,
                                         string? assetUrl, string? assetName)
    {
        // in-place install needs a direct asset AND a writable install
        // folder; otherwise it's a browser download like before
        // "Install now" was already confirmed on the changelog dialog
        bool inPlace = assetUrl is not null && UpdateApplier.CanSelfUpdate;
        if (!inPlace)
        {
            MessageBox.Show(this, assetUrl is not null
                    ? "The install folder isn't writable, so the update can't be applied automatically — opening the download instead."
                    : "No direct download asset was found — opening the release page.",
                "Update");
            string target = assetUrl ?? rel.Url;
            if (target != "")
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            return;
        }
        try
        {
            if (await UpdateApplier.DownloadAndInstallAsync(this, assetUrl!, assetName ?? "update"))
                UpdateApplier.RelaunchAndExit(this, _doc?.Path);
        }
        catch (Exception ex)
        {
            if (MessageBox.Show(this,
                    $"The update could not be installed: {ex.Message}\n\nOpen the download page instead?",
                    "Update failed", MessageBoxButtons.YesNo) == DialogResult.Yes &&
                rel.Url != "")
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(rel.Url) { UseShellExecute = true });
        }
    }

    /// <summary>Standalone-vs-light chooser: radios + "remember my choice".
    /// Returns null on cancel, else (standalone?, remember?).</summary>
    private (bool Standalone, bool Remember)? AskUpdateFlavor(string tag)
    {
        using var f = new Form
        {
            Text = $"Update to {tag}", ClientSize = new Size(380, 168),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var lbl = new Label
        {
            Text = "This machine has the .NET 8 Desktop Runtime installed, so the " +
                   "much smaller framework-dependent build will also run here.",
            Left = 12, Top = 10, Width = 356, Height = 34,
        };
        var lite = new RadioButton
            { Text = "Switch to the lighter build (recommended)", Left = 12, Top = 50, Width = 356, Checked = true };
        var full = new RadioButton
            { Text = "Keep the standalone build (no dependencies)", Left = 12, Top = 74, Width = 356 };
        var remember = new CheckBox
            { Text = "Remember my choice (stop asking)", Left = 12, Top = 100, Width = 356 };
        var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK, Left = 200, Top = 130, Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 288, Top = 130, Width = 82 };
        f.Controls.AddRange(new Control[] { lbl, lite, full, remember, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return null;
        return (full.Checked, remember.Checked);
    }

    /// <summary>Check GitHub releases against the repo baked into the
    /// build. Interactive mode reports not-configured / up-to-date /
    /// no-releases / errors; silent mode (startup) only speaks when a
    /// newer version exists.</summary>
    private async Task CheckForUpdates(bool interactive)
    {
        if (!UpdateChecker.Configured)
        {
            if (interactive)
                MessageBox.Show(this,
                    "The update source is not set in this build.",
                    "Check for Updates");
            return;
        }
        try
        {
            var rel = await UpdateChecker.FetchLatestAsync();
            if (rel is null)
            {
                if (interactive)
                    MessageBox.Show(this, $"No releases found on {UpdateChecker.Repo} yet.", "Check for Updates");
                return;
            }
            if (rel.Version > UpdateChecker.Current)
            {
                // "skip this version" quiets the STARTUP check for exactly
                // that release; an explicit menu check always shows it,
                // and any newer release shows again automatically
                if (!interactive &&
                    UpdateChecker.SkipVersion == rel.Version.ToString(3))
                    return;
                await OfferUpdate(rel);
            }
            else
            {
                // same version, but the PREFERRED package type differs from
                // the running one (Help > Options changed after install):
                // offer swapping to the alternate package of this release
                bool runningStandalone = UpdateChecker.IsSelfContained;
                string pref = UpdateChecker.UpdateFlavor;
                bool wantStandalone = pref == "standalone" ||
                                      (pref != "framework" && runningStandalone);
                if (rel.Version == UpdateChecker.Current && wantStandalone != runningStandalone)
                {
                    await OfferFlavorSwitch(rel, wantStandalone);
                    return;
                }
                if (interactive)
                    MessageBox.Show(this,
                        $"You're up to date — v{UpdateChecker.Current.ToString(3)} (latest release: {rel.Tag}).",
                        "Check for Updates");
            }
        }
        catch (Exception ex)
        {
            // silent startup checks never nag about network problems
            if (interactive)
                MessageBox.Show(this, $"Update check failed: {ex.Message}", "Check for Updates");
        }
    }

    // ---------- layout ----------

    private SplitContainer? _split1, _split2;

    private void BuildLayout()
    {
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_props);

        // NOTE: never set Panel1MinSize/Panel2MinSize/SplitterDistance in
        // the initializer — a SplitContainer's default size is ~200px and a
        // MinSize larger than that THROWS in the constructor (silent exit
        // for a WinExe). All sizing happens in Shown, after real layout.
        _split2 = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
        };
        _split2.Panel1.Controls.Add(_canvas);
        _split2.Panel2.Controls.Add(right);

        _split1 = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
        };
        // the LEFT pane is shared: outline in Design mode, data entry in
        // Data mode (labelprint's layout — operators work on the left,
        // label preview fills the rest)
        _split1.Panel1.Controls.Add(_outline);
        _split1.Panel1.Controls.Add(_dataPanel);
        _split1.Panel2.Controls.Add(_split2);

        Controls.Add(_split1);

        Shown += (_, _) =>
        {
            try
            {
                float uiF = Ui.Factor(this);   // splitter panes hold text: scale with it
                _split1.Panel1MinSize = (int)(180 * uiF);
                _split1.SplitterDistance = (int)(260 * uiF);        // outline tree
                _split2.Panel2MinSize = (int)(260 * uiF);
                _split2.SplitterDistance = Math.Max((int)(300 * uiF), _split2.Width - (int)(340 * uiF)); // property panel
            }
            catch (ArgumentOutOfRangeException) { /* tiny window: keep defaults */ }
            _canvas.FitToWindow();
        };

        var tools = _toolStrip = new ToolStrip();
        _modeButton.CheckedChanged += (_, _) => SetMode(_modeButton.Checked ? EditorMode.Data : EditorMode.Design);
        tools.Items.Add(_modeButton);
        // session dataset picker: which dataset (Epicor environment /
        // database / ...) declared sources read from, FOR THIS SESSION.
        // Loud when overriding: amber background. Station mode hides the
        // toolbar entirely, so a station stays pinned to the machine default.
        _datasetCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        _datasetCombo.DropDown += (_, _) => PopulateDatasetCombo();
        _datasetCombo.SelectedIndexChanged += (_, _) =>
        {
            string? pick = _datasetCombo.SelectedIndex <= 0
                ? null : _datasetCombo.SelectedItem?.ToString();
            if (pick == _sessionDataset) return;
            _sessionDataset = pick;
            _datasetCombo.BackColor = pick is null ? SystemColors.Window : Color.Gold;
            _sourceRows.Clear(); _sourceFails.Clear(); _listRowSets.Clear(); _listRowFails.Clear(); _listRowSig.Clear();               // never mix rows across datasets
            if (_modeButton.Checked && _doc is not null)
                BuildDataPanel();
        };
        PopulateDatasetCombo();
        tools.Items.Add(_datasetCombo);
        tools.Items.Add(new ToolStripSeparator());
        var fit = new ToolStripButton("Fit")
        {
            ToolTipText = "Zoom the label to fit the window — Ctrl+0 or double middle-click " +
                          "(mouse wheel over the canvas = zoom, middle-drag = pan)",
        };
        fit.Click += (_, _) => _canvas.FitToWindow();
        tools.Items.Add(fit);
        tools.Items.Add(new ToolStripSeparator());
        var tbText = new ToolStripButton("+Text");
        tbText.Click += (_, _) => InsertObject("text");
        tools.Items.Add(tbText);
        var tbBc = new ToolStripButton("+Barcode");
        tbBc.Click += (_, _) => InsertObject("barcode", "code128");
        tools.Items.Add(tbBc);
        var tbLine = new ToolStripButton("+Line");
        tbLine.Click += (_, _) => InsertObject("line");
        tools.Items.Add(tbLine);
        var tbBox = new ToolStripButton("+Box");
        tbBox.Click += (_, _) => InsertObject("box");
        tools.Items.Add(tbBox);
        Controls.Add(tools);

        _status.Items.Add(_statusDoc);
        _status.Items.Add(new ToolStripStatusLabel { Spring = true });
        _status.Items.Add(_statusPos);
        Controls.Add(_status);

        _canvas.SelectionChanged += o =>
        {
            _props.ShowSelection(_doc, o is null ? Array.Empty<EditorObject>() : _canvas.Selection);
            if (_canvas.Selection.Count == 1) SyncOutlineSelection(o);
            else SyncOutlineToGroup();
            UpdateStatusInfo();
        };
        _canvas.CursorWorldMoved += p =>
            _statusPos.Text = $"x: {p.X:0} mils  y: {p.Y:0} mils";
        _outline.AfterSelect += (_, e) =>
        {
            if (_syncingOutline) return; // we moved the highlight, not the user
            // Data mode locks layout interaction: an outline click must
            // never re-summon the design selection machinery (belt and
            // braces — SetMode also disables the tree)
            if (_canvas.Mode != EditorMode.Design) return;
            if (e.Node?.Tag is System.Xml.Linq.XElement grp)
            {
                // a Group node selects the WHOLE group
                if (grp.Parent is null) { RefreshOutline(); return; }
                _canvas.SelectMany(grp.Descendants()
                    .Where(EditorObject.IsEditable).Select(EditorObject.Wrap));
                return;
            }
            if (e.Node?.Tag is not EditorObject o) return;
            // a node can go stale between mutations (deleted object whose
            // element is detached from the document) - never select a ghost
            if (o.El.Parent is null) { RefreshOutline(); return; }
            _canvas.Select(o);
        };
        _outline.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _outline.SelectedNode = e.Node;
            if (e.Node?.Tag is EditorLayer layer) ShowLayerMenu(layer, e.Location);
            else if (e.Node?.Tag is System.Xml.Linq.XElement grp && grp.Parent is not null)
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Rename Group…", null, (_, _) =>
                {
                    if (_doc is null) return;
                    string? nm = PromptText("Rename group",
                        (string?)grp.Attribute("data-name") ?? "");
                    if (nm is null) return;
                    _doc.Undo.Push(EditCommand.SetAttr(
                        grp, "data-name", nm == "" ? null : nm, "rename group"));
                    RefreshOutline();
                });
                menu.Items.Add("Ungroup", null, (_, _) =>
                {
                    _doc?.Ungroup(grp);
                    RefreshOutline();
                });
                menu.Show(_outline, e.Location);
            }
        };
        // outline refresh rides on Undo.Changed → OutlineMaybeRefresh (signature-
        // guarded); an unconditional RefreshOutline here rebuilt the whole tree
        // on every inspector commit — extra flicker for nothing
        _props.Changed += () => _canvas.Invalidate();
    }

    private void BuildMenu()
    {
        var menu = _menuStrip = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&New…", null, (_, _) => FileNew()).ShortcutKeys(Keys.Control | Keys.N);
        file.DropDownItems.Add("&Open…", null, (_, _) => OpenDialog()).ShortcutKeys(Keys.Control | Keys.O);
        file.DropDownItems.Add("Label Si&ze…", null, (_, _) => LabelSize());
        var recent = new ToolStripMenuItem("Open &Recent");
        file.DropDownItems.Add(recent);
        var miClose = (ToolStripMenuItem)file.DropDownItems.Add("&Close", null, (_, _) => CloseFile());
        miClose.ShortcutKeys(Keys.Control | Keys.W);
        var miSave = (ToolStripMenuItem)file.DropDownItems.Add("&Save", null, (_, _) => SaveFile(null));
        miSave.ShortcutKeys(Keys.Control | Keys.S);
        var miSaveAs = (ToolStripMenuItem)file.DropDownItems.Add("Save &As…", null, (_, _) => SaveAsDialog());
        file.DropDownItems.Add(new ToolStripSeparator());
        var miPrint = (ToolStripMenuItem)file.DropDownItems.Add("&Print…", null, (_, _) => PrintNow());
        miPrint.ShortcutKeys(Keys.Control | Keys.P);
        file.DropDownItems.Add("Print &Log…", null, (_, _) => ShowPrintLog());
        file.DropDownItems.Add(new ToolStripSeparator());
        var miValidate = (ToolStripMenuItem)file.DropDownItems.Add("&Validate", null, (_, _) => ShowValidation());
        miValidate.ShortcutKeys(Keys.F7);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Co&nnections…", null, (_, _) =>
        {
            using var dlg = new ConnectionsDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _sourceRows.Clear(); _sourceFails.Clear(); _listRowSets.Clear(); _listRowFails.Clear(); _listRowSig.Clear();          // store changed: stale rows out
                PopulateDatasetCombo();
                if (_modeButton.Checked && _doc is not null) BuildDataPanel();
            }
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        // gray out what can't run right now (shortcuts already no-op safely;
        // this is purely the visual affordance, refreshed as the menu opens)
        file.DropDownOpening += (_, _) =>
        {
            bool doc = _doc is not null;
            miClose.Enabled = miSave.Enabled = miSaveAs.Enabled = doc;
            miPrint.Enabled = miValidate.Enabled = doc;
            RebuildRecentMenu(recent);
        };
        RebuildRecentMenu(recent);   // populated before first open too

        var edit = new ToolStripMenuItem("&Edit");
        // Undo.Changed already runs OutlineMaybeRefresh — only the canvas needs a poke
        var miUndo = (ToolStripMenuItem)edit.DropDownItems.Add(
            "&Undo", null, (_, _) => { _doc?.Undo.Undo(); _canvas.Invalidate(); });
        miUndo.ShortcutKeys(Keys.Control | Keys.Z);
        var miRedo = (ToolStripMenuItem)edit.DropDownItems.Add(
            "&Redo", null, (_, _) => { _doc?.Undo.Redo(); _canvas.Invalidate(); });
        miRedo.ShortcutKeys(Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        var miGroup = (ToolStripMenuItem)edit.DropDownItems.Add(
            "&Group", null, (_, _) => GroupSelection());
        miGroup.ShortcutKeys(Keys.Control | Keys.G);
        var miUngroup = (ToolStripMenuItem)edit.DropDownItems.Add(
            "U&ngroup", null, (_, _) => UngroupSelection());
        miUngroup.ShortcutKeys(Keys.Control | Keys.Shift | Keys.G);
        edit.DropDownItems.Add(new ToolStripSeparator());
        var miFields = (ToolStripMenuItem)edit.DropDownItems.Add(
            "&Fields, Maps && Lists…", null, (_, _) => ShowMetadataDialog());
        miFields.ShortcutKeys(Keys.F4);
        edit.DropDownItems.Add(new ToolStripSeparator());
        var miFwd = (ToolStripMenuItem)edit.DropDownItems.Add(
            "Bring &Forward", null, (_, _) => Reorder(true));
        miFwd.ShortcutKeys(Keys.Control | Keys.Oemplus);
        var miBack = (ToolStripMenuItem)edit.DropDownItems.Add(
            "Send &Backward", null, (_, _) => Reorder(false));
        miBack.ShortcutKeys(Keys.Control | Keys.OemMinus);
        edit.DropDownOpening += (_, _) =>
        {
            bool doc = _doc is not null;
            bool design = doc && !_modeButton.Checked;
            miUndo.Enabled = design && _doc!.Undo.CanUndo;
            miUndo.Text = "&Undo" + (design && _doc!.Undo.UndoLabel is { } ul ? $" {ul}" : "");
            miRedo.Enabled = design && _doc!.Undo.CanRedo;
            miRedo.Text = "&Redo" + (design && _doc!.Undo.RedoLabel is { } rl ? $" {rl}" : "");
            miGroup.Enabled = design && _canvas.Selection.Count > 1;
            miUngroup.Enabled = design && _canvas.Selected is { } sel &&
                                EditorDoc.GroupContainer(sel.El) is not null;
            miFields.Enabled = doc;
            miFwd.Enabled = miBack.Enabled = design && _canvas.Selected is not null;
        };

        var insert = new ToolStripMenuItem("&Insert");
        insert.DropDownItems.Add("&Text", null, (_, _) => InsertObject("text"));
        var bc = new ToolStripMenuItem("&Barcode");
        foreach (var sym in Etiq.Core.EtiqTemplate.Symbologies)
        {
            string caption = LabelRenderer.IsImplemented(sym)
                ? sym : sym + "   (render not implemented yet)";
            bc.DropDownItems.Add(caption, null, (_, _) => InsertObject("barcode", sym));
        }
        insert.DropDownItems.Add(bc);
        insert.DropDownItems.Add("&Line", null, (_, _) => InsertObject("line"));
        insert.DropDownItems.Add("Bo&x", null, (_, _) => InsertObject("box"));
        insert.DropDownItems.Add(new ToolStripSeparator());
        insert.DropDownItems.Add("La&yer…", null, (_, _) => InsertLayer());
        insert.DropDownOpening += (_, _) =>
        {
            bool can = _doc is not null && !_modeButton.Checked;   // Design mode only
            foreach (ToolStripItem it in insert.DropDownItems) it.Enabled = can;
        };

        var view = new ToolStripMenuItem("&View");
        _viewDesignItem = (ToolStripMenuItem)view.DropDownItems.Add(
            "&Design Mode", null, (_, _) => _modeButton.Checked = false);
        _viewDataItem = (ToolStripMenuItem)view.DropDownItems.Add(
            "Da&ta Mode", null, (_, _) => _modeButton.Checked = true);
        view.DropDownItems.Add(new ToolStripSeparator());
        var miFit = (ToolStripMenuItem)view.DropDownItems.Add(
            "&Fit to Window", null, (_, _) => _canvas.FitToWindow());
        miFit.ShortcutKeys(Keys.Control | Keys.D0);
        view.DropDownItems.Add(new ToolStripSeparator());
        var miStation = (ToolStripMenuItem)view.DropDownItems.Add(
            "Enter Print-&Station Mode…", null, (_, _) => EnterStationMode());
        view.DropDownOpening += (_, _) =>
        {
            bool doc = _doc is not null;
            _viewDesignItem!.Enabled = _viewDataItem!.Enabled = doc;
            miStation.Enabled = _doc?.Path is not null;   // needs a saved doc
        };

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Check for &Updates…", null, async (_, _) => await CheckForUpdates(interactive: true));
        help.DropDownItems.Add("&Options…", null, (_, _) => UpdateDialogs.ShowOptions(this));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("&About Etiquette…", null, (_, _) => ShowAbout());

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(insert);
        menu.Items.Add(view);
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    // ---------- new / insert ----------

    /// <summary>Gate for every action that would discard the current
    /// document (close / open / new). True = safe to proceed: nothing
    /// dirty, saved on request, or discard explicitly chosen. False = the
    /// user cancelled (or cancelled the Save As) — abort the action.</summary>
    private bool ConfirmDiscard()
    {
        if (_doc is null || !_doc.IsDirty) return true;
        var r = MessageBox.Show(this,
            $"Save changes to {Path.GetFileName(_doc.Path ?? "(unsaved new label)")}?",
            "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes)
        {
            SaveFile(null);           // pathless docs route to Save As
            return !_doc.IsDirty;     // Save As cancelled → still dirty → abort
        }
        return true;                  // No = discard
    }

    private void FileNew()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new NewLabelDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int w = dlg.WidthMils, h = dlg.HeightMils;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg"
                 width="{dlg.WidthAttr}" height="{dlg.HeightAttr}" viewBox="0 0 {w} {h}">
              <metadata>
                <etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
                </etiq:label>
              </metadata>
              <g data-layer="Main">
              </g>
            </svg>
            """;
        _doc = EditorDoc.Parse(xml);
        _doc.MarkDirty();   // choosing the canvas size already is work
        // generator stamp: warn (open anyway) when the template was saved
        // by a newer Etiquette — it may use features this build lacks
        if (Version.TryParse((string?)_doc.EtiqLabel()?.Attribute("generator") ?? "", out var gen))
        {
            var g3 = new Version(gen.Major, gen.Minor, Math.Max(gen.Build, 0));
            if (g3 > UpdateChecker.Current)
                MessageBox.Show(this,
                    $"This template was saved with Etiquette {g3} — you are running " +
                    $"{UpdateChecker.Current.ToString(3)}. It may use features this version " +
                    "doesn't understand; consider updating before editing it.",
                    "Newer template", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _canvas.Doc = _doc;
        _doc.Undo.Changed += OutlineMaybeRefresh; // deletes/undo/redo update the tree
        RefreshDeclaredFieldNames();
        UpdateStatusInfo();
        UpdateTitle();
        RefreshOutline();
        if (_modeButton.Checked) _modeButton.Checked = false;   // new docs open in Design
    }

    /// <summary>File > Label Size…: show the current physical size and
    /// resize (undoable). Guard: Design mode only — the station never
    /// reshapes its label.</summary>
    private void LabelSize()
    {
        if (_doc is null)
        {
            MessageBox.Show(this, "Create or open a label first (File → New / Open).", "No document");
            return;
        }
        if (_canvas.Mode != EditorMode.Design)
        {
            MessageBox.Show(this, "Switch to Design mode to change the label size.", "Data mode is locked");
            return;
        }
        var vb = _doc.ViewBox;
        var phys = _doc.PhysicalSize ?? (vb.W / 1000.0, vb.H / 1000.0, "in");
        using var dlg = new NewLabelDialog(phys.W, phys.H, phys.Unit, resize: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.WidthMils == (int)Math.Round(vb.W) && dlg.HeightMils == (int)Math.Round(vb.H) &&
            dlg.WidthAttr == (string?)_doc.Root.Attribute("width") &&
            dlg.HeightAttr == (string?)_doc.Root.Attribute("height")) return;
        _doc.SetLabelSize(dlg.WidthAttr, dlg.HeightAttr, dlg.WidthMils, dlg.HeightMils);
        _canvas.FitToWindow();
        _canvas.Invalidate();
    }

    /// <summary>Layer new objects land on: the selected object's layer,
    /// else the layer selected in the outline, else the first layer,
    /// else a fresh "Main" layer.</summary>
    private EditorLayer TargetLayer()
    {
        if (_canvas.Selected?.Layer is { } fromSel && !fromSel.Locked) return fromSel;
        // groups nest now: walk UP the tree to the enclosing layer node
        for (var n = _outline.SelectedNode; n is not null; n = n.Parent)
            if (n.Tag is EditorLayer l && !l.Locked) return l;
        var first = _doc!.Layers.FirstOrDefault(x => !x.Locked);
        return first ?? _doc.AddLayer("Main");
    }

    private void InsertObject(string kind, string symbology = "code128")
    {
        if (_doc is null)
        {
            MessageBox.Show(this, "Create or open a label first (File → New / Open).", "No document");
            return;
        }
        if (_canvas.Mode != EditorMode.Design)
        {
            MessageBox.Show(this, "Switch to Design mode to add objects.", "Data mode is locked");
            return;
        }
        var vb = _canvas.Doc!.ViewBox;
        double cx = vb.X + vb.W / 2, cy = vb.Y + vb.H / 2;
        var ns = _doc.Root.Name.Namespace;
        string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        System.Xml.Linq.XElement el = kind switch
        {
            "text" => new(ns + "text",
                new System.Xml.Linq.XAttribute("x", N(cx)),
                new System.Xml.Linq.XAttribute("y", N(cy)),
                new System.Xml.Linq.XAttribute("font-family", "Arial"),
                new System.Xml.Linq.XAttribute("font-size", N(Math.Max(vb.H * 0.12, 80))),
                "New Text"),
            "barcode" => new(ns + "rect",
                new System.Xml.Linq.XAttribute("x", N(cx - 750)),
                new System.Xml.Linq.XAttribute("y", N(cy - 250)),
                new System.Xml.Linq.XAttribute("width",
                    N(symbology is "qr" or "datamatrix" or "aztec" ? 800 : 1500)),
                new System.Xml.Linq.XAttribute("height",
                    N(symbology is "qr" or "datamatrix" or "aztec" ? 800 : 500)),
                new System.Xml.Linq.XAttribute("data-barcode", symbology),
                new System.Xml.Linq.XAttribute("data-value", "12345678")),
            "line" => new(ns + "line",
                new System.Xml.Linq.XAttribute("x1", N(cx - 750)),
                new System.Xml.Linq.XAttribute("y1", N(cy)),
                new System.Xml.Linq.XAttribute("x2", N(cx + 750)),
                new System.Xml.Linq.XAttribute("y2", N(cy)),
                new System.Xml.Linq.XAttribute("stroke", "black"),
                new System.Xml.Linq.XAttribute("stroke-width", "10")),
            _ => new(ns + "rect",
                new System.Xml.Linq.XAttribute("x", N(cx - 500)),
                new System.Xml.Linq.XAttribute("y", N(cy - 300)),
                new System.Xml.Linq.XAttribute("width", "1000"),
                new System.Xml.Linq.XAttribute("height", "600"),
                new System.Xml.Linq.XAttribute("fill", "none"),
                new System.Xml.Linq.XAttribute("stroke", "black"),
                new System.Xml.Linq.XAttribute("stroke-width", "10")),
        };

        _doc.AddObject(TargetLayer(), el, $"insert {kind}");
        RefreshOutline();
        _canvas.Select(EditorObject.Wrap(el));
    }

    private void InsertLayer()
    {
        if (_doc is null) return;
        string name = $"Layer {_doc.Layers.Count + 1}";
        _doc.AddLayer(name);
        RefreshOutline();
    }

    // ---------- file ----------

    // ---------- recent files ----------
    // settings.json: recentFiles = newline-joined MRU paths (newest first);
    // recentMax = how many to keep (Help > Options; default 10, cap 30)

    private const string RecentKey = "recentFiles";
    private const string RecentMaxKey = "recentMax";

    internal static int RecentMax =>
        int.TryParse(UpdateChecker.GetSetting(RecentMaxKey), out var n) && n > 0
            ? Math.Min(n, 30) : 10;

    private static List<string> RecentFiles() =>
        (UpdateChecker.GetSetting(RecentKey) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void PushRecent(string path)
    {
        var list = RecentFiles();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > RecentMax) list.RemoveRange(RecentMax, list.Count - RecentMax);
        UpdateChecker.SetSetting(RecentKey, string.Join('\n', list));
    }

    /// <summary>Rebuild the File > Open Recent submenu: newest first,
    /// files that vanished from disk grayed out, Clear at the bottom.</summary>
    private void RebuildRecentMenu(ToolStripMenuItem recent)
    {
        recent.DropDownItems.Clear();
        var list = RecentFiles();
        if (list.Count > RecentMax) list = list.Take(RecentMax).ToList();
        foreach (var p in list)
        {
            string path = p;
            var item = new ToolStripMenuItem(Path.GetFileName(path))
                { ToolTipText = path, Enabled = File.Exists(path) };
            item.Click += (_, _) =>
            {
                if (!ConfirmDiscard()) return;
                OpenFile(path);
            };
            recent.DropDownItems.Add(item);
        }
        recent.Enabled = list.Count > 0;
        if (list.Count == 0) return;
        recent.DropDownItems.Add(new ToolStripSeparator());
        recent.DropDownItems.Add("&Clear Recent", null, (_, _) =>
        {
            UpdateChecker.SetSetting(RecentKey, null);
            RebuildRecentMenu(recent);
        });
    }

    /// <summary>File > Close: back to the empty no-document state.</summary>
    private void CloseFile()
    {
        if (_doc is null || !ConfirmDiscard()) return;
        _doc = null;
        _sourceRows.Clear(); _sourceFails.Clear(); _listRowSets.Clear(); _listRowFails.Clear(); _listRowSig.Clear();
        _canvas.Doc = null;          // fires SelectionChanged(null) → inspector clears
        RefreshOutline();
        if (_modeButton.Checked) BuildDataPanel();   // empties the data pane
        UpdateStatusInfo();
        UpdateTitle();
    }

    private void OpenDialog()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
            { Filter = "Etiquette templates (*.svg)|*.svg|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            _doc = EditorDoc.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed"); return;
        }
        // generator stamp: warn (open anyway) when the template was saved
        // by a newer Etiquette — it may use features this build lacks
        if (Version.TryParse((string?)_doc.EtiqLabel()?.Attribute("generator") ?? "", out var gen))
        {
            var g3 = new Version(gen.Major, gen.Minor, Math.Max(gen.Build, 0));
            if (g3 > UpdateChecker.Current)
                MessageBox.Show(this,
                    $"This template was saved with Etiquette {g3} — you are running " +
                    $"{UpdateChecker.Current.ToString(3)}. It may use features this version " +
                    "doesn't understand; consider updating before editing it.",
                    "Newer template", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _canvas.Doc = _doc;
        _doc.Undo.Changed += OutlineMaybeRefresh; // deletes/undo/redo update the tree
        RefreshDeclaredFieldNames();
        UpdateStatusInfo();
        UpdateTitle();
        _sourceRows.Clear(); _sourceFails.Clear(); _listRowSets.Clear(); _listRowFails.Clear(); _listRowSig.Clear();                 // rows belong to the previous doc
        PushRecent(path);
        RefreshOutline();
        if (_modeButton.Checked) BuildDataPanel();
    }

    private void SaveFile(string? path)
    {
        if (_doc is null) return;
        if (path is null && _doc.Path is null) { SaveAsDialog(); return; }   // new unsaved doc
        try
        {
            // stamp the saving app's version on etiq:label — open warns when
            // a template was made by a NEWER Etiquette than the one running
            _doc.EtiqLabel()?.SetAttributeValue("generator", UpdateChecker.Current.ToString(3));
            _doc.Save(path);
            UpdateStatusInfo();
            UpdateTitle();
            PushRecent(_doc.Path!);   // Save As introduces a brand-new path
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed"); }
    }

    private void SaveAsDialog()
    {
        if (_doc is null) return;
        using var dlg = new SaveFileDialog { Filter = "Etiquette templates (*.svg)|*.svg" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SaveFile(dlg.FileName);
    }

    private void ShowAbout()
    {
        using var dlg = new Form
        {
            Text = "About Etiquette",
            ClientSize = new Size(460, 260),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(dlg);
        Image? logo = null;
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("etiq.logo.png");
            if (s is not null)
            {
                using var tmp = new Bitmap(s);
                logo = new Bitmap(tmp);   // deep copy: outlives the stream
            }
        }
        catch { /* logo optional */ }
        dlg.Controls.Add(new PictureBox
        {
            Image = logo, SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(20, 15, 420, 132), BackColor = Color.White,
        });
        dlg.BackColor = Color.White;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        dlg.Controls.Add(new Label
        {
            Text = $"Etiquette Designer {ver?.ToString(3)}\n" +
                   "Clean-room label design & print toolchain.\n" +
                   "Templates are plain SVG — yours forever.",
            Bounds = new Rectangle(20, 158, 420, 60),
            TextAlign = ContentAlignment.MiddleCenter,
        });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(190, 222, 80, 28) };
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        dlg.ShowDialog(this);
        logo?.Dispose();
    }

    /// <summary>Native print (Phase 3 GDI path): Data mode prints the
    /// resolved values shown in the preview; Design mode prints the sample
    /// text as drawn.</summary>
    private void PrintNow()
    {
        if (_doc is null) return;
        using var measurer = new GdiTextMeasurer();
        PrintService.Print(this, _doc, _canvas.ResolvedValues, measurer);
    }

    /// <summary>Effective printer for direct printing: embedded picker
    /// (null while "Default printer" is checked), else the pinned name,
    /// else null = machine default.</summary>
    private string? PanelPrinter(EtiqTemplate.PanelDef panel) =>
        panel.Printer == "embedded"
            ? (_panelPrinterDefault?.Checked != false ? null : _panelPrinterBox?.Text)
            : panel.Printer;

    /// <summary>Copies/collation for a panel-driven run: fixed:N wins,
    /// else the embedded controls, else 1/grouped.</summary>
    private (int Copies, bool Grouped) PanelRun(EtiqTemplate.PanelDef panel) => (
        panel.FixedCopies ?? (int)(_panelCopies?.Value ?? 1),
        panel.Collate != "sequenced" &&
        (panel.Collate == "grouped" || _panelCollate is null || _panelCollate.SelectedIndex != 1));

    /// <summary>Panel-driven single-label print: copies expanded into
    /// pages; direct mode goes straight to the (named or default) printer
    /// with no system dialog — the labelprint behavior.</summary>
    private void PrintNow(EtiqTemplate.PanelDef panel, int copies)
    {
        if (_doc is null) return;
        using var measurer = new GdiTextMeasurer();
        var pages = Enumerable.Repeat(_canvas.ResolvedValues, Math.Max(1, copies)).ToList();
        PrintService.PrintBatch(this, _doc, pages, measurer,
            direct: panel.Print == "direct", printer: PanelPrinter(panel));
    }

    private void ShowMetadataDialog()
    {
        if (_doc is null) return;
        void AfterInstall()
        {
            RefreshDeclaredFieldNames();
            if (_modeButton.Checked) BuildDataPanel();       // Data mode: new prompts/lists
            _canvas.Invalidate();
        }
        using var dlg = new MetadataDialog(_doc, applied =>
        {
            // Apply button: install a snapshot mid-session, one undo step each
            _doc.Undo.Push(_doc.ReplaceEtiqLabel(applied));
            AfterInstall();
        }, SampleValues());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.HasUnappliedChanges)                          // OK after Apply = no-op skip
        {
            _doc.Undo.Push(_doc.ReplaceEtiqLabel(dlg.Result));
            AfterInstall();
        }
    }

    /// <summary>Per-field sample values for previews: the last Data-mode
    /// resolve when there is one, else each bound element's design-time
    /// content (text, or data-value on barcodes) — the same thing the
    /// canvas shows in Design mode.</summary>
    private Dictionary<string, string> SampleValues()
    {
        var d = new Dictionary<string, string>();
        if (_canvas.ResolvedValues is { } rv)
            foreach (var (k, v) in rv) d[k] = v;
        if (_doc is null) return d;
        foreach (var el in _doc.Xml.Descendants())
        {
            string? f = (string?)el.Attribute("data-field");
            if (string.IsNullOrEmpty(f) || d.ContainsKey(f)) continue;
            string v = (string?)el.Attribute("data-value") ?? el.Value;
            if (!string.IsNullOrWhiteSpace(v)) d[f] = v.Trim();
        }
        return d;
    }

    /// <summary>Feed the declared field names to the data-field dropdown in
    /// the property grid.</summary>
    private void RefreshDeclaredFieldNames()
    {
        FieldNameConverter.Names = _doc?.EtiqLabel()?
            .Elements(Etiq.Editor.Core.EditorDoc.EtiqNs + "field")
            .Select(f => (string?)f.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray() ?? Array.Empty<string>();
    }

    private void ShowValidation()
    {
        if (_doc is null) return;
        var findings = _doc.Validate();
        MessageBox.Show(this,
            findings.Count == 0 ? "No findings — template is clean."
                : string.Join(Environment.NewLine, findings.Select(f => f.ToString())),
            $"Validate — {findings.Count(f => f.Severity == Severity.Error)} error(s), " +
            $"{findings.Count(f => f.Severity == Severity.Warning)} warning(s)");
    }

    private void Reorder(bool forward)
    {
        if (_doc is null || _canvas.Selected is null) return;
        _doc.ReorderZ(_canvas.Selected, forward);
        RefreshOutline();
    }

    private void GroupSelection()
    {
        if (_doc is null || _canvas.Selection.Count < 2)
        {
            MessageBox.Show(this, "Select two or more objects to group (Ctrl+click or drag a box).", "Group");
            return;
        }
        // a group lives in exactly ONE layer (its members are its XML
        // children) — grouping across layers pulls everything into the
        // first object's layer, so make that explicit
        var layerNames = _canvas.Selection
            .Select(o => o.Layer?.Name ?? "(none)").Distinct().ToList();
        if (layerNames.Count > 1 && MessageBox.Show(this,
                $"The selection spans layers ({string.Join(", ", layerNames)}). " +
                $"A group lives in one layer — grouping moves everything into " +
                $"\"{_canvas.Selection[0].Layer?.Name ?? "(none)"}\". Continue?",
                "Group", MessageBoxButtons.OKCancel) != DialogResult.OK)
            return;
        _doc.GroupObjects(_canvas.Selection.ToList());
        RefreshOutline();
    }

    private void UngroupSelection()
    {
        if (_doc is null || _canvas.Selected is null) return;
        var g = EditorDoc.GroupContainer(_canvas.Selected.El);
        if (g is null)
        {
            MessageBox.Show(this, "Selection is not inside a group.", "Ungroup");
            return;
        }
        _doc.Ungroup(g);
        RefreshOutline();
    }

    // ---------- outline ----------

    private string _outlineSig = "";

    /// <summary>What the outline SHOULD show, as a comparable string. Element
    /// identity + caption per node, so deletes, inserts, undo/redo, z-order
    /// moves and renames all change it, while pure geometry drags don't.</summary>
    private string OutlineSignature()
    {
        if (_doc is null) return "";
        var sb = new System.Text.StringBuilder();
        void Walk(System.Xml.Linq.XElement container)
        {
            foreach (var child in container.Elements())
            {
                if (IsPlainGroup(child))
                {
                    sb.Append('g').Append(child.GetHashCode()).Append('|')
                      .Append(GroupCaption(child)).Append('\n');
                    Walk(child);
                }
                else if (EditorObject.IsEditable(child))
                {
                    sb.Append(child.GetHashCode()).Append('|')
                      .Append(Caption(EditorObject.Wrap(child))).Append('\n');
                }
            }
        }
        foreach (var layer in _doc.Layers)
        {
            sb.Append(layer.El.GetHashCode()).Append('|').Append(LayerCaption(layer)).Append('\n');
            Walk(layer.El);
        }
        return sb.ToString();
    }

    /// <summary>Called on EVERY undo-stack change (deletes, undo/redo,
    /// drags...). Rebuilds the tree only when its content is actually out
    /// of date, so mouse-move drags stay cheap and flicker-free.</summary>
    /// <summary>Titlebar = "<file>[*] — Etiquette Designer <version>".
    /// The asterisk tracks EditorDoc.IsDirty (serialize-and-compare, so
    /// undoing back to the saved state clears it). Called from every place
    /// the document, its path, or its content changes; cheap enough that
    /// over-calling is fine. (When tabs arrive, this string moves to the
    /// tab header and the titlebar shows the active tab's.)</summary>
    /// <summary>The status-bar slot the filename used to occupy (it lives
    /// in the titlebar now): label size, then the selection — kind and
    /// size/position of one object, or a count. What you're editing and
    /// how big it is, at a glance.</summary>
    private void UpdateStatusInfo()
    {
        if (_doc is null) { _statusDoc.Text = "no document"; return; }
        var vb = _doc.ViewBox;
        string info = $"{vb.W / 1000.0:0.##} × {vb.H / 1000.0:0.##} in";
        var sel = _canvas.Selection;
        if (sel.Count == 1)
        {
            var o = sel[0];
            try
            {
                var b = o.Bounds();
                info += $"    |    {o.Kind}: {b.W:0} × {b.H:0} mils @ ({b.X:0}, {b.Y:0})";
            }
            catch { info += $"    |    {o.Kind}"; }
        }
        else if (sel.Count > 1)
            info += $"    |    {sel.Count} objects selected";
        _statusDoc.Text = info;
    }

    private void UpdateTitle()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string app = $"Etiquette Designer {ver?.ToString(3)}";
        if (_doc is null) { Text = app; return; }
        string name = _doc.Path is null ? "(unsaved)" : Path.GetFileName(_doc.Path);
        Text = $"{name}{(_doc.IsDirty ? "*" : "")} — {app}";
    }

    private void OutlineMaybeRefresh()
    {
        UpdateTitle();   // undo/redo/edits all pass through here
        UpdateStatusInfo();  // size / selection bounds may have changed with them
        if (OutlineSignature() != _outlineSig) RefreshOutline();
        // ShowSelection is now cheap when nothing structural changed (it
        // just re-reads values) — and it also swaps in the right row set
        // when an undo/redo changed a structural attribute (symbology,
        // logo mode), which plain RefreshValues left stale
        _props.ShowSelection(_doc, _canvas.Selection);
    }

    private static bool IsPlainGroup(System.Xml.Linq.XElement e) =>
        e.Name.LocalName == "g" && e.Attribute("data-layer") is null;

    /// <summary>Groups show their data-name when set, else a member count.
    /// data-name is editor metadata only — renderers ignore it.</summary>
    private static string GroupCaption(System.Xml.Linq.XElement g) =>
        (string?)g.Attribute("data-name") is { Length: > 0 } nm
            ? nm
            : $"Group ({g.Descendants().Count(EditorObject.IsEditable)})";

    /// <summary>Mirror the XML structure: groups appear as nested nodes, so
    /// clicking a Group selects the whole group and clicking a member
    /// selects just that member — the structure is visible, not implied.</summary>
    private void AddOutlineChildren(TreeNode parent, System.Xml.Linq.XElement container)
    {
        foreach (var child in container.Elements())
        {
            if (IsPlainGroup(child))
            {
                var gn = new TreeNode(GroupCaption(child)) { Tag = child };
                AddOutlineChildren(gn, child);
                parent.Nodes.Add(gn);
            }
            else if (EditorObject.IsEditable(child))
            {
                var o = EditorObject.Wrap(child);
                parent.Nodes.Add(new TreeNode(Caption(o)) { Tag = o });
            }
        }
    }

    private void RefreshOutline()
    {
        _outline.BeginUpdate();
        _outline.Nodes.Clear();
        if (_doc is not null)
            foreach (var layer in _doc.Layers)
            {
                var ln = new TreeNode(LayerCaption(layer)) { Tag = layer };
                AddOutlineChildren(ln, layer.El);
                _outline.Nodes.Add(ln);
            }
        _outline.ExpandAll();
        _outline.EndUpdate();
        _outlineSig = OutlineSignature();
        // re-highlight ONLY for a single selection: AfterSelect routes back
        // through canvas.Select (single), which would collapse a multi-select
        if (_canvas.Selection.Count == 1) SyncOutlineSelection(_canvas.Selected);
    }

    private static string LayerCaption(EditorLayer l) =>
        l.Name + (l.Locked ? " 🔒" : "") + (l.Printed ? "" : " (no print)") +
        (l.Visible ? "" : " (hidden)");

    private static string Caption(EditorObject o)
    {
        string? field = (string?)o.El.Attribute("data-field");
        return o.Kind switch
        {
            ObjectKind.Text => $"Text: {(field is not null ? "{" + field + "}" : Snip(o.El.Value))}",
            ObjectKind.Barcode => $"Barcode ({(string?)o.El.Attribute("data-barcode")}): " +
                                  (field is not null ? "{" + field + "}" : Snip((string?)o.El.Attribute("data-value") ?? "")),
            _ => o.Kind.ToString(),
        };
        static string Snip(string s) => s.Length > 18 ? s[..18] + "…" : s;
    }

    /// <summary>True while WE move the outline highlight to mirror a canvas
    /// selection — AfterSelect must not route that back into canvas.Select,
    /// or a whole-group selection collapses to its primary member the
    /// moment the highlighted node changes (grouped objects then drag
    /// WITHOUT their companions).</summary>
    private bool _syncingOutline;

    private static TreeNode? FindObjectNode(TreeNodeCollection nodes, System.Xml.Linq.XElement el)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is EditorObject t && t.El == el) return n;
            var inner = FindObjectNode(n.Nodes, el);
            if (inner is not null) return inner;
        }
        return null;
    }

    /// <summary>When the canvas selection IS exactly one whole group,
    /// highlight that Group node; otherwise leave the tree alone.</summary>
    private void SyncOutlineToGroup()
    {
        if (_doc is null || _canvas.Selection.Count < 2) return;
        var containers = _canvas.Selection
            .Select(s => Etiq.Editor.Core.EditorDoc.GroupContainer(s.El))
            .Distinct().ToList();
        if (containers.Count != 1 || containers[0] is not { } g) return;
        if (g.Descendants().Count(EditorObject.IsEditable) != _canvas.Selection.Count) return;
        static TreeNode? Find(TreeNodeCollection nodes, System.Xml.Linq.XElement el)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is System.Xml.Linq.XElement x && x == el) return n;
                var inner = Find(n.Nodes, el);
                if (inner is not null) return inner;
            }
            return null;
        }
        _syncingOutline = true;
        try
        {
            var node = Find(_outline.Nodes, g);
            if (node is not null) _outline.SelectedNode = node;
        }
        finally
        {
            _syncingOutline = false;
        }
    }

    private void SyncOutlineSelection(EditorObject? o)
    {
        if (o is null) return;
        _syncingOutline = true;
        try
        {
            var n = FindObjectNode(_outline.Nodes, o.El);
            if (n is not null) _outline.SelectedNode = n;
        }
        finally
        {
            _syncingOutline = false;
        }
    }

    // ---------- design / data mode ----------

    // ---------- print station ----------
    // A dedicated kiosk-ish presentation of the SAME window: everything
    // but the essentials is removed — no menu, no toolbar, no outline, no
    // inspector; just the label preview, the data panel and the status
    // bar. Answers the "labelprint for Etiquette templates" need without
    // a second app.
    //
    // Persistence: settings.json key "stationFile" = the template path.
    // While set, EVERY start of etiqedit opens that template in station
    // mode — the machine's role, not a per-session flag. Set via View →
    // Enter Print-Station Mode (persists) or `--station <file>` (transient,
    // this run only). Exit ONLY via Ctrl+Shift+F12 + typing UNLOCK — no
    // clickable path, so it cannot be turned off by accident.

    private const string StationFileKey = "stationFile";
    // station-only: the label re-fits whenever the canvas area resizes
    private EventHandler? _stationFit;
    private const Keys StationExitChord = Keys.Control | Keys.Shift | Keys.F12;

    /// <summary>View → Enter Print-Station Mode: persists the current
    /// (saved) template as this machine's station template and switches
    /// the UI immediately.</summary>
    private void EnterStationMode()
    {
        if (_doc?.Path is not { } path)
        {
            MessageBox.Show(this,
                "Save the label first — print-station mode reopens it by path on every start.",
                "Print-Station Mode");
            return;
        }
        if (MessageBox.Show(this,
                "Turn this machine into a print station for\n\n" +
                $"    {Path.GetFileName(path)}\n\n" +
                "etiqedit will open straight into this stripped-down print view on every " +
                "start until the mode is explicitly turned off.\n\n" +
                "To exit later: press Ctrl+Shift+F12 and type UNLOCK.",
                "Enter Print-Station Mode", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
            return;
        UpdateChecker.SetSetting(StationFileKey, path);
        StationLock();
    }

    /// <summary>Apply the station presentation to the running window.</summary>
    private void StationLock()
    {
        _stationLocked = true;
        _modeButton.Checked = true;      // → SetMode(Data)
        _modeButton.Enabled = false;
        // strip the chrome: menu (disable too — invisible menus still fire
        // their shortcut keys), toolbar, outline pane
        if (_menuStrip is not null) { _menuStrip.Visible = false; _menuStrip.Enabled = false; }
        if (_toolStrip is not null) _toolStrip.Visible = false;
        // the left pane now IS the data-entry pane — keep it; SetMode(Data)
        // already collapsed the inspector side
        Text = $"Etiquette Print Station — {Path.GetFileName(_doc?.Path ?? "label")}";
        // operators don't zoom/pan: keep the label fitted through window
        // (and splitter) resizes for the life of the lock
        _stationFit ??= (_, _) => _canvas.FitToWindow();
        _canvas.Resize -= _stationFit;   // never double-subscribe
        _canvas.Resize += _stationFit;
        _canvas.FitToWindow();
    }

    /// <summary>Restore the full editor (chord + typed confirmation only).</summary>
    private void StationUnlock()
    {
        _stationLocked = false;
        _modeButton.Enabled = true;
        if (_menuStrip is not null) { _menuStrip.Visible = true; _menuStrip.Enabled = true; }
        if (_toolStrip is not null) _toolStrip.Visible = true;
        UpdateTitle();
        if (_stationFit is not null) _canvas.Resize -= _stationFit;
        _canvas.FitToWindow();
    }

    /// <summary>The ONLY way out of station mode: Ctrl+Shift+F12, then the
    /// word UNLOCK typed in full — deliberate by construction.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_stationLocked && keyData == StationExitChord)
        {
            StationExitPrompt();
            return true;
        }
        // station log chord: deliberate (Ctrl+Shift+L), never on the panel
        // unless the template opts in with buttons="…,log"
        if (_stationLocked && keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            ShowPrintLog();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Print log viewer. Reprint replays the selected record's
    /// logged values through the template's own print path (etiq:panel
    /// direct settings when declared, the system dialog otherwise); with
    /// no open document the log is view-only.</summary>
    private void ShowPrintLog()
    {
        Action<Dictionary<string, string>>? reprint = null;
        if (_doc is not null)
        {
            reprint = values =>
            {
                var panel = EtiqTemplate.Parse(_doc.Xml.ToString()).Panel;
                using var measurer = new GdiTextMeasurer();
                PrintService.PrintBatch(this, _doc,
                    new IReadOnlyDictionary<string, string>?[] { values }, measurer,
                    direct: panel.Print == "direct", printer: PanelPrinter(panel));
            };
        }
        using var dlg = new PrintLogDialog(this, reprint);
        dlg.ShowDialog(this);
    }

    private void StationExitPrompt()
    {
        using var f = new Form
        {
            Text = "Exit Print-Station Mode", ClientSize = new Size(360, 130),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var lbl = new Label
        {
            Text = "Type UNLOCK to leave print-station mode and restore the full editor:",
            Left = 12, Top = 10, Width = 336, Height = 32,
        };
        var tb = new TextBox { Left = 12, Top = 48, Width = 336 };
        var ok = new Button { Text = "Exit station mode", DialogResult = DialogResult.OK, Left = 148, Top = 88, Width = 120, Enabled = false };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 276, Top = 88, Width = 72 };
        tb.TextChanged += (_, _) => ok.Enabled =
            tb.Text.Trim().Equals("UNLOCK", StringComparison.Ordinal);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        UpdateChecker.SetSetting(StationFileKey, null);   // off until re-enabled
        StationUnlock();
    }

    private void SetMode(EditorMode mode)
    {
        _canvas.Mode = mode;
        _modeButton.Text = mode == EditorMode.Design ? "Mode: DESIGN" : "Mode: DATA";
        bool design = mode == EditorMode.Design;
        _props.Visible = design;
        _dataPanel.Visible = !design;
        // left pane: outline (Design) ↔ data entry (Data); right (inspector)
        // side collapses entirely in Data mode so the preview gets the room
        _outline.Visible = design;
        _outline.Enabled = design;   // belt & braces: tree clicks stay dead in Data mode
        if (_split2 is not null) _split2.Panel2Collapsed = !design;
        if (_split1 is not null && _split1.Width > 0)
        {
            try
            {
                float uiF = Ui.Factor(this);
                // data entry needs room for its 300px-wide inputs; the
                // outline tree is happy narrower — remember nothing, just
                // set a sensible width for what the pane now holds
                _split1.SplitterDistance = (int)((design ? 260 : 340) * uiF);
            }
            catch (ArgumentOutOfRangeException) { /* tiny window: keep as-is */ }
        }
        if (mode == EditorMode.Data)
        {
            _canvas.Select(null);
            BuildDataPanel();
        }
        else
        {
            _canvas.ResolvedValues = null;
        }
        _canvas.Invalidate();
    }

    private void BuildDataPanel()
    {
        _previewTimer?.Stop();
        _previewTimer?.Dispose();
        _previewTimer = null;
        _dataPanel.SuspendLayout();
        // AutoScroll gotcha: controls added while the panel is scrolled are
        // placed relative to the SCROLLED origin — a rebuild after the old
        // panel was scrolled (or after opening another file in Data mode)
        // lands everything outside the viewport and the pane looks empty.
        _dataPanel.AutoScrollPosition = Point.Empty;
        var old = _dataPanel.Controls.Cast<Control>().ToList();
        _dataPanel.Controls.Clear();          // Clear does NOT dispose
        foreach (var c in old) c.Dispose();
        _promptBoxes.Clear();
        _listCombos.Clear();
        _listDisplayToKey.Clear();
        if (_doc is null)
        {
            if (_stationLocked)   // locked with a missing template: say why
                _dataPanel.Controls.Add(new Label
                {
                    Left = 10, Top = 12, Width = _dataPanel.Width - 20, Height = 200,
                    ForeColor = Color.Firebrick,
                    Text = "Print-station template missing or unreadable:\n\n" +
                           (UpdateChecker.GetSetting(StationFileKey) ?? "(unknown)") +
                           "\n\nRestore the file and restart etiqedit — or press " +
                           "Ctrl+Shift+F12 and type UNLOCK to exit station mode.",
                });
            _dataPanel.ResumeLayout();
            return;
        }

        EtiqTemplate template;
        try { template = EtiqTemplate.Parse(_doc.Xml.ToString()); }
        catch (Exception ex)
        {
            // never leave the pane silently blank — say why
            _dataPanel.Controls.Add(new Label
            {
                Left = 10, Top = 12, Width = _dataPanel.Width - 20, Height = 120,
                ForeColor = Color.Firebrick, Text = "Template error: " + ex.Message,
            });
            _dataPanel.ResumeLayout();
            return;
        }

        // debounced auto-preview: any edit re-resolves ~350ms after the last
        // keystroke; errors go to the inline status line, never a popup
        _previewTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _previewTimer.Tick += async (_, _) => { _previewTimer!.Stop(); await RefreshPreviewAsync(template); };
        // NULL-SAFE: a panel rebuild disposes the old controls AFTER
        // clearing _previewTimer — disposing the focused box fires its
        // Leave handler, which lands here with no timer
        void Touched() { _previewTimer?.Stop(); _previewTimer?.Start(); }

        // built at runtime, AFTER the form's one-shot DPI scale pass ran —
        // Control.Scale never touches later-added children, so factor every
        // hand-placed coordinate here explicitly
        float uf = Ui.Factor(this);
        int S(int v) => (int)(v * uf);

        int y = S(12);
        void AddLabel(string text)
        {
            _dataPanel.Controls.Add(new Label
                { Text = text, Left = S(10), Top = y, Width = S(320), AutoSize = false });
            y += S(20);
        }

        var panel = template.Panel;
        _panelCopies = null; _panelCollate = null;   // recreated below if embedded
        _panelPrinterDefault = null; _panelPrinterBox = null;

        AddLabel("DATA MODE — layout locked");
        y += S(8);
        if (panel.ButtonsAt == "top") EmitButtons();
        void EmitPrompt(EtiqTemplate.Field f)
        {
            AddLabel(f.Caption ?? f.Name + ":");
            // casing follows the field's declared case= (never assumed);
            // the resolver normalizes regardless, this just mirrors it live
            var tb = new TextBox
            {
                Left = S(10), Top = y, Width = S(300),
                CharacterCasing = f.Case switch
                {
                    "upper" => CharacterCasing.Upper,
                    "lower" => CharacterCasing.Lower,
                    _ => CharacterCasing.Normal,   // normal/title/absent; resolver
                                                   // normalizes title at resolve time
                },
            };
            if (f.Source == "prompt" && f.Default is { Length: > 0 } dflt)
                tb.Text = dflt;   // prefill; Clear restores it too
            tb.TextChanged += (_, _) => Touched();
            // remote sources gate on focus (see FetchSourceColumn): leaving
            // the box is the "entry done" signal, so refresh again then
            tb.Leave += (_, _) => Touched();
            _promptBoxes[f.Name] = tb;
            _dataPanel.Controls.Add(tb);
            y += S(34);
        }
        // embedded pick lists: one shared selection per list drives every
        // field bound to it (the "set" behavior); the box is type-to-search
        void EmitList(EtiqTemplate.ListDef l)
        {
            AddLabel(l.Caption ?? l.Name + ":");
            var cb = new ComboBox
            {
                Left = S(10), Top = y, Width = S(300),
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            _listCombos[l.Name] = cb;
            RebuildListItems(template, l, cb);
            cb.SelectedIndexChanged += (_, _) => Touched();
            cb.TextChanged += (_, _) => Touched();
            _dataPanel.Controls.Add(cb);
            y += S(34);
        }

        // panel order = FIELD DECLARATION ORDER (the F4 Fields tab): a
        // prompt appears where declared; a list appears where its first
        // bound field is declared; unreferenced lists follow at the end
        var emittedLists = new HashSet<string>();
        var listsByName = template.Lists.ToDictionary(l => l.Name);
        // collect the inputs as (token, emit) pairs, then apply the
        // template's explicit order= (unlisted inputs keep declaration
        // order — OrderBy is stable)
        var inputs = new List<(string Tok, Action Emit)>();
        foreach (var f in template.Fields)
        {
            if (f.PanelHide) continue;   // etiq:panel-level opt-out per field
            var ff = f;
            if (f.Source == "prompt")
                inputs.Add(($"field:{f.Name}", () => EmitPrompt(ff)));
            else if (f.Source is ("epicor" or "rest") && f.Override)
                inputs.Add(($"field:{f.Name}", () =>
                {
                    // overrideable pull: empty box = fetched value (shown as
                    // ghost text once resolved); typing beats the pull
                    EmitPrompt(ff);
                    _promptBoxes[ff.Name].PlaceholderText = "(from source)";
                }));
            else if (f.Source == "list" && f.ListRef is { } lr &&
                     emittedLists.Add(lr) && listsByName.TryGetValue(lr, out var l) &&
                     !l.PanelHide)
            {
                var ll = l;
                inputs.Add(($"list:{l.Name}", () => EmitList(ll)));
            }
        }
        foreach (var l in template.Lists.Where(l => !l.PanelHide && emittedLists.Add(l.Name)))
        {
            var ll = l;
            inputs.Add(($"list:{l.Name}", () => EmitList(ll)));
        }
        if (panel.Order.Length > 0)
            inputs = inputs.OrderBy(i =>
            {
                int idx = Array.IndexOf(panel.Order, i.Tok);
                return idx < 0 ? int.MaxValue : idx;
            }).ToList();
        foreach (var (_, emit) in inputs) emit();

        // embedded copies + collation (etiq:panel copies="embedded"):
        // collation only matters past 1 copy — grayed until then
        if (panel.Copies == "embedded")
        {
            AddLabel("Copies:");
            var cn = new NumericUpDown
                { Left = S(10), Top = y, Width = S(70), Minimum = 1, Maximum = 999, Value = 1 };
            _dataPanel.Controls.Add(cn);
            _panelCopies = cn;
            // collation ONLY matters when a single run yields MORE THAN ONE
            // page at 1 copy (a Print All batch) AND copies > 1 — a single
            // label collates identically either way. collate="ask" hides
            // the selector entirely; the popup covers the rare case.
            bool batchPossible = template.Lists.Any(l => l.Rows.Count > 0);
            if (panel.Collate != "ask")
            {
                var cb = new ComboBox
                {
                    Left = S(90), Top = y, Width = S(150),
                    DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false,
                };
                cb.Items.AddRange(new object[] { "grouped (1-1-2-2)", "sequenced (1-2-1-2)" });
                cb.SelectedIndex = panel.Collate == "sequenced" ? 1 : 0;
                cn.ValueChanged += (_, _) =>
                    cb.Enabled = batchPossible && cn.Value > 1 && panel.Collate == "choose";
                _dataPanel.Controls.Add(cb);
                _panelCollate = cb;
            }
            y += S(34);
        }

        // printer picker (etiq:panel printer="embedded"): labelprint-style
        // — "Default printer" checked keeps the machine default; unchecking
        // enables the installed-printer list
        if (panel.Printer == "embedded")
        {
            AddLabel("Printer:");
            var dflt = new CheckBox
                { Text = "Default printer", Left = S(10), Top = y, AutoSize = true, Checked = true };
            var pick = new ComboBox
            {
                Left = S(10), Top = y + S(26), Width = S(300),
                DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false,
            };
            try
            {
                foreach (string pr in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
                    pick.Items.Add(pr);
                var def = new System.Drawing.Printing.PrinterSettings();
                if (def.IsValid) pick.SelectedItem = def.PrinterName;
            }
            catch { /* spooler trouble: list stays empty */ }
            dflt.CheckedChanged += (_, _) => pick.Enabled = !dflt.Checked;
            _dataPanel.Controls.Add(dflt);
            _dataPanel.Controls.Add(pick);
            _panelPrinterDefault = dflt; _panelPrinterBox = pick;
            y += S(60);
        }

        // action buttons: which, in what order, and where — etiq:panel
        // buttons= / buttons-at= (defaults reproduce the historical set)
        void EmitButtons()
        {
            int x = S(10);
            void Btn(string text, int w, Action onClick)
            {
                if (x + S(w) > S(330)) { x = S(10); y += S(34); }   // wrap
                var b = new Button { Text = text, Left = x, Top = y + S(6), Width = S(w), Height = S(28) };
                b.Click += (_, _) => onClick();
                _dataPanel.Controls.Add(b);
                x += S(w) + S(6);
            }
            foreach (var kind in panel.Buttons)
                switch (kind)
                {
                    case "preview":
                        Btn("Refresh Preview", 110, async () =>
                            { _sourceFails.Clear(); _listRowFails.Clear(); _listRowSig.Clear(); _listRowSets.Clear(); await RefreshPreviewAsync(template); });
                        break;
                    case "print":
                        Btn(panel.Print == "direct" ? "Print" : "Print…", 90, () =>
                        {
                            if (!RefreshPreview(template)) return;   // never print a failed resolve
                            PrintNow(panel, PanelRun(panel).Copies);
                        });
                        break;
                    case "printall":
                        foreach (var l in template.Lists.Where(l => l.Rows.Count > 0))
                        {
                            var list = l;
                            Btn($"Print All: {l.Name} ({l.Rows.Count})…", 300,
                                () => PrintAllRows(template, list));
                        }
                        break;
                    case "log":
                        Btn("Log…", 70, ShowPrintLog);
                        break;
                    case "clear":
                        Btn("Clear", 90, () =>
                        {
                            _previewTimer?.Stop();   // one refresh at the end, not per box
                            foreach (var (name, tb) in _promptBoxes)
                                tb.Text = template.Fields.FirstOrDefault(f =>
                                    f.Name == name && f.Source == "prompt")?.Default ?? "";
                            // pick lists RESET (default / first row), never blank
                            foreach (var cb in _listCombos.Values) cb.Text = cb.Tag as string ?? "";
                            RefreshPreview(template);
                        });
                        break;
                }
            y += S(40);
        }
        if (panel.ButtonsAt != "top") EmitButtons();

        _dataStatus = new Label
        {
            Left = S(10), Top = y + S(10), Width = S(310), Height = S(140), AutoSize = false,
            ForeColor = SystemColors.GrayText,
        };
        _dataPanel.Controls.Add(_dataStatus);
        _dataPanel.ResumeLayout();

        RefreshPreview(template);
    }

    /// <summary>"key — Name" (prefers a column literally named Name, else the
    /// first non-key column with a value) so operators pick by name, not ID.</summary>
    private static string RowDisplay(IReadOnlyDictionary<string, string> row, string keyCol, string key)
    {
        string? name = row.FirstOrDefault(
            kv => kv.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value;
        name ??= row.FirstOrDefault(kv => kv.Key != keyCol && kv.Value != "").Value;
        return string.IsNullOrWhiteSpace(name) || name == key ? key : $"{key} — {name}";
    }

    /// <summary>Picker text for one row: the list's display= field resolved
    /// with THIS row selected (so a compose can combine any of its columns),
    /// else the key — Name heuristic.</summary>
    private string ListRowDisplay(EtiqTemplate template, EtiqTemplate.ListDef l,
                                  IReadOnlyDictionary<string, string> row, string key)
    {
        if (l.Display is { } df)
        {
            var sel = CurrentListSelections();
            sel[l.Name] = key;
            try
            {
                // remote OFF: this resolves once PER ROW while filling a
                // picker — a source fetch here would mean one HTTP call per
                // row (the multi-minute freeze); rows must render offline
                string d = new FieldResolver(template, BuildResolveContext(sel, remote: false)).Resolve(df);
                if (!string.IsNullOrWhiteSpace(d)) return d.Replace('\n', ' ');
            }
            catch (ResolveException) { /* fall through to the heuristic */ }
        }
        return RowDisplay(row, l.Key, key);
    }

    /// <summary>(Re)populate a list picker: applies the list's filter
    /// (filter-column matches the resolved filter-ref value; empty value =
    /// all rows) and the display text, keeping the current selection when
    /// it survives the filter.</summary>
    /// <summary>Signature of the row set a query-fed list needs RIGHT NOW
    /// (query + dataset + resolved target/params/filters), or null while a
    /// {Field} the query depends on is still empty. Resolution is offline
    /// (remote:false) — the inputs of a list query are prompts and other
    /// lists, never remote pulls.</summary>
    private string? ListQuerySig(EtiqTemplate template, EtiqTemplate.ListDef l,
                                 out EtiqTemplate.SourceDef? src, out string target,
                                 out Dictionary<string, string> pars, out Dictionary<string, string> fils)
    {
        src = template.Sources.FirstOrDefault(x => x.Name == l.From);
        target = ""; pars = new(); fils = new();
        if (src is null) return null;
        var resolver = new FieldResolver(template, BuildResolveContext(remote: false));
        bool ready = true;
        string Val(string raw)
        {
            if (!raw.StartsWith('{') || !raw.EndsWith('}')) return raw;
            try
            {
                string v = resolver.Resolve(raw[1..^1]);
                if (string.IsNullOrWhiteSpace(v)) ready = false;
                return v;
            }
            catch (ResolveException) { ready = false; return ""; }
        }
        target = Val(src.Baq ?? src.Query ?? "");
        pars = src.Params.ToDictionary(kv => kv.Key, kv => Val(kv.Value));
        fils = src.Filters.ToDictionary(kv => kv.Key, kv => Val(kv.Value));
        if (!ready || target == "") return null;
        return src.Name + "\x1f" + (_sessionDataset ?? "") + "\x1f" + target + "\x1f" +
            string.Join("\x1f", pars.Concat(fils).OrderBy(kv => kv.Key)
                .Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>Rows a picker should show: embedded rows, or — for a
    /// query-fed list — the cached row set for the current signature. A
    /// missing set starts ONE background fetch; the picker shows empty
    /// (status line says why) and is rebuilt when the rows land.</summary>
    private IReadOnlyList<Dictionary<string, string>> RowsFor(EtiqTemplate template, EtiqTemplate.ListDef l, ComboBox cb)
    {
        if (l.From is null) return l.Rows;
        string? sig = ListQuerySig(template, l, out var src, out var target, out var pars, out var fils);
        string? prevSig = _listRowSig.GetValueOrDefault(l.Name);
        if (prevSig is not null && prevSig != sig)
            _listRowSets.Remove(prevSig);   // live inventory: switching away and back = reload, never a stale snapshot
        _listRowSig[l.Name] = sig;
        var empty = new List<Dictionary<string, string>>();
        void Status(string text, bool error = false)
        {
            _listNotes[l.Name] = (text, error);
            if (_dataStatus is null) return;
            _dataStatus.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
            _dataStatus.Text = text;
        }
        if (src is null) { Status($"list '{l.Name}': query '{l.From}' is not declared", true); return Live(empty); }
        if (sig is null) { Status($"{l.Caption ?? l.Name}: waiting for the inputs of query '{src.Name}'"); return Live(empty); }
        if (_listRowSets.TryGetValue(sig, out var rows))
        {
            _listNotes.Remove(l.Name);   // RebuildListItems reports the row count
            return Live(rows);
        }
        // a previous failure for this signature was already reported when
        // it happened; pickers only rebuild when their inputs change, so
        // arriving here again (class switched away and back, Refresh) is a
        // deliberate retry — forget the failure and fetch again
        _listRowFails.Remove(sig);
        if (_listRowFetching.Add(sig))
        {
            string key = sig;   // non-null copy for the closures below
            Status($"Loading {l.Caption ?? l.Name}…");
            string connName = src.Connection, srcName = src.Name;
            string? ds = src.Dataset ?? ActiveDataset;
            var listName = l.Name;
            Task.Run(() =>
            {
                try
                {
                    var set = FetchListRows(connName, srcName, ds, target, pars, fils);
                    BeginInvoke(() =>
                    {
                        _listRowSets[key] = set;
                        _listRowFetching.Remove(key);
                        if (_listCombos.TryGetValue(listName, out var box) && !box.IsDisposed &&
                            _listRowSig.GetValueOrDefault(listName) == key)
                        {
                            RebuildListItems(template, l, box);   // reports the usable row count
                            _previewTimer?.Stop(); _previewTimer?.Start();   // = Touched()
                        }
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke(() =>
                    {
                        _listRowFails[key] = $"list '{listName}': {ex.Message}";
                        _listRowFetching.Remove(key);
                        Status(_listRowFails[key], true);
                    });
                }
            });
        }
        return Live(empty);

        List<Dictionary<string, string>> Live(List<Dictionary<string, string>> r)
        {
            _listRowsLive[l.Name] = r;
            return r;
        }
    }

    /// <summary>Background: every row of a declared query, as strings.
    /// Runs OFF the UI thread (Task.Run) — connection setup + a 2000-row
    /// pull can take seconds.</summary>
    private static List<Dictionary<string, string>> FetchListRows(
        string connName, string srcName, string? dataset, string target,
        Dictionary<string, string> pars, Dictionary<string, string> fils)
    {
        var conns = ConnectionsStore.Load(ConnectionsPath);
        var conn = conns.FirstOrDefault(c => c.Name.Equals(connName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"no connection named '{connName}' on this machine (File > Connections… to set it up)");
        List<Dictionary<string, System.Text.Json.JsonElement>> raw;
        if (conn.Type.Equals("epicor", StringComparison.OrdinalIgnoreCase))
        {
            using var client = new EpicorClient(conn.ToEpicorConfig(dataset));
            raw = client.FetchSourceRowsAsync(target, pars, fils).GetAwaiter().GetResult();
        }
        else if (conn.Type.Equals("glpi", StringComparison.OrdinalIgnoreCase))
        {
            if (pars.Count > 0)
                throw new InvalidOperationException($"query '{srcName}': GLPI list queries take filter-* only (param-id selects ONE item)");
            using var client = new GlpiClient(conn.ToGlpiConfig(dataset));
            raw = client.FetchItemRowsAsync(target, fils).GetAwaiter().GetResult();
        }
        else
            throw new InvalidOperationException(
                $"connection '{conn.Name}' is type '{conn.Type}' — etiq:query needs an epicor or glpi connection");
        var rows = new List<Dictionary<string, string>>(raw.Count);
        foreach (var r in raw)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in r)
                row[k] = v.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
                    _ => v.ToString(),
                };
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A resolve error prefixed with the live pickers' own status
    /// (loading / failed / empty) — the resolve error is usually just a
    /// consequence of that, and would otherwise hide it.</summary>
    private string WithListNotes(string err)
    {
        var notes = _listNotes.Values.Where(n => n.Error || n.Text.Contains("Loading")).Select(n => n.Text).ToList();
        return notes.Count == 0 ? err : string.Join("  |  ", notes) + "  —  " + err;
    }

    /// <summary>A picker must be rebuilt when its filter-ref value changed
    /// (embedded lists) or when the row set its query needs changed
    /// (query-fed lists — e.g. the item type was switched).</summary>
    private bool ListNeedsRebuild(EtiqTemplate template, EtiqTemplate.ListDef l, Dictionary<string, string> resolved)
    {
        if (l.FilterRef is { } fr && l.FilterColumn is not null &&
            resolved.GetValueOrDefault(fr) != _listFilterVal.GetValueOrDefault(l.Name))
            return true;
        if (l.From is not null)
            return ListQuerySig(template, l, out _, out _, out _, out _) != _listRowSig.GetValueOrDefault(l.Name);
        return false;
    }

    private void RebuildListItems(EtiqTemplate template, EtiqTemplate.ListDef l, ComboBox cb)
    {
        string? filterVal = null;
        if (l.FilterRef is not null && l.FilterColumn is not null)
        {
            try { filterVal = new FieldResolver(template, BuildResolveContext(remote: false)).Resolve(l.FilterRef); }
            catch (ResolveException) { filterVal = null; }   // unresolved filter = no filtering
        }
        _listFilterVal[l.Name] = filterVal;

        var rows = RowsFor(template, l, cb);
        string prevText = cb.Text;
        var map = new Dictionary<string, string>();
        _listDisplayToKey[l.Name] = map;
        cb.BeginUpdate();
        cb.Items.Clear();
        int noKey = 0;
        foreach (var row in rows)
        {
            if (!row.TryGetValue(l.Key, out var kv) || string.IsNullOrWhiteSpace(kv)) { noKey++; continue; }
            if (l.FilterColumn is not null && !string.IsNullOrEmpty(filterVal) &&
                row.GetValueOrDefault(l.FilterColumn) != filterVal) continue;
            string display = ListRowDisplay(template, l, row, kv);
            if (!map.TryAdd(display, kv))
            {
                display = $"{display} ({kv})";   // duplicate display text: disambiguate
                if (!map.TryAdd(display, kv)) continue;
            }
            cb.Items.Add(display);
        }
        cb.EndUpdate();
        if (l.From is not null && _listRowsLive.ContainsKey(l.Name) && !_listRowFetching.Contains(_listRowSig.GetValueOrDefault(l.Name) ?? ""))
        {
            // the row count is the honest status for a live picker: "0 of 12
            // usable" says "your monitors have no inventory numbers", which
            // no resolve error ever would
            string note = cb.Items.Count == 0 && rows.Count > 0
                ? $"{l.Caption ?? l.Name}: {rows.Count} row(s) fetched, none has a '{l.Key}' value — nothing to pick"
                : noKey > 0
                    ? $"{l.Caption ?? l.Name}: {cb.Items.Count} row(s) ({noKey} without '{l.Key}' skipped)"
                    : rows.Count == 0 && _listRowSets.ContainsKey(_listRowSig.GetValueOrDefault(l.Name) ?? "")
                        ? $"{l.Caption ?? l.Name}: the query returned no rows"
                        : $"{l.Caption ?? l.Name}: {cb.Items.Count} row(s)";
            bool warn = cb.Items.Count == 0;
            if (_listRowSets.ContainsKey(_listRowSig.GetValueOrDefault(l.Name) ?? ""))
                _listNotes[l.Name] = (note, warn);
        }
        // remember the "reset" position for the Clear button (default= if it
        // matches a row, else the first row) — Clear must never blank a
        // picker that has a defined set of entries
        cb.Tag = l.Default is { } dflt && map.ContainsValue(dflt)
            ? map.First(kv => kv.Value == dflt).Key
            : cb.Items.Count > 0 ? cb.Items[0]!.ToString() : "";
        if (prevText != "" && map.ContainsKey(prevText)) cb.Text = prevText;
        else if (l.Default is { } d && map.ContainsValue(d))
            cb.Text = map.First(kv => kv.Value == d).Key;
        else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        else cb.Text = "";
    }

    /// <summary>Selected key per list: the display map when the text matches
    /// a row, else whatever was typed up to the display separator (a raw
    /// key entry) — the resolver reports unknown keys cleanly.</summary>
    private Dictionary<string, string> CurrentListSelections()
    {
        var sel = new Dictionary<string, string>();
        foreach (var (name, cb) in _listCombos)
        {
            string t = cb.Text.Trim();
            if (t == "") continue;
            sel[name] = _listDisplayToKey.TryGetValue(name, out var map)
                        && map.TryGetValue(t, out var k)
                ? k
                : t.Split(" — ")[0].Trim();
        }
        return sel;
    }

    private void PopulateDatasetCombo()
    {
        if (_datasetCombo is null) return;
        var names = new List<string>();
        try
        {
            foreach (var c in ConnectionsStore.Load(ConnectionsPath))
                foreach (var ds in c.Datasets.Keys)
                    if (!names.Contains(ds, StringComparer.OrdinalIgnoreCase)) names.Add(ds);
        }
        catch { /* unreadable store: picker stays empty */ }
        string current = _datasetCombo.SelectedIndex <= 0 ? "" : _datasetCombo.Text;
        _datasetCombo.Items.Clear();
        string dflt = UpdateChecker.GetSetting("dataset") is { } md
            ? $"(default: {md})" : "(default dataset)";
        _datasetCombo.Items.Add(dflt);
        foreach (var n in names) _datasetCombo.Items.Add(n);
        _datasetCombo.SelectedIndex = current != "" && _datasetCombo.Items.Contains(current)
            ? _datasetCombo.Items.IndexOf(current) : 0;
        _datasetCombo.Visible = names.Count > 0;   // no datasets anywhere = no picker
    }

    /// <summary>Provider behind ResolveContext.SourceColumn: fetch (once
    /// per param signature) the declared source's row and return one
    /// column. Runs synchronously inside resolve — the preview is already
    /// debounced, and print wants the blocking semantics anyway.</summary>
    private string? FetchSourceColumn(EtiqTemplate template, ResolveContext ctx,
                                      IReadOnlySet<string> focusedPrompts,
                                      string sourceName, string column)
    {
        var src = template.Sources.FirstOrDefault(x => x.Name == sourceName)
            ?? throw new InvalidOperationException($"source '{sourceName}' is not declared");
        if (!_fetchingSources.TryAdd(sourceName, 0))
            throw new InvalidOperationException(
                $"source '{sourceName}' is part of a circular source-parameter chain");
        try
        {
            // param/filter values: "{Field}" resolves through the SAME
            // context (prompts, lists, even other sources); literals pass
            string Val(string raw) => raw.StartsWith('{') && raw.EndsWith('}')
                ? new FieldResolver(template, ctx).Resolve(raw[1..^1])
                : raw;
            var pars = src.Params.ToDictionary(kv => kv.Key, kv => Val(kv.Value));
            var fils = src.Filters.ToDictionary(kv => kv.Key, kv => Val(kv.Value));
            // the target itself may be field-fed: query="{ItemType}" lets one
            // template cover Computer / Monitor / Peripheral / Cable …
            string target = Val(src.Baq ?? src.Query ?? "");

            string sig = sourceName + "\x1f" + (_sessionDataset ?? "") + "\x1f" + target + "\x1f" +
                string.Join("\x1f", pars.Concat(fils).OrderBy(kv => kv.Key)
                    .Select(kv => kv.Key + "=" + kv.Value));
            if (!_sourceRows.TryGetValue(sig, out var row))
            {
                // NO pull until entry is DONE: every field-fed value must be
                // non-empty AND its prompt box must not be mid-edit
                // (focused) — otherwise each keystroke's debounce tick
                // would hit the service with partial values ("1", "12",
                // "123"…). The box's Leave handler re-runs the preview once
                // entry commits; a value already fetched (cache hit above)
                // stays visible regardless of focus.
                foreach (var raw in src.Params.Values.Concat(src.Filters.Values)
                             .Append(src.Baq ?? src.Query ?? ""))
                {
                    if (!raw.StartsWith('{') || !raw.EndsWith('}')) continue;
                    string rf = raw[1..^1];
                    if (string.IsNullOrWhiteSpace(Val(raw)))
                        throw new InvalidOperationException($"waiting for {rf}");
                    if (focusedPrompts.Contains(rf))
                        throw new InvalidOperationException($"waiting for {rf} (still typing)");
                }
                var conns = ConnectionsStore.Load(ConnectionsPath);
                var conn = conns.FirstOrDefault(c =>
                        c.Name.Equals(src.Connection, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"no connection named '{src.Connection}' on this machine " +
                        "(File > Connections… to set it up)");
                bool isEpicor = conn.Type.Equals("epicor", StringComparison.OrdinalIgnoreCase);
                bool isGlpi = conn.Type.Equals("glpi", StringComparison.OrdinalIgnoreCase);
                if (!isEpicor && !isGlpi)
                    throw new InvalidOperationException(
                        $"connection '{conn.Name}' is type '{conn.Type}' — etiq:query needs an epicor or glpi connection");
                if (_sourceFails.TryGetValue(sig, out var prevFail))
                    throw new InvalidOperationException(prevFail);   // no auto-retry storm
                string? ds = src.Dataset ?? ActiveDataset;
                try
                {
                    // Task.Run: this sync-over-async wait can land ON the UI
                    // thread (e.g. a prompt default= makes the query eligible
                    // during panel build) — without the hop, client awaits
                    // resuming on the blocked UI thread = deadlock
                    if (isEpicor)
                    {
                        row = Task.Run(async () =>
                        {
                            using var client = new EpicorClient(conn.ToEpicorConfig(ds));
                            return await client.FetchSourceRowAsync(target, pars, fils)
                                .ConfigureAwait(false);
                        }).GetAwaiter().GetResult();
                    }
                    else
                    {
                        // glpi: query= is the item type (Computer, Monitor, …);
                        // param-id or filter-<column> picks the item
                        row = Task.Run(async () =>
                        {
                            using var client = new GlpiClient(conn.ToGlpiConfig(ds));
                            return await client.FetchItemRowAsync(target, pars, fils)
                                .ConfigureAwait(false);
                        }).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _sourceFails[sig] = ex.Message;
                    throw;
                }
                _sourceRows[sig] = row;
                if (_sourceRows.Count > 64) _sourceRows.Clear(); _sourceFails.Clear();   // crude cap
            }
            if (!row.TryGetValue(column, out var v)) return null;
            return v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : v.ToString();
        }
        finally { _fetchingSources.TryRemove(sourceName, out _); }
    }

    private ResolveContext BuildResolveContext(Dictionary<string, string>? listOverride = null,
                                               bool remote = true)
    {
        string counterFile = Path.Combine(Path.GetTempPath(), "etiqedit-preview-counters.json");
        // parsed ONCE per context — the SourceColumn lambda runs per column
        var tmpl = _doc is not null && (_doc.Xml.Descendants(EtiqTemplate.Ns + "query").Any() ||
                                        _doc.Xml.Descendants(EtiqTemplate.Ns + "source").Any())
            ? EtiqTemplate.Parse(_doc.Xml.ToString()) : null;
        // captured HERE: Control.Focused must be read on the UI thread, and
        // the resolve may run on a background task
        var focused = _promptBoxes.Where(kv => kv.Value.Focused)
            .Select(kv => kv.Key).ToHashSet();
        ResolveContext ctx = null!;
        ctx = new ResolveContext
        {
            PromptValues = _promptBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text),
            ListSelections = listOverride ?? CurrentListSelections(),
            // snapshot: the resolve may run on a background task while a
            // fetch completion replaces a list's rows on the UI thread
            ListRows = ((Func<Func<string, IReadOnlyList<Dictionary<string, string>>?>>)(() =>
            {
                var snap = _listRowsLive.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Dictionary<string, string>>)kv.Value);
                return name => snap.GetValueOrDefault(name);
            }))(),
            Counters = new LocalFileCounterProvider(counterFile),   // local serials (no Epicor ctx yet)
            EpicorColumn = _ => null,
            Rest = (_, _, _) => null,
            // the lambda runs after ctx is assigned — safe self-reference
            SourceColumn = tmpl is null || !remote ? null
                : (src, col) => FetchSourceColumn(tmpl, ctx, focused, src, col),
        };
        return ctx;
    }

    private bool _previewBusy, _previewAgain;

    /// <summary>Background preview resolve: remote source fetches must
    /// never block the UI thread (connection setup alone can take seconds
    /// — the app looked FROZEN). One resolve in flight at a time; a
    /// request arriving mid-flight runs once more at the end.</summary>
    private async Task RefreshPreviewAsync(EtiqTemplate template)
    {
        if (_previewBusy) { _previewAgain = true; return; }
        _previewBusy = true;
        try
        {
            var ctx = BuildResolveContext();   // reads controls: UI thread
            bool remote = template.Sources.Count > 0;
            if (remote && _dataStatus is not null)
            {
                _dataStatus.ForeColor = SystemColors.GrayText;
                _dataStatus.Text = "Resolving…";
            }
            // expected failures come back as a VALUE, not an exception —
            // a ResolveException crossing the Task boundary made VS's
            // Just-My-Code debugger break as "user-unhandled" even though
            // it was caught
            var (resolved, err) = await Task.Run(() =>
            {
                try
                {
                    return (new FieldResolver(template, ctx).ResolveAll(), (string?)null);
                }
                catch (ResolveException ex)
                {
                    return ((Dictionary<string, string>?)null, ex.Message);
                }
            });
            // pickers rebuild BEFORE the success check: an empty query-fed
            // list makes every resolve fail ("no row selected"), and if that
            // failure skipped the rebuild the picker could never recover
            // when its inputs changed (class switched away from an empty one)
            foreach (var l in template.Lists)
                if (_listCombos.TryGetValue(l.Name, out var cb) &&
                    ListNeedsRebuild(template, l, resolved ?? new Dictionary<string, string>()))
                    RebuildListItems(template, l, cb);
            if (resolved is null)
            {
                if (_dataStatus is not null)
                {
                    _dataStatus.ForeColor = Color.Firebrick;
                    _dataStatus.Text = WithListNotes(err ?? "");
                }
                return;
            }
            _canvas.ResolvedValues = resolved;
            _canvas.Invalidate();
            foreach (var f in template.Fields)
                if (f.Source is ("epicor" or "rest") && f.Override &&
                    _promptBoxes.TryGetValue(f.Name, out var box) &&
                    resolved.TryGetValue(f.Name, out var rv))
                    box.PlaceholderText = rv == "" ? "(from source)" : rv;
            if (_dataStatus is not null)
            {
                _dataStatus.ForeColor = SystemColors.GrayText;
                _dataStatus.Text = $"Preview OK — {resolved.Count} field(s) resolved.";
            }
        }
        finally
        {
            _previewBusy = false;
            if (_previewAgain) { _previewAgain = false; _previewTimer?.Start(); }
        }
    }

    /// <summary>Resolve into the canvas preview. Errors show on the inline
    /// status line. SYNCHRONOUS — used by the PRINT path, where blocking
    /// until the data is in hand is exactly right. Returns success.</summary>
    private bool RefreshPreview(EtiqTemplate template)
    {
        try
        {
            var resolved = new FieldResolver(template, BuildResolveContext()).ResolveAll();
            _canvas.ResolvedValues = resolved;
            _canvas.Invalidate();
            // re-filter pickers whose filter-ref value changed with this edit
            foreach (var l in template.Lists)
                if (_listCombos.TryGetValue(l.Name, out var cb) && ListNeedsRebuild(template, l, resolved))
                    RebuildListItems(template, l, cb);
            if (_dataStatus is not null)
            {
                _dataStatus.ForeColor = SystemColors.GrayText;
                _dataStatus.Text = $"Preview OK — {resolved.Count} field(s) resolved.";
            }
            return true;
        }
        catch (ResolveException ex)
        {
            // same rule as the async path: pickers must be able to recover
            // from a failing resolve, or an empty list is a dead end
            foreach (var l in template.Lists)
                if (_listCombos.TryGetValue(l.Name, out var cb) &&
                    ListNeedsRebuild(template, l, new Dictionary<string, string>()))
                    RebuildListItems(template, l, cb);
            if (_dataStatus is not null)
            {
                _dataStatus.ForeColor = Color.Firebrick;
                _dataStatus.Text = WithListNotes(ex.Message);
            }
            return false;
        }
    }

    /// <summary>One label per row of the list; other lists/prompts keep the
    /// panel's current values. Rows that fail to resolve are reported and
    /// skipped after confirmation.</summary>
    private void PrintAllRows(EtiqTemplate template, EtiqTemplate.ListDef list)
    {
        if (_doc is null) return;
        var pages = new List<IReadOnlyDictionary<string, string>?>();
        var errors = new List<string>();
        foreach (var row in list.Rows)
        {
            if (!row.TryGetValue(list.Key, out var key)) continue;
            var sel = CurrentListSelections();
            sel[list.Name] = key;
            try { pages.Add(new FieldResolver(template, BuildResolveContext(sel)).ResolveAll()); }
            catch (ResolveException ex) { errors.Add($"{key}: {ex.Message}"); }
        }
        if (errors.Count > 0)
        {
            string msg = $"{errors.Count} row(s) failed to resolve and would be skipped:\n\n"
                + string.Join("\n", errors.Take(10))
                + (errors.Count > 10 ? $"\n… and {errors.Count - 10} more" : "")
                + (pages.Count > 0 ? $"\n\nContinue with the remaining {pages.Count}?" : "");
            if (pages.Count == 0) { MessageBox.Show(this, msg, "Print All"); return; }
            if (MessageBox.Show(this, msg, "Print All", MessageBoxButtons.OKCancel)
                != DialogResult.OK) return;
        }
        var panelCfg = template.Panel;
        int copies; bool grouped;
        if (panelCfg.Copies == "ask")
        {
            var opts = AskCopies(this, pages.Count, list.Name);
            if (opts is null) return;
            (copies, grouped) = opts.Value;
        }
        else
        {
            (copies, grouped) = PanelRun(panelCfg);   // embedded/fixed: no dialog
            // collate="ask": no on-form selector — ask only when the run
            // actually multiplies more than one page
            if (panelCfg.Collate == "ask" && copies > 1 && pages.Count > 1)
                grouped = MessageBox.Show(this,
                    $"Group copies together (1-1-2-2)?\n\nYes = grouped, No = sequenced (1-2-1-2).",
                    "Collation", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes;
        }
        var final = pages;
        if (copies > 1)
        {
            final = new List<IReadOnlyDictionary<string, string>?>();
            if (grouped)                    // 1-1, 2-2, 3-3
                foreach (var p in pages)
                    for (int c = 0; c < copies; c++) final.Add(p);
            else                            // 1-2-3, 1-2-3
                for (int c = 0; c < copies; c++) final.AddRange(pages);
        }
        using var measurer = new GdiTextMeasurer();
        PrintService.PrintBatch(this, _doc, final, measurer,
            direct: panelCfg.Print == "direct", printer: PanelPrinter(panelCfg));
    }

    /// <summary>Copies + arrangement for a batch. We expand copies into
    /// PAGES ourselves so the order is controllable — the driver's own
    /// Copies setting should stay at 1.</summary>
    private static (int Copies, bool Grouped)? AskCopies(IWin32Window owner, int labels, string listName)
    {
        using var f = new Form
        {
            Text = "Print All", ClientSize = new Size(330, 178),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var lbl = new Label
            { Text = $"Copies of each of the {labels} \"{listName}\" labels:", Left = 12, Top = 14, Width = 240 };
        var num = new NumericUpDown
            { Left = 254, Top = 12, Width = 60, Minimum = 1, Maximum = 999, Value = 1 };
        var seq = new RadioButton
            { Text = "Collated:  1-2-3, 1-2-3", Left = 12, Top = 46, Width = 300, Checked = true };
        var grp = new RadioButton
            { Text = "Grouped:  1-1, 2-2, 3-3", Left = 12, Top = 70, Width = 300 };
        var hint = new Label
        {
            Text = "Leave the printer dialog's own Copies at 1 — it would multiply the whole batch.",
            Left = 12, Top = 98, Width = 306, Height = 30, ForeColor = SystemColors.GrayText,
        };
        var ok = new Button { Text = "Print", DialogResult = DialogResult.OK, Left = 152, Top = 136, Width = 78 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 236, Top = 136, Width = 78 };
        void SyncOrder() { seq.Enabled = grp.Enabled = num.Value > 1; }
        num.ValueChanged += (_, _) => SyncOrder();
        SyncOrder();
        f.Controls.AddRange(new Control[] { lbl, num, seq, grp, hint, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        return ((int)num.Value, grp.Checked);
    }

    private void ShowLayerMenu(EditorLayer layer, Point at)
    {
        if (_doc is null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add(layer.Locked ? "Unlock" : "Lock", null, (_, _) =>
        {
            _doc.Undo.Push(layer.SetLocked(!layer.Locked));
            RefreshOutline(); _canvas.Invalidate();
        });
        menu.Items.Add(layer.Visible ? "Hide" : "Show", null, (_, _) =>
        {
            _doc.Undo.Push(layer.SetVisible(!layer.Visible));
            RefreshOutline(); _canvas.Invalidate();
        });
        menu.Items.Add(layer.Printed ? "Exclude from print" : "Include in print", null, (_, _) =>
        {
            _doc.Undo.Push(layer.SetPrinted(!layer.Printed));
            RefreshOutline(); _canvas.Invalidate();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rename…", null, (_, _) =>
        {
            string? name = PromptText("Rename layer", layer.Name);
            if (!string.IsNullOrWhiteSpace(name) && name != layer.Name)
            {
                _doc.Undo.Push(layer.Rename(name));
                RefreshOutline();
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        // z stack: layers paint in document order, later = on top
        menu.Items.Add("Raise (draw on top)", null, (_, _) =>
        {
            if (_doc.MoveLayer(layer, +1)) { RefreshOutline(); _canvas.Invalidate(); }
        });
        menu.Items.Add("Lower (draw underneath)", null, (_, _) =>
        {
            if (_doc.MoveLayer(layer, -1)) { RefreshOutline(); _canvas.Invalidate(); }
        });
        menu.Items.Add(new ToolStripSeparator());
        var others = _doc.Layers.Where(l => l.El != layer.El).ToList();
        var merge = new ToolStripMenuItem("Merge contents into")
            { Enabled = others.Count > 0 };
        foreach (var t in others)
        {
            var target = t;
            merge.DropDownItems.Add(target.Name, null, (_, _) =>
            {
                var contents = layer.El.Descendants()
                    .Where(EditorObject.IsEditable).Select(EditorObject.Wrap).ToList();
                if (contents.Count > 0) _doc.MoveToLayer(contents, target);
                _doc.RemoveLayer(layer);   // second undo step
                RefreshOutline();
                _canvas.Invalidate();
            });
        }
        menu.Items.Add(merge);
        menu.Items.Add("Delete Layer", null, (_, _) =>
        {
            if (_doc.Layers.Count <= 1)
            {
                MessageBox.Show(this, "A template needs at least one layer.", "Delete Layer");
                return;
            }
            int n = layer.El.Descendants().Count(EditorObject.IsEditable);
            if (n > 0 && MessageBox.Show(this,
                    $"Layer \"{layer.Name}\" contains {n} object(s) — they will be deleted with it. " +
                    "(Use \"Merge contents into\" to keep them.) Continue?",
                    "Delete Layer", MessageBoxButtons.OKCancel) != DialogResult.OK)
                return;
            _doc.RemoveLayer(layer);
            _canvas.Select(null);
            RefreshOutline();
            _canvas.Invalidate();
        });
        menu.Show(_outline, at);
    }

    /// <summary>Minimal modal text prompt (no VB InputBox dependency).</summary>
    private string? PromptText(string title, string initial) =>
        Prompts.PromptText(this, title, initial);

}

internal static class MenuExt
{
    /// <summary>Fluent shortcut assignment for ToolStripMenuItem adds.</summary>
    public static void ShortcutKeys(this ToolStripItem item, Keys keys)
    {
        if (item is ToolStripMenuItem mi) mi.ShortcutKeys = keys;
    }
}

/// <summary>File → New: label size in inches or millimeters.</summary>
public sealed class NewLabelDialog : Form
{
    private readonly NumericUpDown _w = new()
        { Minimum = 0.1m, Maximum = 20, DecimalPlaces = 2, Value = 4, Increment = 0.25m };
    private readonly NumericUpDown _h = new()
        { Minimum = 0.1m, Maximum = 20, DecimalPlaces = 2, Value = 2, Increment = 0.25m };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    public NewLabelDialog() : this(4, 2, "in", resize: false) { }

    /// <summary>resize=true: "Label Size" flavor — pre-filled with the current
    /// size, Apply instead of Create.</summary>
    public NewLabelDialog(double width, double height, string unit, bool resize)
    {
        Text = resize ? "Label Size" : "New Label";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(260, 150);
        Ui.AutoScale(this);

        _unit.Items.AddRange(new object[] { "in", "mm" });
        _unit.SelectedIndex = 0;
        string prevUnit = "in";
        _unit.SelectedIndexChanged += (_, _) =>
        {
            bool mm = Unit == "mm";
            if (Unit == prevUnit) return;
            // CONVERT the entered size, never reset it
            decimal f = mm ? 25.4m : 1m / 25.4m;
            decimal w = Math.Round(_w.Value * f, 2), h = Math.Round(_h.Value * f, 2);
            _w.Maximum = mm ? 500 : 20; _h.Maximum = mm ? 500 : 20;
            _w.Increment = mm ? 1 : 0.25m; _h.Increment = mm ? 1 : 0.25m;
            _w.Value = Math.Clamp(w, _w.Minimum, _w.Maximum);
            _h.Value = Math.Clamp(h, _h.Minimum, _h.Maximum);
            prevUnit = Unit;
        };

        Controls.Add(new Label { Text = "Width:", Left = 12, Top = 15, AutoSize = true });
        _w.SetBounds(80, 12, 90, 24); Controls.Add(_w);
        Controls.Add(new Label { Text = "Height:", Left = 12, Top = 47, AutoSize = true });
        _h.SetBounds(80, 44, 90, 24); Controls.Add(_h);
        Controls.Add(new Label { Text = "Units:", Left = 12, Top = 79, AutoSize = true });
        _unit.SetBounds(80, 76, 90, 24); Controls.Add(_unit);

        if (unit == "mm")
        {
            _w.Maximum = 500; _h.Maximum = 500; _w.Increment = 1; _h.Increment = 1;
            prevUnit = "mm";
            _unit.SelectedItem = "mm";   // handler sees Unit == prevUnit → no conversion
        }
        _w.Value = (decimal)Math.Clamp(width, (double)_w.Minimum, (double)_w.Maximum);
        _h.Value = (decimal)Math.Clamp(height, (double)_h.Minimum, (double)_h.Maximum);

        var ok = new Button { Text = resize ? "Apply" : "Create", DialogResult = DialogResult.OK };
        ok.SetBounds(80, 112, 80, 28); Controls.Add(ok);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(168, 112, 80, 28); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    private string Unit => (string)_unit.SelectedItem!;
    private double Wv => (double)_w.Value;
    private double Hv => (double)_h.Value;

    public string WidthAttr => $"{Wv:0.###}{Unit}";
    public string HeightAttr => $"{Hv:0.###}{Unit}";
    public int WidthMils => (int)Math.Round(Unit == "mm" ? Wv * 1000 / 25.4 : Wv * 1000);
    public int HeightMils => (int)Math.Round(Unit == "mm" ? Hv * 1000 / 25.4 : Hv * 1000);
}
