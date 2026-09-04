using System.Text.Json;
using Etiq.Core;

namespace Etiq.Editor;

/// <summary>
/// Print log viewer: one row per printed LABEL (job + page), with the
/// job's latest known fate (spooled / completed / error / stuck) merged
/// in from the follow-up events. The look-back window is configurable and
/// persisted (default 1 month); it is a read window, not a retention
/// limit — the monthly files keep everything.
/// Reprint replays the selected row's logged values through the caller's
/// print path — the same verbatim-replay contract the series manifest
/// will use.
/// </summary>
public sealed class PrintLogDialog : Form
{
    private readonly DataGridView _grid = GridTools.NewGrid();
    private readonly NumericUpDown _backN = new() { Minimum = 1, Maximum = 999, Width = 60 };
    private readonly ComboBox _backUnit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Button _reprint = new() { Text = "Reprint", Width = 90, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Action<Dictionary<string, string>>? _reprintAction;
    private readonly List<Dictionary<string, string>?> _rowValues = new();

    /// <summary>reprint: null hides the button (log is view-only).</summary>
    public PrintLogDialog(IWin32Window? owner, Action<Dictionary<string, string>>? reprint)
    {
        _reprintAction = reprint;
        Text = "Print Log";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Ui.AutoScale(this);
        ClientSize = new Size(920, 480);
        MinimumSize = new Size(620, 300);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "Show last", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_backN);
        _backUnit.Items.AddRange(new object[] { "days", "months" });
        top.Controls.Add(_backUnit);
        var refresh = new Button { Text = "Refresh", Width = 80 };
        refresh.Click += (_, _) => Reload();
        top.Controls.Add(refresh);
        _status.Margin = new Padding(12, 8, 0, 0);
        top.Controls.Add(_status);

        _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add("time", "Time");
        _grid.Columns.Add("template", "Template");
        _grid.Columns.Add("printer", "Printer");
        _grid.Columns.Add("page", "Page");
        _grid.Columns.Add("result", "Result");
        _grid.Columns.Add("values", "Values");
        _grid.Columns["time"]!.FillWeight = 80; _grid.Columns["template"]!.FillWeight = 70;
        _grid.Columns["printer"]!.FillWeight = 80; _grid.Columns["page"]!.FillWeight = 30;
        _grid.Columns["result"]!.FillWeight = 45; _grid.Columns["values"]!.FillWeight = 220;
        _grid.SelectionChanged += (_, _) => _reprint.Enabled =
            _reprintAction is not null && CurrentValues() is not null;

        var bottom = new FlowLayoutPanel
            { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 80 };
        bottom.Controls.Add(close);
        if (_reprintAction is not null)
        {
            _reprint.Click += (_, _) => { if (CurrentValues() is { } v) _reprintAction(v); };
            bottom.Controls.Add(_reprint);
        }
        CancelButton = close;

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);

        // persisted look-back window, default 1 month
        int n = int.TryParse(UpdateChecker.GetSetting("printLogBackN"), out int pn) ? Math.Clamp(pn, 1, 999) : 1;
        string unit = UpdateChecker.GetSetting("printLogBackUnit") == "days" ? "days" : "months";
        _backN.Value = n; _backUnit.SelectedItem = unit;
        _backN.ValueChanged += (_, _) => { Persist(); Reload(); };
        _backUnit.SelectedIndexChanged += (_, _) => { Persist(); Reload(); };

        Reload();
    }

    private void Persist()
    {
        UpdateChecker.SetSetting("printLogBackN", ((int)_backN.Value).ToString());
        UpdateChecker.SetSetting("printLogBackUnit", (string)_backUnit.SelectedItem!);
    }

    private Dictionary<string, string>? CurrentValues() =>
        _grid.CurrentRow is { Index: >= 0 and var i } && i < _rowValues.Count ? _rowValues[i] : null;

    private void Reload()
    {
        var since = (string)_backUnit.SelectedItem! == "days"
            ? DateTimeOffset.Now.AddDays(-(int)_backN.Value)
            : DateTimeOffset.Now.AddMonths(-(int)_backN.Value);
        var events = PrintLog.Read(since);

        // fate per job: the last non-spooled event wins
        var fate = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            string ev = Str(e, "event"), job = Str(e, "job");
            if (job != "" && ev is "completed" or "error" or "stuck") fate[job] = ev;
        }

        _grid.Rows.Clear();
        _rowValues.Clear();
        foreach (var e in Enumerable.Reverse(events))          // newest first
        {
            if (Str(e, "event") != "spooled") continue;
            string job = Str(e, "job");
            Dictionary<string, string>? vals = null;
            string valText = "";
            if (e.TryGetValue("values", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                vals = new Dictionary<string, string>();
                foreach (var p in v.EnumerateObject())
                    vals[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                valText = string.Join("; ", vals.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            string when = DateTimeOffset.TryParse(Str(e, "ts"), out var ts) ? ts.ToString("yyyy-MM-dd HH:mm:ss") : Str(e, "ts");
            string pageTxt = Str(e, "page") is { Length: > 0 } pg ? $"{pg}/{Str(e, "pages")}" : "";
            _grid.Rows.Add(when, Str(e, "template"), Str(e, "printer"), pageTxt,
                           fate.GetValueOrDefault(job, "spooled"), valText);
            _rowValues.Add(vals);
        }
        _status.Text = $"{_grid.Rows.Count} label(s)" +
            (PrintLog.Directory is null ? " — logging is OFF" : "");
        _grid.ClearSelection();
    }

    private static string Str(Dictionary<string, JsonElement> e, string key) =>
        e.TryGetValue(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" :
              v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : v.ToString()
            : "";
}
