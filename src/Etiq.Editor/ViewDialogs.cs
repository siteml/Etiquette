using Etiq.Core;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>Where the editor finds the printer/media registry
/// (config/printers.json): settings "configDir", then
/// %APPDATA%\Etiquette\config, then &lt;exe&gt;\config. Missing = empty
/// registry, never an error — the registry only POPULATES values here.</summary>
internal static class EditorRegistry
{
    public static Registry Load()
    {
        foreach (string dir in Candidates())
        {
            try
            {
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "printers.json")))
                    return Registry.Load(dir);
            }
            catch { /* malformed registry: fall through to the next candidate */ }
        }
        return new Registry();
    }

    public static IEnumerable<string> Candidates()
    {
        string? cfg = UpdateChecker.GetSetting("configDir");
        if (!string.IsNullOrWhiteSpace(cfg)) yield return cfg;
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Etiquette", "config");
        yield return Path.Combine(AppContext.BaseDirectory, "config");
    }
}

/// <summary>View → Grid… and View → Target Printer… (docs/grid-guides.md).
/// Both edit a CLONE of the document's ViewSettings and return it; the
/// caller installs it as one undo step.</summary>
internal static class ViewDialogs
{
    /// <summary>Grid source: off | a pitch in the display unit (suffix
    /// allowed) | printer dots. Returns null on cancel.</summary>
    public static ViewSettings? ShowGrid(IWin32Window owner, ViewSettings current)
    {
        var v = current.Clone();
        using var f = new Form
        {
            Text = "Grid", ClientSize = new Size(360, 214),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);

        var rOff = new RadioButton { Text = "No grid", Left = 14, Top = 14, Width = 320 };
        var rPitch = new RadioButton { Text = "Pitch:", Left = 14, Top = 44, Width = 70 };
        var pitch = new TextBox { Left = 90, Top = 42, Width = 110 };
        var pitchHint = new Label
        {
            Left = 206, Top = 46, Width = 150, ForeColor = SystemColors.GrayText,
            Text = $"{UnitPrefs.Suffix}; or 1mm, 0.05in, 50mils",
        };
        var rDots = new RadioButton { Text = "Printer dots", Left = 14, Top = 78, Width = 120 };
        var dotsInfo = new Label { Left = 36, Top = 104, Width = 210, Height = 36 };
        var target = new Button { Text = "Target printer…", Left = 250, Top = 100, Width = 100 };
        var show = new CheckBox { Text = "Show grid", Left = 14, Top = 146, Width = 160, Checked = v.ShowGrid };

        void RefreshDots()
        {
            dotsInfo.Text = v.DotsPerMm > 0
                ? $"{Num.F(v.DotsPerMm)} dots/mm ({Num.F(Math.Round(v.DotsPerMm * 25.4, 1))} dpi)" +
                  (string.IsNullOrWhiteSpace(v.Target) ? "" : $" — {v.Target}") +
                  $"\n1 dot = {Units.Format(Units.DotPitchMils(v.DotsPerMm), DisplayUnit.Mils)} mils"
                : "No target density set.";
            dotsInfo.ForeColor = v.DotsPerMm > 0 ? SystemColors.ControlText : Color.Firebrick;
        }
        target.Click += (_, _) =>
        {
            if (ShowTarget(f, v) is { } nv) { v = nv; RefreshDots(); rDots.Checked = true; }
        };

        if (current.GridOff) rOff.Checked = true;
        else if (current.IsDotGrid) rDots.Checked = true;
        else
        {
            rPitch.Checked = true;
            pitch.Text = Units.TryParseLength(current.Grid, DisplayUnit.Mils, out double m)
                ? UnitPrefs.F(m) : current.Grid;
        }
        if (pitch.Text == "") pitch.Text = UnitPrefs.F(UnitPrefs.Current switch
        {
            DisplayUnit.Mm => Units.MilsPerMm, DisplayUnit.Mils => 50, _ => 50,   // 1 mm / 0.05 in
        });
        pitch.Enter += (_, _) => rPitch.Checked = true;
        RefreshDots();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 174, Top = 176, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 262, Top = 176, Width = 80 };
        f.Controls.AddRange(new Control[] { rOff, rPitch, pitch, pitchHint, rDots, dotsInfo, target, show, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;

        while (true)
        {
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            if (rOff.Checked) v.Grid = "off";
            else if (rDots.Checked)
            {
                if (v.DotsPerMm <= 0)
                {
                    MessageBox.Show(f, "Choose a target printer (or type a density) first.",
                        "Grid", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    continue;
                }
                v.Grid = "dots";
            }
            else
            {
                if (!UnitPrefs.TryParse(pitch.Text, out double mils) || mils <= 0)
                {
                    MessageBox.Show(f, "Enter a positive pitch, e.g. 0.05in, 1mm or 50mils.",
                        "Grid", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    continue;
                }
                // store what was typed when it carries a unit; otherwise mils
                string t = pitch.Text.Trim();
                bool hasSuffix = t.Length > 0 && (char.IsLetter(t[^1]) || t[^1] == '"');
                v.Grid = hasSuffix ? t : Num.F(mils);
            }
            v.ShowGrid = show.Checked;
            return v;
        }
    }

    /// <summary>Pick the head density: a registry printer (dotsPerMm, else
    /// dpi/25.4) or a custom value. Stores PHYSICS (dots-per-mm) plus the
    /// name as a label only — never resolved by name later.</summary>
    public static ViewSettings? ShowTarget(IWin32Window owner, ViewSettings current)
    {
        var v = current.Clone();
        var reg = EditorRegistry.Load();
        using var f = new Form
        {
            Text = "Target Printer", ClientSize = new Size(380, 190),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);

        var rReg = new RadioButton { Text = "Registry printer:", Left = 14, Top = 14, Width = 130 };
        var combo = new ComboBox { Left = 150, Top = 12, Width = 216, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var p in reg.Printers.Values.OrderBy(p => p.Name))
            combo.Items.Add(new PrinterItem(p));
        var regHint = new Label
        {
            Left = 150, Top = 38, Width = 216, ForeColor = SystemColors.GrayText,
            Text = combo.Items.Count == 0 ? "no printers.json found" : "",
        };
        var rCustom = new RadioButton { Text = "Custom:", Left = 14, Top = 70, Width = 130 };
        var dpmm = new NumericUpDown
        {
            Left = 150, Top = 68, Width = 80, DecimalPlaces = 3, Minimum = 1, Maximum = 100, Increment = 0.5m,
        };
        var dpmmL = new Label { Text = "dots/mm", Left = 234, Top = 72, Width = 60 };
        var dpi = new NumericUpDown
        {
            Left = 150, Top = 98, Width = 80, DecimalPlaces = 1, Minimum = 25, Maximum = 2540, Increment = 1,
        };
        var dpiL = new Label { Text = "dpi (converted)", Left = 234, Top = 102, Width = 120 };
        bool syncing = false;
        dpmm.ValueChanged += (_, _) =>
        {
            if (syncing) return; syncing = true;
            dpi.Value = Math.Clamp(Math.Round(dpmm.Value * 25.4m, 1), dpi.Minimum, dpi.Maximum);
            syncing = false; rCustom.Checked = true;
        };
        dpi.ValueChanged += (_, _) =>
        {
            if (syncing) return; syncing = true;
            dpmm.Value = Math.Clamp(Math.Round(dpi.Value / 25.4m, 3), dpmm.Minimum, dpmm.Maximum);
            syncing = false; rCustom.Checked = true;
        };
        combo.SelectedIndexChanged += (_, _) => rReg.Checked = true;

        // prefill: current value → matching registry entry, else custom
        if (v.DotsPerMm > 0)
        {
            syncing = true;
            dpmm.Value = Math.Clamp((decimal)v.DotsPerMm, dpmm.Minimum, dpmm.Maximum);
            dpi.Value = Math.Clamp(Math.Round(dpmm.Value * 25.4m, 1), dpi.Minimum, dpi.Maximum);
            syncing = false;
            var match = combo.Items.OfType<PrinterItem>()
                .FirstOrDefault(i => Math.Abs(i.Def.DotsPerMmEffective - v.DotsPerMm) < 1e-6 &&
                                     (v.Target is null || i.Def.Name == v.Target));
            if (match is not null) { combo.SelectedItem = match; rReg.Checked = true; }
            else rCustom.Checked = true;
        }
        else
        {
            syncing = true; dpmm.Value = 8; dpi.Value = 203.2m; syncing = false;
            if (combo.Items.Count > 0) { combo.SelectedIndex = 0; rReg.Checked = true; }
            else rCustom.Checked = true;
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 194, Top = 150, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 282, Top = 150, Width = 80 };
        f.Controls.AddRange(new Control[] { rReg, combo, regHint, rCustom, dpmm, dpmmL, dpi, dpiL, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;

        if (rReg.Checked && combo.SelectedItem is PrinterItem pi)
        {
            v.DotsPerMm = pi.Def.DotsPerMmEffective;
            v.Target = pi.Def.Name;
        }
        else
        {
            v.DotsPerMm = (double)dpmm.Value;
            v.Target = null;
        }
        return v;
    }

    /// <summary>Add/edit one guide: axis (fixed when editing), position in
    /// the display unit (suffix allowed), optional name. Returns the guide or
    /// null on cancel.</summary>
    public static Guide? ShowGuide(IWin32Window owner, Guide? existing, bool? forceVertical = null)
    {
        using var f = new Form
        {
            Text = existing is null ? "Add Guide" : "Guide", ClientSize = new Size(320, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        Ui.AutoScale(f);
        bool vertical = existing?.Vertical ?? forceVertical ?? true;
        var rV = new RadioButton { Text = "Vertical (x)", Left = 14, Top = 14, Width = 130, Checked = vertical };
        var rH = new RadioButton { Text = "Horizontal (y)", Left = 160, Top = 14, Width = 140, Checked = !vertical };
        rV.Enabled = rH.Enabled = existing is null;
        var posLbl = new Label { Text = $"Position ({UnitPrefs.Suffix}):", Left = 14, Top = 50, Width = 110 };
        var pos = new TextBox { Left = 130, Top = 46, Width = 170, Text = existing is null ? "" : UnitPrefs.F(existing.Pos) };
        var nameLbl = new Label { Text = "Name:", Left = 14, Top = 82, Width = 110 };
        var name = new TextBox { Left = 130, Top = 78, Width = 170, Text = existing?.Name ?? "" };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 134, Top = 112, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 222, Top = 112, Width = 80 };
        f.Controls.AddRange(new Control[] { rV, rH, posLbl, pos, nameLbl, name, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        pos.Select();
        while (true)
        {
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            if (!UnitPrefs.TryParse(pos.Text, out double mils))
            {
                MessageBox.Show(f, "Enter a position, e.g. 1.25, 30mm or 250mils.", "Guide",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }
            string? nm = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            return new Guide(rV.Checked, mils, nm);
        }
    }

    private sealed record PrinterItem(PrinterDef Def)
    {
        public override string ToString() =>
            $"{Def.Name}  —  {Num.F(Math.Round(Def.DotsPerMmEffective, 3))} dots/mm" +
            (Def.DotsPerMm is null ? $" (from {Def.Dpi} dpi)" : "");
    }
}
