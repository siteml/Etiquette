using System.Xml.Linq;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>Grid plumbing shared by MetadataDialog and ComposeDialog:
/// row-order tools (order is meaningful everywhere in the convention) and
/// the compose seg-grid columns + XML load/commit.</summary>
internal static class GridTools
{
    private static readonly XNamespace NS = EditorDoc.EtiqNs;

    public static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true,
        AllowUserToDeleteRows = true, RowHeadersWidth = 24,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    /// <summary>Right-click row menu: Insert Above / Move Up / Move Down /
    /// Delete. Moves swap cell values, so grids with combo columns keep
    /// their column types.</summary>
    public static void AttachRowTools(DataGridView g)
    {
        var menu = new ContextMenuStrip();
        int row = -1;
        void Swap(int a, int b)
        {
            if (a < 0 || b < 0 || a >= g.Rows.Count || b >= g.Rows.Count) return;
            if (g.Rows[a].IsNewRow || g.Rows[b].IsNewRow) return;
            g.EndEdit();
            for (int c = 0; c < g.Columns.Count; c++)
                (g.Rows[a].Cells[c].Value, g.Rows[b].Cells[c].Value) =
                    (g.Rows[b].Cells[c].Value, g.Rows[a].Cells[c].Value);
            g.CurrentCell = g.Rows[b].Cells[0];
        }
        menu.Items.Add("Insert Row Above", null, (_, _) =>
        {
            if (row >= 0) { g.EndEdit(); g.Rows.Insert(row, 1); }
        });
        menu.Items.Add("Move Up", null, (_, _) => Swap(row, row - 1));
        menu.Items.Add("Move Down", null, (_, _) => Swap(row, row + 1));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete Row", null, (_, _) =>
        {
            if (row >= 0 && row < g.Rows.Count && !g.Rows[row].IsNewRow)
            { g.EndEdit(); g.Rows.RemoveAt(row); }
        });
        menu.Opening += (_, e) => e.Cancel = row < 0;
        g.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) { return; }
            var hit = g.HitTest(e.X, e.Y);
            row = hit.RowIndex;
            if (row >= 0) { g.ClearSelection(); g.Rows[row].Selected = true; }
        };
        g.ContextMenuStrip = menu;
    }

    // ---------- compose seg grid ----------

    public static readonly string[] SegCols =
        { "value", "ref", "sep", "map", "default", "split", "part", "start", "len", "format", "case", "pad", "if-empty" };

    /// <summary>Columns that stay visible in the compact seg grid; every
    /// other seg attribute lives in hidden cells edited through the details
    /// pane and summarized in the read-only "transforms" column.</summary>
    public static readonly string[] CoreSegCols = { "value", "ref", "sep" };
    public const string SummaryCol = "transforms";

    /// <summary>Add the seg columns (newline checkbox + attributes) to a
    /// fresh grid, with the sep-BEFORE tooltip. compact=true hides the
    /// transform attributes behind a summary column.</summary>
    public static void AddSegColumns(DataGridView g, bool compact = false)
    {
        g.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "newline", HeaderText = "newline", FillWeight = 40 });
        foreach (var c in SegCols)
        {
            int idx = g.Columns.Add(c, c == "sep" ? "sep BEFORE" : c);
            if (compact && !CoreSegCols.Contains(c)) g.Columns[idx].Visible = false;
            if (c == "value" || c == "ref") g.Columns[idx].FillWeight = 120;
            if (c == "sep") g.Columns[idx].FillWeight = 55;
        }
        if (compact)
        {
            int si = g.Columns.Add(SummaryCol, "transforms");
            g.Columns[si].ReadOnly = true;
            g.Columns[si].FillWeight = 160;
            g.Columns[si].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            g.Columns[si].ToolTipText = "Edit in the pane below (select the row)";
        }
        g.Columns["sep"]!.ToolTipText =
            "Separator emitted BEFORE this segment's own value - and only " +
            "when this segment is non-empty AND the line already has content. " +
            "Put \", \" on the State row (not the City row) to get \"City, State\". " +
            "Spaces are kept.";
        if (!compact)
        {
            g.Columns["split"]!.ToolTipText =
                "Delimiter to split the value on; part picks the piece " +
                "(0-based; negative from the end: -1 = last, -2 = one before). " +
                "\"Site > Bldg > Room\" with split \">\" part -2 gives \"Bldg\". " +
                "Pieces are trimmed; a missing piece is empty.";
            g.Columns["part"]!.ToolTipText = "Which split piece to keep (default -1 = last).";
        }
    }

    /// <summary>One-line human summary of a row's transform attributes, in
    /// the order the resolver applies them.</summary>
    public static string SegSummary(DataGridViewRow row)
    {
        string V(string c) => row.Cells[c].Value?.ToString() ?? "";
        var parts = new List<string>();
        if (V("split") != "") parts.Add($"piece {(V("part") == "" ? "-1" : V("part"))} of \"{V("split")}\"");
        if (V("start") != "" || V("len") != "")
            parts.Add($"chars {(V("start") == "" ? "0" : V("start"))}{(V("len") == "" ? "…" : "+" + V("len"))}");
        if (V("format") != "") parts.Add(V("format"));
        if (V("case") != "" && V("case") != "normal") parts.Add(V("case"));
        if (V("pad") != "") parts.Add("pad " + V("pad"));
        if (V("map") != "") parts.Add($"map {V("map")}{(V("default") == "" ? "" : " (else " + V("default") + ")")}");
        if (V("if-empty") != "") parts.Add($"if empty → {V("if-empty")}");
        return string.Join(" · ", parts);
    }

    public static void RefreshSummaries(DataGridView g)
    {
        if (g.Columns[SummaryCol] is null) return;
        foreach (DataGridViewRow r in g.Rows)
            if (!r.IsNewRow) r.Cells[SummaryCol].Value = SegSummary(r);
    }

    /// <summary>Fill a seg grid from the etiq:seg children of parent
    /// (a compose field, or one etiq:variant).</summary>
    /// <summary>Attributes where PRESENT-BUT-EMPTY is meaningful (an
    /// explicitly blank fallback) — shown and typed as "" since an empty
    /// cell means "attribute absent".</summary>
    private static readonly string[] EmptyOkCols = { "default", "if-empty" };

    public static void LoadSegs(DataGridView g, XElement parent)
    {
        g.Rows.Clear();
        foreach (var s in parent.Elements(NS + "seg"))
        {
            var vals = new object[SegCols.Length + 1];
            vals[0] = (string?)s.Attribute("newline") == "true";
            for (int i = 0; i < SegCols.Length; i++)
            {
                string? v = (string?)s.Attribute(SegCols[i]);
                if (EmptyOkCols.Contains(SegCols[i]) && v is not null)
                    v = v == "" ? "\"\""                          // blank fallback shows as ""
                        : v == "\"\"" || v.StartsWith('\\') ? "\\" + v   // values needing the escape round-trip with it
                        : v;
                vals[i + 1] = v ?? "";
            }
            g.Rows.Add(vals);
        }
        RefreshSummaries(g);
    }

    /// <summary>Replace parent's etiq:seg children with the grid's rows.</summary>
    public static void CommitSegs(DataGridView g, XElement parent, bool endEdit = true)
    {
        if (endEdit) g.EndEdit();
        parent.Elements(NS + "seg").Remove();
        foreach (DataGridViewRow row in g.Rows)
        {
            if (row.IsNewRow) continue;
            if (row.Cells["newline"].Value is true)
            {
                // a line break carries nothing else (validator enforces too)
                parent.Add(new XElement(NS + "seg", new XAttribute("newline", "true")));
                continue;
            }
            var el = new XElement(NS + "seg");
            bool any = false;
            foreach (var c in SegCols)
            {
                string v = row.Cells[c].Value?.ToString() ?? "";
                if (v == "") continue;
                // "" typed literally = explicitly BLANK fallback (an absent
                // default blocks; default="" falls back to empty). A leading
                // backslash escapes: \"" = the literal text ""
                if (EmptyOkCols.Contains(c))
                    v = v == "\"\"" ? "" : v.StartsWith('\\') ? v[1..] : v;
                el.SetAttributeValue(c, v);
                any = true;
            }
            if (any) parent.Add(el);
        }
    }
}
