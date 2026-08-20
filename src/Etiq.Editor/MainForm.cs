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
    private readonly Dictionary<string, TextBox> _promptBoxes = new();
    private readonly Dictionary<string, ComboBox> _listCombos = new();
    // list row picker: display string ("key — Name") → key value, per list
    private readonly Dictionary<string, Dictionary<string, string>> _listDisplayToKey = new();
    private readonly Dictionary<string, string?> _listFilterVal = new(); // last applied filter
    private Label? _dataStatus;                       // inline resolve status
    private System.Windows.Forms.Timer? _previewTimer; // debounced auto-preview

    public MainForm(string? openPath)
    {
        Text = "Etiquette Designer";
        Width = 1280; Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // window/taskbar icon = the exe's own icon (assets/etiq.ico via
        // <ApplicationIcon>); never fatal if extraction fails
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* default icon */ }

        // Dock layout runs in REVERSE Controls order: the Fill split must
        // be added first and the MenuStrip last, or the menu overlaps the
        // top of the panels (and renders under the toolbar).
        BuildLayout();
        BuildMenu();

        if (openPath is not null && File.Exists(openPath)) OpenFile(openPath);

        // silent startup update check, only when the build ships a repo
        Shown += async (_, _) =>
        {
            if (UpdateChecker.Configured)
                await CheckForUpdates(interactive: false);
        };
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

        string? assetUrl = wantStandalone ? rel.StandaloneUrl : rel.FrameworkUrl;
        string? assetName = wantStandalone ? rel.StandaloneName : rel.FrameworkName;

        // in-place install needs a direct asset AND a writable install
        // folder; otherwise it's a browser download like before
        bool inPlace = assetUrl is not null && UpdateApplier.CanSelfUpdate;
        string offer = inPlace
            ? $"Install {assetName} now?\n\netiqedit restarts when it finishes."
            : assetUrl is not null
                ? $"Download {assetName}?\n\n(The install folder isn't writable, so the update can't be applied automatically.)"
                : "Open the download page?";
        if (MessageBox.Show(this,
                $"Version {rel.Tag} is available (you have v{UpdateChecker.Current.ToString(3)}).\n\n" + offer,
                "Update available", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        if (!inPlace)
        {
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
                await OfferUpdate(rel);
            }
            else if (interactive)
            {
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
        right.Controls.Add(_dataPanel);

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
        _split1.Panel1.Controls.Add(_outline);
        _split1.Panel2.Controls.Add(_split2);

        Controls.Add(_split1);

        Shown += (_, _) =>
        {
            try
            {
                _split1.Panel1MinSize = 180;
                _split1.SplitterDistance = 260;                     // outline tree
                _split2.Panel2MinSize = 260;
                _split2.SplitterDistance = Math.Max(300, _split2.Width - 340); // property panel
            }
            catch (ArgumentOutOfRangeException) { /* tiny window: keep defaults */ }
            _canvas.FitToWindow();
        };

        var tools = new ToolStrip();
        _modeButton.CheckedChanged += (_, _) => SetMode(_modeButton.Checked ? EditorMode.Data : EditorMode.Design);
        tools.Items.Add(_modeButton);
        tools.Items.Add(new ToolStripSeparator());
        var fit = new ToolStripButton("Fit")
        {
            ToolTipText = "Zoom the label to fit the window " +
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
        };
        _canvas.CursorWorldMoved += p =>
            _statusPos.Text = $"x: {p.X:0} mils  y: {p.Y:0} mils";
        _outline.AfterSelect += (_, e) =>
        {
            if (_syncingOutline) return; // we moved the highlight, not the user
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
        _props.Changed += () => { _canvas.Invalidate(); RefreshOutline(); };
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&New…", null, (_, _) => FileNew()).ShortcutKeys(Keys.Control | Keys.N);
        file.DropDownItems.Add("&Open…", null, (_, _) => OpenDialog()).ShortcutKeys(Keys.Control | Keys.O);
        file.DropDownItems.Add("&Save", null, (_, _) => SaveFile(null)).ShortcutKeys(Keys.Control | Keys.S);
        file.DropDownItems.Add("Save &As…", null, (_, _) => SaveAsDialog());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Print…", null, (_, _) => PrintNow()).ShortcutKeys(Keys.Control | Keys.P);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Validate", null, (_, _) => ShowValidation()).ShortcutKeys(Keys.F7);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add("&Undo", null, (_, _) => { _doc?.Undo.Undo(); RefreshOutline(); }).ShortcutKeys(Keys.Control | Keys.Z);
        edit.DropDownItems.Add("&Redo", null, (_, _) => { _doc?.Undo.Redo(); RefreshOutline(); }).ShortcutKeys(Keys.Control | Keys.Y);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("&Group", null, (_, _) => GroupSelection()).ShortcutKeys(Keys.Control | Keys.G);
        edit.DropDownItems.Add("U&ngroup", null, (_, _) => UngroupSelection()).ShortcutKeys(Keys.Control | Keys.Shift | Keys.G);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("&Fields, Maps && Lists…", null, (_, _) => ShowMetadataDialog()).ShortcutKeys(Keys.F4);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("Bring &Forward", null, (_, _) => Reorder(true)).ShortcutKeys(Keys.Control | Keys.Oemplus);
        edit.DropDownItems.Add("Send &Backward", null, (_, _) => Reorder(false)).ShortcutKeys(Keys.Control | Keys.OemMinus);

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

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add("&Design Mode", null, (_, _) => _modeButton.Checked = false);
        view.DropDownItems.Add("Da&ta Mode", null, (_, _) => _modeButton.Checked = true);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Check for &Updates…", null, async (_, _) => await CheckForUpdates(interactive: true));
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

    private void FileNew()
    {
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
        _canvas.Doc = _doc;
        _doc.Undo.Changed += OutlineMaybeRefresh; // deletes/undo/redo update the tree
        RefreshDeclaredFieldNames();
        _statusDoc.Text = "(unsaved new label)";
        RefreshOutline();
        if (_modeButton.Checked) _modeButton.Checked = false;   // new docs open in Design
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
                    N(symbology is "qr" or "iqr" or "datamatrix" ? 800 : 1500)),
                new System.Xml.Linq.XAttribute("height",
                    N(symbology is "qr" or "iqr" or "datamatrix" ? 800 : 500)),
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

    private void OpenDialog()
    {
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
        _canvas.Doc = _doc;
        _doc.Undo.Changed += OutlineMaybeRefresh; // deletes/undo/redo update the tree
        RefreshDeclaredFieldNames();
        _statusDoc.Text = Path.GetFileName(path);
        RefreshOutline();
        if (_modeButton.Checked) BuildDataPanel();
    }

    private void SaveFile(string? path)
    {
        if (_doc is null) return;
        if (path is null && _doc.Path is null) { SaveAsDialog(); return; }   // new unsaved doc
        try { _doc.Save(path); _statusDoc.Text = Path.GetFileName(_doc.Path!); }
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
        });
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.HasUnappliedChanges)                          // OK after Apply = no-op skip
        {
            _doc.Undo.Push(_doc.ReplaceEtiqLabel(dlg.Result));
            AfterInstall();
        }
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
    private void OutlineMaybeRefresh()
    {
        if (OutlineSignature() != _outlineSig) RefreshOutline();
        _props.RefreshValues(); // keep X/Y/rotation live during drags and undo/redo
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

    private void SetMode(EditorMode mode)
    {
        _canvas.Mode = mode;
        _modeButton.Text = mode == EditorMode.Design ? "Mode: DESIGN" : "Mode: DATA";
        _props.Visible = mode == EditorMode.Design;
        _dataPanel.Visible = mode == EditorMode.Data;
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
        _dataPanel.Controls.Clear();
        _promptBoxes.Clear();
        _listCombos.Clear();
        _listDisplayToKey.Clear();
        if (_doc is null) return;

        var template = EtiqTemplate.Parse(_doc.Xml.ToString());

        // debounced auto-preview: any edit re-resolves ~350ms after the last
        // keystroke; errors go to the inline status line, never a popup
        _previewTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _previewTimer.Tick += (_, _) => { _previewTimer!.Stop(); RefreshPreview(template); };
        void Touched() { _previewTimer!.Stop(); _previewTimer.Start(); }

        int y = 12;
        void AddLabel(string text)
        {
            _dataPanel.Controls.Add(new Label
                { Text = text, Left = 10, Top = y, Width = 320, AutoSize = false });
            y += 20;
        }

        AddLabel("DATA MODE — layout locked");
        y += 8;
        void EmitPrompt(EtiqTemplate.Field f)
        {
            AddLabel(f.Caption ?? f.Name + ":");
            // casing follows the field's declared case= (never assumed);
            // the resolver normalizes regardless, this just mirrors it live
            var tb = new TextBox
            {
                Left = 10, Top = y, Width = 300,
                CharacterCasing = f.Case switch
                {
                    "upper" => CharacterCasing.Upper,
                    "lower" => CharacterCasing.Lower,
                    _ => CharacterCasing.Normal,   // normal/title/absent; resolver
                                                   // normalizes title at resolve time
                },
            };
            tb.TextChanged += (_, _) => Touched();
            _promptBoxes[f.Name] = tb;
            _dataPanel.Controls.Add(tb);
            y += 34;
        }
        // embedded pick lists: one shared selection per list drives every
        // field bound to it (the "set" behavior); the box is type-to-search
        void EmitList(EtiqTemplate.ListDef l)
        {
            AddLabel(l.Caption ?? l.Name + ":");
            var cb = new ComboBox
            {
                Left = 10, Top = y, Width = 300,
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            _listCombos[l.Name] = cb;
            RebuildListItems(template, l, cb);
            cb.SelectedIndexChanged += (_, _) => Touched();
            cb.TextChanged += (_, _) => Touched();
            _dataPanel.Controls.Add(cb);
            y += 34;
        }

        // panel order = FIELD DECLARATION ORDER (the F4 Fields tab): a
        // prompt appears where declared; a list appears where its first
        // bound field is declared; unreferenced lists follow at the end
        var emittedLists = new HashSet<string>();
        var listsByName = template.Lists.ToDictionary(l => l.Name);
        foreach (var f in template.Fields)
        {
            if (f.Source == "prompt") EmitPrompt(f);
            else if (f.Source == "list" && f.ListRef is { } lr &&
                     emittedLists.Add(lr) && listsByName.TryGetValue(lr, out var l))
                EmitList(l);
        }
        foreach (var l in template.Lists.Where(l => emittedLists.Add(l.Name)))
            EmitList(l);

        var preview = new Button { Text = "Refresh Preview", Left = 10, Top = y + 6, Width = 145 };
        preview.Click += (_, _) => RefreshPreview(template);
        _dataPanel.Controls.Add(preview);

        var print = new Button { Text = "Print…", Left = 165, Top = y + 6, Width = 145 };
        print.Click += (_, _) =>
        {
            if (!RefreshPreview(template)) return;   // never print a failed resolve
            PrintNow();
        };
        _dataPanel.Controls.Add(print);
        y += 36;

        // batch path: one label per row of a list (the print-station run)
        foreach (var l in template.Lists.Where(l => l.Rows.Count > 0))
        {
            var all = new Button
                { Text = $"Print All: {l.Name} ({l.Rows.Count})…", Left = 10, Top = y + 6, Width = 300 };
            var list = l;
            all.Click += (_, _) => PrintAllRows(template, list);
            _dataPanel.Controls.Add(all);
            y += 36;
        }

        _dataStatus = new Label
        {
            Left = 10, Top = y + 10, Width = 310, Height = 64, AutoSize = false,
            ForeColor = SystemColors.GrayText,
        };
        _dataPanel.Controls.Add(_dataStatus);

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
                string d = new FieldResolver(template, BuildResolveContext(sel)).Resolve(df);
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
    private void RebuildListItems(EtiqTemplate template, EtiqTemplate.ListDef l, ComboBox cb)
    {
        string? filterVal = null;
        if (l.FilterRef is not null && l.FilterColumn is not null)
        {
            try { filterVal = new FieldResolver(template, BuildResolveContext()).Resolve(l.FilterRef); }
            catch (ResolveException) { filterVal = null; }   // unresolved filter = no filtering
        }
        _listFilterVal[l.Name] = filterVal;

        string prevText = cb.Text;
        var map = new Dictionary<string, string>();
        _listDisplayToKey[l.Name] = map;
        cb.BeginUpdate();
        cb.Items.Clear();
        foreach (var row in l.Rows)
        {
            if (!row.TryGetValue(l.Key, out var kv)) continue;
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

    private ResolveContext BuildResolveContext(Dictionary<string, string>? listOverride = null)
    {
        string counterFile = Path.Combine(Path.GetTempPath(), "etiqedit-preview-counters.json");
        return new ResolveContext
        {
            PromptValues = _promptBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text),
            ListSelections = listOverride ?? CurrentListSelections(),
            Counters = new LocalFileCounterProvider(counterFile),   // local serials (no Epicor ctx yet)
            EpicorColumn = _ => null,
            Rest = (_, _, _) => null,
        };
    }

    /// <summary>Resolve into the canvas preview. Errors show on the inline
    /// status line (this runs on every keystroke). Returns success.</summary>
    private bool RefreshPreview(EtiqTemplate template)
    {
        try
        {
            var resolved = new FieldResolver(template, BuildResolveContext()).ResolveAll();
            _canvas.ResolvedValues = resolved;
            _canvas.Invalidate();
            // re-filter pickers whose filter-ref value changed with this edit
            foreach (var l in template.Lists)
                if (l.FilterRef is { } fr && l.FilterColumn is not null &&
                    _listCombos.TryGetValue(l.Name, out var cb) &&
                    resolved.GetValueOrDefault(fr) != _listFilterVal.GetValueOrDefault(l.Name))
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
            if (_dataStatus is not null)
            {
                _dataStatus.ForeColor = Color.Firebrick;
                _dataStatus.Text = ex.Message;
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
        var opts = AskCopies(this, pages.Count, list.Name);
        if (opts is null) return;
        var (copies, grouped) = opts.Value;
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
        PrintService.PrintBatch(this, _doc, final, measurer);
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

    public NewLabelDialog()
    {
        Text = "New Label";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(260, 150);

        _unit.Items.AddRange(new object[] { "in", "mm" });
        _unit.SelectedIndex = 0;
        _unit.SelectedIndexChanged += (_, _) =>
        {
            bool mm = Unit == "mm";
            _w.Maximum = mm ? 500 : 20; _h.Maximum = mm ? 500 : 20;
            _w.Value = mm ? 100 : 4; _h.Value = mm ? 50 : 2;
            _w.Increment = mm ? 1 : 0.25m; _h.Increment = mm ? 1 : 0.25m;
        };

        Controls.Add(new Label { Text = "Width:", Left = 12, Top = 15, Width = 60 });
        _w.SetBounds(80, 12, 90, 24); Controls.Add(_w);
        Controls.Add(new Label { Text = "Height:", Left = 12, Top = 47, Width = 60 });
        _h.SetBounds(80, 44, 90, 24); Controls.Add(_h);
        Controls.Add(new Label { Text = "Units:", Left = 12, Top = 79, Width = 60 });
        _unit.SetBounds(80, 76, 90, 24); Controls.Add(_unit);

        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK };
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
