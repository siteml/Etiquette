using Etiq.Core;

namespace Etiq.Editor;

/// <summary>
/// Help → Printer Settings…: everything Etiquette remembers PER WINDOWS
/// PRINTER, in plain words — no ZPL knowledge needed. Print offset (any
/// transport); transport (driver / ZPL raster); and for the raster path
/// the things the Windows driver used to decide per job: print method
/// (ribbon or not), darkness, speed, plus raster rotation. An "Advanced"
/// box takes literal ZPL for anything else. Stored in settings.json as
/// "printXxx:&lt;queue&gt;" keys; PrintService.RawZplPrefix composes the
/// job prefix from them.
/// </summary>
internal static class PrinterSetupDialog
{
    /// <summary>One printer's pending edits (null = untouched).</summary>
    private sealed class Pending
    {
        public int OffX, OffY;
        public string Transport = "driver";
        public string Rotate = "auto";
        public string Media = "";        // "" | transfer | direct
        public int Darkness = -1;        // -1 = printer's setting
        public int Speed = 0;            // 0 = printer's setting, else in/s
        public bool PlainHex;
        public string Zpl = "";
        public static Pending Load(string p) => new()
        {
            OffX = PrintService.GetOffset(p).X, OffY = PrintService.GetOffset(p).Y,
            Transport = PrintService.GetTransport(p) == "zpl-raster" ? "zpl-raster" : "driver",
            Rotate = PrintService.GetRotate(p),
            Media = PrintService.GetMedia(p),
            Darkness = PrintService.GetDarkness(p),
            Speed = PrintService.GetSpeed(p),
            PlainHex = PrintService.GetPlainHex(p),
            Zpl = PrintService.GetZpl(p),
        };
        public void Save(string p)
        {
            PrintService.SetOffset(p, OffX, OffY);
            PrintService.SetTransport(p, Transport);
            PrintService.SetRotate(p, Rotate);
            PrintService.SetMedia(p, Media);
            PrintService.SetDarkness(p, Darkness);
            PrintService.SetSpeed(p, Speed);
            PrintService.SetPlainHex(p, PlainHex);
            PrintService.SetZpl(p, Zpl);
        }
    }

    public static void Show(IWin32Window owner, string? preselect = null)
    {
        using var f = new Form
        {
            Text = "Printer Settings", ClientSize = new Size(470, 500),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        var tip = new ToolTip { AutoPopDelay = 20000 };
        int L1 = 14, L2 = 160, W2 = 290;

        var printerLbl = new Label { Text = "Printer:", Left = L1, Top = 18, Width = 140, Height = 20 };
        var printer = new ComboBox { Left = L2, Top = 14, Width = W2, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string pn in System.Drawing.Printing.PrinterSettings.InstalledPrinters) printer.Items.Add(pn);
        tip.SetToolTip(printer, "Settings below are remembered for this Windows printer by its exact queue name.");

        var transLbl = new Label { Text = "How to print:", Left = L1, Top = 52, Width = 140, Height = 20 };
        var transport = new ComboBox { Left = L2, Top = 48, Width = W2, DropDownStyle = ComboBoxStyle.DropDownList };
        transport.Items.AddRange(new object[]
        {
            "Through the Windows driver (default)",
            "Send the label image directly (Zebra)",
        });
        tip.SetToolTip(transport,
            "Directly: Etiquette renders the label to dots itself and sends it to the Zebra\n" +
            "as one compressed job — no driver in the loop. Use it when the printer stalls\n" +
            "between labels (slow USB→parallel adapters) or the driver's output is poor.");

        var offLbl = new Label { Text = "Print offset:", Left = L1, Top = 86, Width = 140, Height = 20 };
        var offX = new NumericUpDown { Left = L2, Top = 82, Width = 70, Minimum = -500, Maximum = 500 };
        var offXl = new Label { Text = "→ right (mils)", Left = L2 + 74, Top = 86, Width = 90, Height = 20 };
        var offY = new NumericUpDown { Left = L2 + 166, Top = 82, Width = 70, Minimum = -500, Maximum = 500 };
        var offYl = new Label { Text = "↓ down", Left = L2 + 240, Top = 86, Width = 50, Height = 20 };
        tip.SetToolTip(offX, "Nudge everything on the label. 1000 mils = 1 inch.");

        // ---- direct (ZPL raster) group ----
        var grp = new GroupBox { Text = "Direct printing (Zebra)", Left = L1, Top = 122, Width = 442, Height = 300 };
        int gL1 = 12, gL2 = 146, gW2 = 280;
        var mediaLbl = new Label { Text = "Print method:", Left = gL1, Top = 30, Width = 130, Height = 20 };
        var media = new ComboBox { Left = gL2, Top = 26, Width = gW2, DropDownStyle = ComboBoxStyle.DropDownList };
        media.Items.AddRange(new object[]
        {
            "Leave as set on the printer",
            "Thermal transfer — printer has a ribbon",
            "Direct thermal — no ribbon, heat-sensitive labels",
        });
        tip.SetToolTip(media,
            "The driver sent this with every job; without it the printer uses whatever it was\n" +
            "last set to. Wrong method = far too hot (bars bleed together) or too faint.");

        var darkLbl = new Label { Text = "Darkness:", Left = gL1, Top = 64, Width = 130, Height = 20 };
        var darkSet = new CheckBox { Text = "Set to", Left = gL2, Top = 62, Width = 64, Height = 22 };
        var dark = new NumericUpDown { Left = gL2 + 68, Top = 60, Width = 56, Minimum = 0, Maximum = 30, Value = 10 };
        var darkInfo = new Label { Text = "(0 lightest … 30 darkest)", Left = gL2 + 130, Top = 64, Width = 150, Height = 20 };
        tip.SetToolTip(darkSet, "Unchecked: the printer's own darkness setting. Start around 10 with a ribbon.");

        var speedLbl = new Label { Text = "Print speed:", Left = gL1, Top = 98, Width = 130, Height = 20 };
        var speed = new ComboBox { Left = gL2, Top = 94, Width = gW2, DropDownStyle = ComboBoxStyle.DropDownList };
        speed.Items.AddRange(new object[]
        {
            "Leave as set on the printer", "2 in/s (slowest, most heat)", "3 in/s", "4 in/s", "5 in/s", "6 in/s (fastest)",
        });
        tip.SetToolTip(speed, "Slower = hotter. If bars bleed even at low darkness, go faster.");

        var rotLbl = new Label { Text = "Label rotation:", Left = gL1, Top = 132, Width = 130, Height = 20 };
        var rotate = new ComboBox { Left = gL2, Top = 128, Width = gW2, DropDownStyle = ComboBoxStyle.DropDownList };
        rotate.Items.AddRange(new object[]
        {
            "Automatic (wide labels turned like the driver does)", "Don't rotate", "90° clockwise", "180°", "90° counter-clockwise",
        });
        tip.SetToolTip(rotate, "If labels come out upside down or sideways, pick the other 90°.");

        var advLbl = new Label { Text = "Advanced", Left = gL1, Top = 176, Width = 130, Height = 20, Font = new Font(f.Font, FontStyle.Bold) };
        var plainHex = new CheckBox
        {
            Text = "Send the image uncompressed (slow — diagnostics only)", Left = gL2, Top = 198, Width = gW2, Height = 22,
        };
        var zplLbl = new Label { Text = "Extra ZPL commands:", Left = gL1, Top = 234, Width = 130, Height = 20 };
        var zplBox = new TextBox { Left = gL2, Top = 230, Width = gW2 };
        tip.SetToolTip(zplBox, "Literal ZPL added to every label after the settings above (e.g. ^MMT). Leave empty unless you know what you need.");
        var previewLbl = new Label { Left = gL2, Top = 258, Width = gW2, Height = 34, ForeColor = SystemColors.GrayText };
        grp.Controls.AddRange(new Control[]
            { mediaLbl, media, darkLbl, darkSet, dark, darkInfo, speedLbl, speed, rotLbl, rotate, advLbl, plainHex, zplLbl, zplBox, previewLbl });

        string[] rotKeys = { "auto", "0", "90", "180", "270" };
        string[] mediaKeys = { "", "transfer", "direct" };
        var edits = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        string? cur = null;
        bool loading = false;

        Pending Cur() => edits.TryGetValue(cur!, out var p) ? p : (edits[cur!] = Pending.Load(cur!));

        void ShowPreview()
        {
            if (cur is null) { previewLbl.Text = ""; return; }
            var p = Cur();
            string zpl = PrintService.ComposeZplPrefix(p.Media, p.Darkness, p.Speed, p.Zpl);
            previewLbl.Text = "Sent with every label: ^PW<width>" + zpl;
        }

        void LoadPrinter()
        {
            cur = printer.SelectedItem as string;
            bool any = cur is not null;
            transport.Enabled = offX.Enabled = offY.Enabled = any;
            if (!any) { grp.Enabled = false; return; }
            loading = true;
            var p = Cur();
            offX.Value = Math.Clamp(p.OffX, -500, 500);
            offY.Value = Math.Clamp(p.OffY, -500, 500);
            transport.SelectedIndex = p.Transport == "zpl-raster" ? 1 : 0;
            media.SelectedIndex = Math.Max(0, Array.IndexOf(mediaKeys, p.Media));
            darkSet.Checked = p.Darkness >= 0;
            dark.Value = Math.Clamp(p.Darkness < 0 ? 10 : p.Darkness, 0, 30);
            dark.Enabled = darkSet.Checked;
            speed.SelectedIndex = p.Speed is >= 2 and <= 6 ? p.Speed - 1 : 0;
            rotate.SelectedIndex = Math.Max(0, Array.IndexOf(rotKeys, p.Rotate));
            plainHex.Checked = p.PlainHex;
            zplBox.Text = p.Zpl;
            grp.Enabled = transport.SelectedIndex == 1;
            loading = false;
            ShowPreview();
        }

        void Store()
        {
            if (loading || cur is null) return;
            var p = Cur();
            p.OffX = (int)offX.Value; p.OffY = (int)offY.Value;
            p.Transport = transport.SelectedIndex == 1 ? "zpl-raster" : "driver";
            p.Media = mediaKeys[Math.Max(0, media.SelectedIndex)];
            p.Darkness = darkSet.Checked ? (int)dark.Value : -1;
            p.Speed = speed.SelectedIndex >= 1 ? speed.SelectedIndex + 1 : 0;
            p.Rotate = rotKeys[Math.Max(0, rotate.SelectedIndex)];
            p.PlainHex = plainHex.Checked;
            p.Zpl = zplBox.Text;
            dark.Enabled = darkSet.Checked;
            grp.Enabled = transport.SelectedIndex == 1;
            ShowPreview();
        }

        printer.SelectedIndexChanged += (_, _) => LoadPrinter();
        offX.ValueChanged += (_, _) => Store();
        offY.ValueChanged += (_, _) => Store();
        dark.ValueChanged += (_, _) => Store();
        foreach (var c in new[] { transport, media, speed, rotate }) c.SelectedIndexChanged += (_, _) => Store();
        darkSet.CheckedChanged += (_, _) => Store();
        plainHex.CheckedChanged += (_, _) => Store();
        zplBox.TextChanged += (_, _) => Store();

        try
        {
            string def = preselect ?? new System.Drawing.Printing.PrinterSettings().PrinterName;
            printer.SelectedItem = printer.Items.Contains(def) ? def
                                 : printer.Items.Count > 0 ? printer.Items[0] : null;
        }
        catch { if (printer.Items.Count > 0) printer.SelectedIndex = 0; }
        LoadPrinter();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 286, Top = 456, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 374, Top = 456, Width = 80 };
        f.Controls.AddRange(new Control[]
            { printerLbl, printer, transLbl, transport, offLbl, offX, offXl, offY, offYl, grp, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) == DialogResult.OK)
            foreach (var (pn, p) in edits) p.Save(pn);
    }
}
