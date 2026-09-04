using Etiq.Core;
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
    /// <summary>Per-PRINTER print nudge in mils (settings.json key
    /// "printOffset:<printer name>" = "x,y"; Help > Options). Positive =
    /// right / down. Corrects a driver whose reported hard margin does not
    /// match where the image actually lands (tape printers: a few mils
    /// low). Looked up at print time for the printer the job goes to.</summary>
    public static (int X, int Y) GetOffset(string printer)
    {
        string? v = UpdateChecker.GetSetting("printOffset:" + printer);
        if (v is null) return (0, 0);
        var parts = v.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
            ? (x, y) : (0, 0);
    }

    public static void SetOffset(string printer, int x, int y) =>
        UpdateChecker.SetSetting("printOffset:" + printer, x == 0 && y == 0 ? null : $"{x},{y}");

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
        => PrintBatch(owner, doc, pages, measurer, direct: false, printer: null);

    /// <summary>direct=true skips the system print dialog entirely
    /// (labelprint behavior — etiq:panel print="direct"): the job goes to
    /// `printer` when named, else the machine default. Copies/collation
    /// are expanded into PAGES by the caller, never left to the driver.</summary>
    public static void PrintBatch(IWin32Window owner, EditorDoc doc,
                                  IReadOnlyList<IReadOnlyDictionary<string, string>?> pages,
                                  ITextMeasurer measurer, bool direct, string? printer)
    {
        if (pages.Count == 0) return;
        var vb = doc.ViewBox;
        if (vb.W <= 0 || vb.H <= 0)
        {
            MessageBox.Show(owner, "Template has no viewBox — cannot size the page.", "Print");
            return;
        }

        // job identity: the log correlates every event through this id, and
        // the spool watcher finds the job in the queue by the DocumentName
        string job = Guid.NewGuid().ToString("N")[..12];
        string template = doc.Path is null ? "(unsaved)" : Path.GetFileNameWithoutExtension(doc.Path);
        using var pd = new PrintDocument
        {
            DocumentName = $"Etiquette {template} [{job}]",
        };
        // PAGE = LABEL. Without an explicit paper size the driver prints on
        // its default form (a Brother tape driver: 18 mm x 100 mm; an
        // office printer: Letter) and the label lands wherever that puts
        // it. World units are mils, PaperSize wants hundredths of an inch.
        // The form is declared PORTRAIT (short side = width, which for a
        // tape printer is the tape) and Landscape flips the drawing onto it
        // for wide labels — the same rotation every other application asks
        // the driver for.
        int shortSide = (int)Math.Round(Math.Min(vb.W, vb.H) / 10.0);
        int longSide = (int)Math.Round(Math.Max(vb.W, vb.H) / 10.0);
        var paper = new PaperSize("Etiquette label", shortSide, longSide);   // custom (Kind = Custom)
        foreach (var ps in new[] { pd.DefaultPageSettings, pd.PrinterSettings.DefaultPageSettings })
        {
            ps.PaperSize = paper;
            ps.Margins = new Margins(0, 0, 0, 0);
            ps.Landscape = vb.W > vb.H;
        }
        int page = 0;
        int offX = 0, offY = 0;   // set once the printer is known (below)
        // per PAGE, after the driver's own DEVMODE has been applied: some
        // drivers (Brother P-touch once its Preferences were OK'd) reassert
        // their stored form between pages — this is the last word
        pd.QueryPageSettings += (_, e) =>
        {
            e.PageSettings.PaperSize = paper;
            e.PageSettings.Margins = new Margins(0, 0, 0, 0);
            e.PageSettings.Landscape = vb.W > vb.H;
        };
        pd.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;   // 1/100 inch
            // origin at the physical page corner, not the printable margin
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            g.ScaleTransform(0.1f, 0.1f);        // world = mils (1/1000 in)
            g.TranslateTransform((float)(offX - vb.X), (float)(offY - vb.Y));
            LabelRenderer.Draw(g, doc, pages[page], measurer);
            page++;
            e.HasMorePages = page < pages.Count;
        };

        if (direct)
        {
            if (!string.IsNullOrWhiteSpace(printer))
                pd.PrinterSettings.PrinterName = printer;
            if (!pd.PrinterSettings.IsValid)
            {
                MessageBox.Show(owner,
                    printer is null
                        ? "No valid default printer is configured on this machine."
                        : $"Printer '{printer}' was not found on this machine.",
                    "Print");
                return;
            }
        }
        else
        {
            using var dlg = new PrintDialog
            {
                Document = pd, UseEXDialog = true, AllowSomePages = false,
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        }
        try
        {
            // a printer change (direct: PrinterName; dialog: user pick)
            // resets the page settings to THAT printer's default form —
            // re-assert the label size. (The template is the page size;
            // a paper choice made in the dialog is deliberately overridden.)
            pd.DefaultPageSettings.PaperSize = paper;
            pd.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            pd.DefaultPageSettings.Landscape = vb.W > vb.H;
            (offX, offY) = GetOffset(pd.PrinterSettings.PrinterName);
            page = 0;
            pd.Print();
            // spooled ≠ printed: log each label's values (the reprintable
            // record), then watch the queue for the job's real fate
            string printerName = pd.PrinterSettings.PrinterName;
            for (int i = 0; i < pages.Count; i++)
                PrintLog.Append(job, "spooled", template, printerName,
                                page: i + 1, pages: pages.Count, values: pages[i]);
            if (PrintLog.Directory is not null)
                SpoolWatcher.Watch(printerName, pd.DocumentName, job, template);
        }
        catch (Exception ex)
        {
            PrintLog.Append(job, "error", template, pd.PrinterSettings.PrinterName,
                            detail: "print call failed: " + ex.Message);
            MessageBox.Show(owner, ex.Message, "Print failed");
        }
    }
}
