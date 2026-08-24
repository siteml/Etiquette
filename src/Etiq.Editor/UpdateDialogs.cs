namespace Etiq.Editor;

/// <summary>What the user chose on the update-available dialog.</summary>
public enum UpdateChoice { Install, SkipVersion, Later }

/// <summary>
/// Update UI: the update-available window renders the repo's CHANGELOG.md
/// (fetched from GitHub) with a small dependency-free markdown-lite
/// renderer into a RichTextBox — headings, bullets, **bold**, `code` —
/// with Install / Skip this version / Later. The Options dialog exposes
/// the update behavior settings (startup check, flavor, skipped release)
/// so any choice can be changed later.
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
            ClientSize = new Size(580, 480), MinimumSize = new Size(440, 320),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        var head = new Label
        {
            Text = $"Version {tag} is available — you have v{currentVer}. What's new:",
            Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 8, 10, 0),
        };
        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window, DetectUrls = true,
        };
        rtb.LinkClicked += (_, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(e.LinkText!) { UseShellExecute = true });
            }
            catch { /* no browser: ignore */ }
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
        f.Controls.Add(rtb);
        f.Controls.Add(head);
        f.Controls.Add(buttons);
        f.AcceptButton = install;
        f.CancelButton = later;
        RenderMarkdown(rtb, changelogMd ??
            "*The changelog could not be loaded — the release page has the details.*");
        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
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
            Text = "Options", ClientSize = new Size(430, 200),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
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
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 246, Top = 160, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 334, Top = 160, Width = 80 };
        f.Controls.AddRange(new Control[] { auto, flavorLbl, flavor, skipLbl, clear, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) == DialogResult.OK)
        {
            UpdateChecker.AutoCheck = auto.Checked;
            UpdateChecker.UpdateFlavor = flavor.SelectedIndex switch
            { 1 => "standalone", 2 => "framework", _ => "auto" };
        }
    }

    // ---------- markdown-lite → RichTextBox ----------
    // Line-based: #/##/### headings, "- " bullets, --- rules; inline
    // **bold** and `code`. Links stay plain text (DetectUrls makes bare
    // URLs clickable). Good enough for a changelog; never throws.

    private static void RenderMarkdown(RichTextBox rtb, string md)
    {
        var body = rtb.Font;
        var bold = new Font(body, FontStyle.Bold);
        var italic = new Font(body, FontStyle.Italic);
        var h1 = new Font(body.FontFamily, body.Size + 4, FontStyle.Bold);
        var h2 = new Font(body.FontFamily, body.Size + 2, FontStyle.Bold);
        var code = new Font(FontFamily.GenericMonospace, body.Size);

        void Append(string text, Font font)
        {
            rtb.SelectionFont = font;
            rtb.AppendText(text);
        }
        void AppendInline(string line, Font baseFont)
        {
            int i = 0;
            var cur = new System.Text.StringBuilder();
            bool inBold = false, inCode = false;
            void Flush()
            {
                if (cur.Length == 0) return;
                Append(cur.ToString(), inCode ? code : inBold ? bold : baseFont);
                cur.Clear();
            }
            while (i < line.Length)
            {
                if (!inCode && i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                {
                    Flush();
                    inBold = !inBold;
                    i += 2;
                }
                else if (line[i] == '`')
                {
                    Flush();
                    inCode = !inCode;
                    i++;
                }
                else
                {
                    cur.Append(line[i]);
                    i++;
                }
            }
            Flush();
        }

        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("### "))
            { Append(line[4..] + "\n", bold); }
            else if (line.StartsWith("## "))
            { Append("\n", body); Append(line[3..] + "\n", h2); }
            else if (line.StartsWith("# "))
            { Append(line[2..] + "\n", h1); }
            else if (line.StartsWith("---"))
            { Append(new string('—', 20) + "\n", body); }
            else if (line.TrimStart().StartsWith("- "))
            {
                string indent = line[..(line.Length - line.TrimStart().Length)];
                Append(indent + "  •  ", body);
                AppendInline(line.TrimStart()[2..], body);
                Append("\n", body);
            }
            else if (line.TrimStart().StartsWith("* ") && line.TrimStart().Length > 2)
            {
                Append("  •  ", body);
                AppendInline(line.TrimStart()[2..], body);
                Append("\n", body);
            }
            else if (line.StartsWith("*") && line.EndsWith("*") && line.Length > 2)
            { Append(line.Trim('*') + "\n", italic); }
            else
            {
                AppendInline(line, body);
                Append("\n", body);
            }
        }
    }
}
