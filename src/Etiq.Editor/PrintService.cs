using System.Drawing.Printing;
using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>
/// Phase 3 native print path: renders the document through LabelRenderer
/// straight onto the printer driver's Graphics (GDI+, same engine as the
/// canvas — WYSIWYG by construction). No raw printer commands, no NuGet:
/// works on Zebra/Toshiba/office printers exactly like labelprint's GDI
/// mode does today.
/// </summary>
public static class PrintService
{
    /// <summary>One label. values=null prints the sample text as drawn
    /// (Design mode).</summary>
    public static void Print(IWin32Window owner, EditorDoc doc,
                             IReadOnlyDictionary<string, string>? values,
                             ITextMeasurer measurer)
        => PrintBatch(owner, doc, new[] { values }, measurer);

    /// <summary>Batch: one page per value set (the print-station "one label
    /// per list row" path). Driver-level copies multiply the whole batch.</summary>
    public static void PrintBatch(IWin32Window owner, EditorDoc doc,
                                  IReadOnlyList<IReadOnlyDictionary<string, string>?> pages,
                                  ITextMeasurer measurer)
    {
        if (pages.Count == 0) return;
        var vb = doc.ViewBox;
        if (vb.W <= 0 || vb.H <= 0)
        {
            MessageBox.Show(owner, "Template has no viewBox — cannot size the page.", "Print");
            return;
        }

        using var pd = new PrintDocument
        {
            DocumentName = pages.Count == 1 ? "Etiquette label"
                                            : $"Etiquette labels ({pages.Count})",
        };
        // label space is landscape-authored; let the driver rotate onto
        // portrait stock exactly like it does for every other application
        pd.DefaultPageSettings.Landscape = vb.W > vb.H;
        int page = 0;
        pd.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;   // 1/100 inch
            // origin at the physical page corner, not the printable margin
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            g.ScaleTransform(0.1f, 0.1f);        // world = mils (1/1000 in)
            g.TranslateTransform((float)-vb.X, (float)-vb.Y);
            LabelRenderer.Draw(g, doc, pages[page], measurer);
            page++;
            e.HasMorePages = page < pages.Count;
        };

        using var dlg = new PrintDialog
        {
            Document = pd, UseEXDialog = true, AllowSomePages = false,
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            page = 0;
            pd.Print();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Print failed");
        }
    }
}
