using System.Xml.Linq;

namespace Etiq.Editor;

/// <summary>
/// Hand-built editor for one etiq:field, replacing the PropertyGrid on the
/// Fields tab. Everything is driven by the static <see cref="Spec"/> table
/// — one row per attribute: label, control kind, the sources it applies to,
/// hint text, and (for combos) where its choices come from. Changing the
/// field schema later means editing THAT TABLE, not layout code.
/// Rows rebuild when the field or its source changes; values commit to the
/// XElement as they are typed (empty = attribute removed).
/// </summary>
public sealed class FieldPane : UserControl
{
    private enum Kind { Text, Check, Combo, ComboFree }   // Combo = closed list, ComboFree = suggestions

    /// <summary>choices: key into the provider map (null = fixed Choices array).</summary>
    private sealed record Row(string Attr, string Label, Kind Kind, string[]? Sources,
                              string Hint, string? ChoicesKey = null, string[]? Choices = null);

    // ---- THE schema. Order here = display order. Sources null = always. ----
    private static readonly Row[] Spec =
    {
        new("name",   "Name",   Kind.Text,  null, "Unique; bind elements via data-field"),
        new("source", "Source", Kind.Combo, null, "",
            Choices: new[] { "epicor", "rest", "prompt", "serial", "auto", "fixed", "compose", "list" }),

        new("caption", "Caption", Kind.Text, new[] { "prompt", "epicor", "rest", "list" },
            "Data-panel label shown to the operator"),
        new("default", "Default", Kind.Text, new[] { "prompt" },
            "Prefills the input; Clear restores it"),
        new("value",   "Value",   Kind.Text, new[] { "fixed", "auto" },
            "Fixed value, or auto pattern e.g. date:dd-MMM-yyyy"),

        new("from",    "Query",   Kind.Combo, new[] { "epicor", "rest" },
            "Declared query this field reads", ChoicesKey: "queries"),
        new("column",  "Column",  Kind.ComboFree, new[] { "epicor", "rest", "list" },
            "Query / list column the value comes from", ChoicesKey: "columns"),
        new("list",    "List",    Kind.Combo, new[] { "list" },
            "Embedded list this field reads", ChoicesKey: "lists"),

        new("connection", "Connection", Kind.Combo, new[] { "rest" },
            "Per-field pull (legacy) — prefer Query above", ChoicesKey: "connections"),
        new("query",   "Endpoint", Kind.Text, new[] { "rest" },
            "Endpoint path (per-field pull only)"),
        new("pick",    "Pick",     Kind.Text, new[] { "rest" },
            "Dotted JSON path into the response, e.g. assets.0.name"),

        new("counter", "Counter", Kind.Text, new[] { "serial" }, "Counter name"),
        new("format",  "Format",  Kind.Text, new[] { "serial" }, "e.g. 0000 or base36:4"),

        new("override", "Override", Kind.Check, new[] { "epicor", "rest" },
            "Operator may type over the fetched value; empty entry = use the pull"),
        new("required", "Required", Kind.Check, new[] { "prompt", "epicor", "rest", "list" },
            ""),
        new("panel",    "Hide on panel", Kind.Check, new[] { "prompt", "epicor", "rest", "list" },
            "Resolves as usual but shows no input"),

        new("case",    "Case",     Kind.Combo, new[] { "prompt", "epicor", "rest", "list", "fixed", "compose" },
            "", Choices: new[] { "", "upper", "lower", "title" }),
        new("on-fail", "On fail",  Kind.ComboFree, new[] { "epicor", "rest" },
            "block (default) | cached | use:VALUE", Choices: new[] { "", "block", "cached" }),
        new("if-empty", "If empty", Kind.Text, new[] { "prompt", "epicor", "rest", "list", "compose" },
            "Value used when the source resolves empty"),
        new("collapse-blank-lines", "Collapse blank lines", Kind.Check, new[] { "compose" },
            "Drop lines that end up empty (address-block style)"),
    };

    // check attrs whose "true" isn't the literal "true"
    private static string CheckOn(string attr) => attr == "panel" ? "hide" : "true";

    // AutoScroll directly on the table makes phantom scrollbars (the
    // Percent column measures before the vertical bar steals width, so a
    // horizontal bar appears, which steals height, which...). Instead the
    // HOST panel scrolls vertically and the table is Dock=Top + AutoSize:
    // its width always tracks the panel's display area, so no horizontal
    // bar can ever appear and the vertical one only shows when needed.
    private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2, Padding = new Padding(0, 2, 0, 0),
    };
    private XElement? _el;
    private Action? _changed;
    private Func<string, string[]>? _choices;   // provider map: key -> live choices
    private bool _loading;

    public FieldPane()
    {
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _host.Controls.Add(_table);
        Controls.Add(_host);
    }

    /// <summary>Show a field (null = empty pane). choices(key) supplies live
    /// combo content for ChoicesKey rows ("queries", "lists", "columns",
    /// "connections"); changed fires after every committed edit.</summary>
    public void SetField(XElement? el, Func<string, string[]> choices, Action changed)
    {
        if (ReferenceEquals(_el, el) && el is not null)
            { _choices = choices; _changed = changed; return; }   // same field — keep the pane (and the caret)
        _el = el; _choices = choices; _changed = changed;
        Rebuild();
    }

    private void Rebuild()
    {
        _loading = true;
        _table.SuspendLayout();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = 0;
        if (_el is null) { _table.ResumeLayout(); _loading = false; return; }

        string src = (string?)_el.Attribute("source") ?? "";
        foreach (var row in Spec)
        {
            if (row.Sources is not null && !row.Sources.Contains(src)) continue;
            AddRow(row);
        }
        _table.ResumeLayout();
        _loading = false;
    }

    private void AddRow(Row row)
    {
        int r = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bool hinted = row.Hint.Length > 0;

        Control ctl;
        string cur = (string?)_el!.Attribute(row.Attr) ?? "";
        switch (row.Kind)
        {
            case Kind.Check:
                var cb = new CheckBox { AutoSize = true, Checked = cur == CheckOn(row.Attr), Margin = new Padding(3, 5, 3, 0) };
                cb.CheckedChanged += (_, _) => Commit(row, cb.Checked ? CheckOn(row.Attr) : "");
                ctl = cb;
                break;
            case Kind.Combo or Kind.ComboFree:
                var co = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = row.Kind == Kind.Combo && row.ChoicesKey is null
                        ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown,
                };
                var items = row.Choices ?? _choices?.Invoke(row.ChoicesKey!) ?? Array.Empty<string>();
                co.Items.AddRange(items.Cast<object>().ToArray());
                if (co.DropDownStyle == ComboBoxStyle.DropDownList)
                    co.SelectedItem = items.Contains(cur) ? cur : items.FirstOrDefault(i => i == cur);
                co.Text = cur;
                // commit on close/leave/typing — but a Source change rebuilds,
                // so route it through BeginInvoke to let the event finish
                void CoCommit()
                {
                    string v = co.Text;
                    if (row.Attr == "source")
                        BeginInvoke(() => { Commit(row, v); Rebuild(); });
                    else Commit(row, v);
                }
                co.SelectedIndexChanged += (_, _) => CoCommit();
                co.TextChanged += (_, _) => { if (co.DropDownStyle == ComboBoxStyle.DropDown) CoCommit(); };
                ctl = co;
                break;
            default:
                var tb = new TextBox { Dock = DockStyle.Fill, Text = cur };
                tb.TextChanged += (_, _) => Commit(row, tb.Text);
                ctl = tb;
                break;
        }

        _table.Controls.Add(new Label
        {
            Text = row.Label, AutoSize = true, Margin = new Padding(3, 6, 3, 0),
        }, 0, r);

        if (!hinted) { _table.Controls.Add(ctl, 1, r); return; }
        var wrap = new TableLayoutPanel
            { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        wrap.Controls.Add(ctl);
        wrap.Controls.Add(new Label
        {
            Text = row.Hint, AutoSize = true, ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 4),
        });
        _table.Controls.Add(wrap, 1, r);
    }

    private void Commit(Row row, string value)
    {
        if (_loading || _el is null) return;
        _el.SetAttributeValue(row.Attr, value.Length == 0 ? null : value);
        _changed?.Invoke();
    }
}
