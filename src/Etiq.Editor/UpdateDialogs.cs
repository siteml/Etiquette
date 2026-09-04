namespace Etiq.Editor;

/// <summary>What the user chose on the update-available dialog.</summary>
public enum UpdateChoice { Install, SkipVersion, Later }

/// <summary>
/// Update UI: the update-available window shows the repo's CHANGELOG.md
/// (fetched from GitHub) converted to HTML and rendered in the built-in
/// WinForms WebBrowser control with GitHub-style CSS — no NuGet, no
/// external engine. Links open in the system browser. Buttons: Install /
/// Skip this version / Remind me later. The Options dialog exposes the
/// update behavior settings so any choice can be changed later.
/// </summary>
public static class UpdateDialogs
{
    /// <summary>Update-available dialog with the rendered changelog.</summary>
    public static UpdateChoice ShowChangelog(IWin32Window owner, string tag,
                                             string currentVer, string? changelogMd)
    {
        using var f = new Form
        {
            Text = $"Update available — {tag}",
            ClientSize = new Size(620, 520), MinimumSize = new Size(460, 340),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var head = new Label
        {
            Text = $"Version {tag} is available — you have v{currentVer}. What's new:",
            Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 8, 10, 0),
        };
        var web = new WebBrowser
        {
            Dock = DockStyle.Fill,
            AllowWebBrowserDrop = false,
            IsWebBrowserContextMenuEnabled = false,
            WebBrowserShortcutsEnabled = false,
            ScriptErrorsSuppressed = true,
        };
        // any real navigation (a link click) opens the SYSTEM browser;
        // only the initial DocumentText load (about:blank) renders inline
        web.Navigating += (_, e) =>
        {
            if (e.Url is { } u && u.Scheme is "http" or "https")
            {
                e.Cancel = true;
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
                }
                catch { /* no browser: ignore */ }
            }
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Height = 44, Padding = new Padding(8, 7, 8, 7),
        };
        var install = new Button { Text = "Install now", AutoSize = true, DialogResult = DialogResult.OK };
        var later = new Button { Text = "Remind me later", AutoSize = true, DialogResult = DialogResult.Cancel };
        var skip = new Button { Text = "Skip this version", AutoSize = true, DialogResult = DialogResult.Ignore };
        buttons.Controls.AddRange(new Control[] { install, later, skip });
        // Fill first, then the edges (dock runs in reverse add order)
        f.Controls.Add(web);
        f.Controls.Add(head);
        f.Controls.Add(buttons);
        f.AcceptButton = install;
        f.CancelButton = later;
        web.DocumentText = MarkdownHtml.Render(changelogMd ??
            "*The changelog could not be loaded — the release page has the details.*");
        return f.ShowDialog(owner) switch
        {
            DialogResult.OK => UpdateChoice.Install,
            DialogResult.Ignore => UpdateChoice.SkipVersion,
            _ => UpdateChoice.Later,
        };
    }

    /// <summary>Options dialog: startup check on/off, download flavor,
    /// and clearing a skipped release. Persists via UpdateChecker.</summary>
    public static void ShowOptions(IWin32Window owner)
    {
        using var f = new Form
        {
            Text = "Options", ClientSize = new Size(450, 276),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var auto = new CheckBox
        {
            Text = "Check for updates when the editor starts",
            Checked = UpdateChecker.AutoCheck, Left = 14, Top = 14, Width = 400,
        };
        var flavorLbl = new Label { Text = "Update download:", Left = 14, Top = 50, Width = 130 };
        var flavor = new ComboBox
        {
            Left = 150, Top = 46, Width = 264, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        flavor.Items.AddRange(new object[]
        {
            "auto (match this install)",
            "standalone (no dependencies, larger)",
            "framework (needs .NET 8 Desktop Runtime, small)",
        });
        flavor.SelectedIndex = UpdateChecker.UpdateFlavor switch
        { "standalone" => 1, "framework" => 2, _ => 0 };

        string? skipped = UpdateChecker.SkipVersion;
        var skipLbl = new Label
        {
            Text = skipped is null ? "No release is being skipped."
                                   : $"Release v{skipped} is being skipped.",
            Left = 14, Top = 88, Width = 270,
        };
        var clear = new Button
        {
            Text = "Stop skipping", Left = 290, Top = 84, Width = 124,
            Enabled = skipped is not null,
        };
        clear.Click += (_, _) =>
        {
            UpdateChecker.SkipVersion = null;
            skipLbl.Text = "No release is being skipped.";
            clear.Enabled = false;
        };
        var recentLbl = new Label { Text = "Recent files kept:", Left = 14, Top = 126, Width = 130 };
        var recentNum = new NumericUpDown
        {
            Left = 150, Top = 122, Width = 70, Minimum = 1, Maximum = 30,
            Value = MainForm.RecentMax,
        };
        // print offset, PER PRINTER: nudge that printer's output by (x, y)
        // mils — a driver that mis-reports its hard margin puts the image a
        // hair off; the correction belongs to the printer, not the machine
        var offLbl = new Label { Text = "Print offset for:", Left = 14, Top = 162, Width = 130 };
        var offPrinter = new ComboBox
        {
            Left = 150, Top = 158, Width = 264, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (string pn in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            offPrinter.Items.Add(pn);
        var offX = new NumericUpDown
        {
            Left = 150, Top = 190, Width = 70, Minimum = -500, Maximum = 500,
        };
        var offXl = new Label { Text = "→ right (mils)", Left = 224, Top = 194, Width = 90 };
        var offY = new NumericUpDown
        {
            Left = 316, Top = 190, Width = 70, Minimum = -500, Maximum = 500,
        };
        var offYl = new Label { Text = "↓ down", Left = 390, Top = 194, Width = 50 };
        var edited = new Dictionary<string, (int X, int Y)>();   // pending per-printer edits
        string? curPrinter = null;
        void LoadOffset()
        {
            curPrinter = offPrinter.SelectedItem as string;
            bool any = curPrinter is not null;
            offX.Enabled = offY.Enabled = any;
            if (!any) return;
            var (x, y) = edited.TryGetValue(curPrinter!, out var e) ? e : PrintService.GetOffset(curPrinter!);
            offX.Value = Math.Clamp(x, -500, 500);
            offY.Value = Math.Clamp(y, -500, 500);
        }
        void StoreOffset()
        {
            if (curPrinter is not null) edited[curPrinter] = ((int)offX.Value, (int)offY.Value);
        }
        offPrinter.SelectedIndexChanged += (_, _) => LoadOffset();
        offX.ValueChanged += (_, _) => StoreOffset();
        offY.ValueChanged += (_, _) => StoreOffset();
        try
        {
            string def = new System.Drawing.Printing.PrinterSettings().PrinterName;
            offPrinter.SelectedItem = offPrinter.Items.Contains(def) ? def
                                    : offPrinter.Items.Count > 0 ? offPrinter.Items[0] : null;
        }
        catch { if (offPrinter.Items.Count > 0) offPrinter.SelectedIndex = 0; }
        LoadOffset();
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 246, Top = 236, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 334, Top = 236, Width = 80 };
        f.Controls.AddRange(new Control[]
            { auto, flavorLbl, flavor, skipLbl, clear, recentLbl, recentNum,
              offLbl, offPrinter, offX, offXl, offY, offYl, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) == DialogResult.OK)
        {
            UpdateChecker.AutoCheck = auto.Checked;
            UpdateChecker.UpdateFlavor = flavor.SelectedIndex switch
            { 1 => "standalone", 2 => "framework", _ => "auto" };
            UpdateChecker.SetSetting("recentMax", ((int)recentNum.Value).ToString());
            foreach (var (pn, (x, y)) in edited) PrintService.SetOffset(pn, x, y);
        }
    }
}

/// <summary>
/// Small dependency-free markdown → HTML converter for the changelog
/// viewer: headings, unordered lists (one nesting level), fenced and
/// inline code, bold, italic, links, horizontal rules, paragraphs.
/// Everything is HTML-escaped first, so arbitrary changelog content can
/// never inject markup. Not a general renderer — just enough to make a
/// Keep-a-Changelog file look the way GitHub renders it.
/// </summary>
internal static class MarkdownHtml
{
    private const string Css = """
        body { font-family: 'Segoe UI', sans-serif; font-size: 13px;
               color: #1f2328; margin: 14px 18px; line-height: 1.55; }
        h1 { font-size: 20px; border-bottom: 1px solid #d8dee4;
             padding-bottom: 6px; margin: 10px 0 8px; }
        h2 { font-size: 16px; border-bottom: 1px solid #eaeef1;
             padding-bottom: 4px; margin: 18px 0 6px; }
        h3 { font-size: 14px; margin: 12px 0 4px; }
        ul { margin: 4px 0 8px; padding-left: 26px; }
        li { margin: 2px 0; }
        p  { margin: 6px 0; }
        code { font-family: Consolas, monospace; font-size: 12px;
               background: #f0f2f4; border-radius: 4px; padding: 1px 5px; }
        pre { background: #f0f2f4; border-radius: 6px; padding: 8px 10px; }
        pre code { background: none; padding: 0; }
        a { color: #0a69da; text-decoration: none; }
        a:hover { text-decoration: underline; }
        hr { border: none; border-top: 1px solid #d8dee4; margin: 12px 0; }
        em { color: #57606a; }
        """;

    public static string Render(string md)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>");
        sb.Append("<meta charset=\"utf-8\"/><style>").Append(Css).Append("</style></head><body>");

        var lines = md.Replace("\r\n", "\n").Split('\n');
        int listDepth = 0;          // 0 = not in a list, 1-2 = <ul> depth
        bool liOpen = false;        // current <li> stays open for continuations
        bool inPre = false;
        var para = new System.Text.StringBuilder();

        void CloseLi()
        {
            if (liOpen) { sb.Append("</li>"); liOpen = false; }
        }
        void CloseLists(int to)
        {
            CloseLi();
            while (listDepth > to) { sb.Append("</ul>"); listDepth--; }
        }
        void FlushPara()
        {
            if (para.Length == 0) return;
            sb.Append("<p>").Append(Inline(para.ToString())).Append("</p>");
            para.Clear();
        }

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            if (inPre)
            {
                if (line.TrimStart().StartsWith("```")) { sb.Append("</code></pre>"); inPre = false; }
                else sb.Append(Escape(raw)).Append('\n');
                continue;
            }
            string t = line.TrimStart();
            int indent = line.Length - t.Length;

            if (t.StartsWith("```"))
            { FlushPara(); CloseLists(0); sb.Append("<pre><code>"); inPre = true; }
            else if (t.StartsWith("### "))
            { FlushPara(); CloseLists(0); sb.Append("<h3>").Append(Inline(t[4..])).Append("</h3>"); }
            else if (t.StartsWith("## "))
            { FlushPara(); CloseLists(0); sb.Append("<h2>").Append(Inline(t[3..])).Append("</h2>"); }
            else if (t.StartsWith("# "))
            { FlushPara(); CloseLists(0); sb.Append("<h1>").Append(Inline(t[2..])).Append("</h1>"); }
            else if (t.StartsWith("---") && t.TrimEnd('-').Length == 0)
            { FlushPara(); CloseLists(0); sb.Append("<hr/>"); }
            else if (t.StartsWith("- ") || t.StartsWith("* "))
            {
                FlushPara();
                int depth = indent >= 2 ? 2 : 1;
                CloseLi();
                while (listDepth > depth) { sb.Append("</ul>"); listDepth--; }
                while (listDepth < depth) { sb.Append("<ul>"); listDepth++; }
                sb.Append("<li>").Append(Inline(t[2..]));
                liOpen = true;
            }
            else if (t.Length == 0)
            { FlushPara(); CloseLists(0); }
            else if (listDepth > 0 && indent >= 2)
            {
                // continuation line of the previous bullet
                sb.Append(' ').Append(Inline(t));
            }
            else
            {
                CloseLists(0);
                if (para.Length > 0) para.Append(' ');
                para.Append(t);
            }
        }
        if (inPre) sb.Append("</code></pre>");
        FlushPara();
        CloseLists(0);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Inline spans over ESCAPED text: `code`, **bold**,
    /// *italic*, and [text](url) links (http/https only).</summary>
    private static string Inline(string s)
    {
        s = Escape(s);
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"`([^`]+)`", "<code>$1</code>");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"\*\*([^*]+)\*\*", "<b>$1</b>");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"(?<![\w*])\*([^*\s][^*]*)\*(?!\*)", "<em>$1</em>");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "<a href=\"$2\">$1</a>");
        return s;
    }
}
