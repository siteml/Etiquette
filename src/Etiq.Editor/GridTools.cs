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
        { "value", "ref", "sep", "map", "default", "start", "len", "format", "case", "pad", "if-empty" };

    /// <summary>Add the seg columns (newline checkbox + attributes) to a
    /// fresh grid, with the sep-BEFORE tooltip.</summary>
    public static void AddSegColumns(DataGridView g)
    {
        g.Columns.Add(new DataGridViewCheckBoxColumn
            { Name = "newline", HeaderText = "newline", FillWeight = 55 });
        foreach (var c in SegCols)
            g.Columns.Add(c, c == "sep" ? "sep BEFORE" : c);
        g.Columns["sep"]!.ToolTipText =
            "Separator emitted BEFORE this segment's own value - and only " +
            "when this segment is non-empty AND the line already has content. " +
            "Put \", \" on the State row (not the City row) to get \"City, State\". " +
            "Spaces are kept.";
    }

    /// <summary>Fill a seg grid from the etiq:seg children of parent
    /// (a compose field, or one etiq:variant).</summary>
    public static void LoadSegs(DataGridView g, XElement parent)
    {
        g.Rows.Clear();
        foreach (var s in parent.Elements(NS + "seg"))
        {
            var vals = new object[SegCols.Length + 1];
            vals[0] = (string?)s.Attribute("newline") == "true";
            for (int i = 0; i < SegCols.Length; i++)
                vals[i + 1] = (string?)s.Attribute(SegCols[i]) ?? "";
            g.Rows.Add(vals);
        }
    }

    /// <summary>Replace parent's etiq:seg children with the grid's rows.</summary>
    public static void CommitSegs(DataGridView g, XElement parent)
    {
        g.EndEdit();
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
                el.SetAttributeValue(c, v);
                any = true;
            }
            if (any) parent.Add(el);
        }
    }
}
