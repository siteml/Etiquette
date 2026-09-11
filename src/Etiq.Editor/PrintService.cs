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
    /// <summary>A sheet (office) printer, judged by its DEFAULT form: short
    /// side ≥ 7 in (A5 and up). Label and tape printers default to their
    /// stock (4 in wide industrial heads, 18 mm tape…) and accept custom
    /// forms; office drivers list "User Defined" too but then park a
    /// label-sized page in a corner of the sheet, so the form list is not
    /// a usable signal.</summary>
    private static bool IsSheetPrinter(PrinterSettings ps)
    {
        try
        {
            // a FRESH settings object: the document's own PrinterSettings has
            // already had the label form pushed into DefaultPageSettings
            // above, so reading it back would only echo our 6×4 request
            var fresh = new PrinterSettings { PrinterName = ps.PrinterName };
            if (!fresh.IsValid) return false;
            var dflt = fresh.DefaultPageSettings.PaperSize;
            return Math.Min(dflt.Width, dflt.Height) >= 700;   // hundredths of an inch
        }
        catch { return false; }
    }

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
    /// per list row" path). Copies chosen in the print dialog multiply the
    /// whole batch — expanded into pages here, never left to the driver.</summary>
    public static void PrintBatch(IWin32Window owner, EditorDoc doc,
                                  IReadOnlyList<IReadOnlyDictionary<string, string>?> pages,
                                  ITextMeasurer measurer)
        => PrintBatch(owner, doc, pages, measurer, direct: false, printer: null);

    /// <summary>What the last job actually asked the driver for — shown on
    /// the data-panel status line so paging surprises can be diagnosed.</summary>
    public static string? LastInfo { get; private set; }

    /// <summary>direct=true skips the system print dialog entirely
    /// (labelprint behavior — etiq:panel print="direct"): the job goes to
    /// `printer` when named, else the machine default. `copies` repeats
    /// the whole batch that many times (collated) — expanded into PAGES
    /// here, never left to the driver, and logged as a copies FIELD on
    /// each record, not as extra rows. A dialog print multiplies further
    /// by whatever the user picks there.</summary>
    public static void PrintBatch(IWin32Window owner, EditorDoc doc,
                                  IReadOnlyList<IReadOnlyDictionary<string, string>?> pages,
                                  ITextMeasurer measurer, bool direct, string? printer,
                                  int copies = 1)
    {
        LastInfo = null;
        if (pages.Count == 0) return;
        var records = pages;   // the distinct labels, as logged — pages may be expanded
        if (copies > 1)
        {
            var expanded = new List<IReadOnlyDictionary<string, string>?>(pages.Count * copies);
            for (int c = 0; c < copies; c++) expanded.AddRange(pages);
            pages = expanded;
        }
        else copies = 1;
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
        // SHEET mode: an office printer (Letter/A4 forms, nothing near the
        // label size) cannot make a 6×4 page — asked for one, the driver
        // prints on its default sheet and parks the "page" wherever it
        // likes (right-aligned, half in the unprintable margin). Detected
        // once the printer is known: keep the sheet, pick the orientation
        // the label fits, place it top-left of the printable area (works on
        // any paper size) and draw a hairline cut outline.
        bool sheet = false, sheetLandscape = false;
        // per PAGE, after the driver's own DEVMODE has been applied: some
        // drivers (Brother P-touch once its Preferences were OK'd) reassert
        // their stored form between pages — this is the last word
        pd.QueryPageSettings += (_, e) =>
        {
            e.PageSettings.Margins = new Margins(0, 0, 0, 0);
            if (sheet) { e.PageSettings.Landscape = sheetLandscape; return; }   // the sheet's own form stands
            e.PageSettings.PaperSize = paper;
            e.PageSettings.Landscape = vb.W > vb.H;
        };
        pd.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;   // 1/100 inch
            // label printer: origin at the PHYSICAL page corner (the page is
            // the label; the driver's hard margin is a lie for stock).
            // sheet printer: origin at the PRINTABLE corner — top-left, so
            // the label lands whole on any paper size, never in the dead zone.
            if (!sheet) g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            g.ScaleTransform(0.1f, 0.1f);        // world = mils (1/1000 in)
            if (sheet)
            {
                using var cut = new Pen(Color.Gray, 4f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawRectangle(cut, offX, offY, (float)vb.W, (float)vb.H);
            }
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
            // COPIES are ours, not the driver's. The dialog writes them into
            // DEVMODE dmCopies; office and PDF drivers honor that, label
            // drivers (Zebra ZDesigner, Seagull) ignore it and print once
            // — their copies live in their own Options tab. Expand the
            // count into pages (collated: whole batch repeated; uncollated:
            // each label repeated) and hand the driver a 1-copy job, so
            // every printer behaves the same. The LOG still gets one row per
            // distinct label (with a copies field), not one per physical copy.
            int dlgCopies = pd.PrinterSettings.Copies;
            if (dlgCopies > 1)
            {
                var expanded = new List<IReadOnlyDictionary<string, string>?>(pages.Count * dlgCopies);
                if (pd.PrinterSettings.Collate)
                    for (int c = 0; c < dlgCopies; c++) expanded.AddRange(pages);
                else
                    foreach (var p in pages)
                        for (int c = 0; c < dlgCopies; c++) expanded.Add(p);
                pages = expanded;   // captured by PrintPage — same variable
                pd.PrinterSettings.Copies = 1;
                copies *= dlgCopies;
            }
            // designed-for vs printing-at: the template may carry the head
            // density its dot grid was built on (etiq:view dots-per-mm); the
            // driver reports the queue's real resolution. Numbers only —
            // never a printer-name match (docs/grid-guides.md).
            double designed = doc.View.DotsPerMm;
            int dpiX = pd.PrinterSettings.DefaultPageSettings.PrinterResolution.X;
            if (designed > 0 && dpiX > 0)
            {
                double actual = dpiX / 25.4;
                if (Math.Abs(actual - designed) / designed > 0.02)
                {
                    var r = MessageBox.Show(owner,
                        $"This template was designed for {Num.F(Math.Round(designed, 2))} dots/mm " +
                        $"(≈{Num.F(Math.Round(designed * 25.4))} dpi)" +
                        (string.IsNullOrWhiteSpace(doc.View.Target) ? "" : $", {doc.View.Target}") +
                        $".\n'{pd.PrinterSettings.PrinterName}' prints at {Num.F(Math.Round(actual, 2))} dots/mm ({dpiX} dpi).\n\n" +
                        "Dot-aligned edges and barcode modules will land between dots. Print anyway?",
                        "Printer density mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
            }
        }
        try
        {
            // a printer change (direct: PrinterName; dialog: user pick)
            // resets the page settings to THAT printer's default form —
            // re-assert the label size. (The template is the page size;
            // a paper choice made in the dialog is deliberately overridden.)
            sheet = IsSheetPrinter(pd.PrinterSettings);
            pd.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            if (sheet)
            {
                // the driver's default form; orientation = the LABEL's, so a
                // proof on paper reads exactly like the label printer's
                // output (a wide label prints landscape, as on stock); fall
                // back to the other orientation only if it doesn't fit
                var dflt = new PrinterSettings { PrinterName = pd.PrinterSettings.PrinterName }
                    .DefaultPageSettings.PaperSize;
                pd.DefaultPageSettings.PaperSize = dflt;
                pd.PrinterSettings.DefaultPageSettings.PaperSize = dflt;
                bool wide = vb.W > vb.H;
                double pw = Math.Min(dflt.Width, dflt.Height) * 10.0, ph = Math.Max(dflt.Width, dflt.Height) * 10.0;
                bool fitsLandscape = vb.W <= ph && vb.H <= pw, fitsPortrait = vb.W <= pw && vb.H <= ph;
                bool landscape = UnitPrefs.SheetOrientation switch
                {
                    "portrait" => false,
                    "landscape" => true,
                    _ => wide ? fitsLandscape || !fitsPortrait : !fitsPortrait && fitsLandscape,
                };
                // push it EVERYWHERE the driver might read it from
                pd.DefaultPageSettings.Landscape = landscape;
                pd.PrinterSettings.DefaultPageSettings.Landscape = landscape;
                sheetLandscape = landscape;
            }
            else
            {
                pd.DefaultPageSettings.PaperSize = paper;
                pd.DefaultPageSettings.Landscape = vb.W > vb.H;
                // label stock: one DEVMODE for the whole job, no per-page
                // ResetDC — the standard controller's ResetDC before every
                // page makes Zebra stop/backfeed between labels instead of
                // running the set in one go (see LabelPrintController)
                pd.PrintController = new LabelPrintController();
            }
            (offX, offY) = GetOffset(pd.PrinterSettings.PrinterName);
            page = 0;
            var dps = pd.DefaultPageSettings;
            LastInfo = $"{(sheet ? "sheet" : "label")} mode → {pd.PrinterSettings.PrinterName}: form " +
                       (sheet ? $"(sheet orientation setting: {UnitPrefs.SheetOrientation}) " : "") +
                       $"{dps.PaperSize.PaperName} {dps.PaperSize.Width / 100.0:0.##}×{dps.PaperSize.Height / 100.0:0.##} in, " +
                       $"{(dps.Landscape ? "landscape" : "portrait")}, hard margin {dps.HardMarginX / 100.0:0.##}/{dps.HardMarginY / 100.0:0.##} in, " +
                       $"offset {offX}/{offY} mils, {pages.Count} page(s), " +
                       $"{(sheet ? "standard controller (ResetDC per page)" : "label controller (one DEVMODE, no ResetDC)")}" +
                       (copies > 1 ? $" ({copies} copies expanded, driver copies=1)" : "");
            pd.Print();
            // spooled ≠ printed: log each label's values (the reprintable
            // record), then watch the queue for the job's real fate
            string printerName = pd.PrinterSettings.PrinterName;
            for (int i = 0; i < records.Count; i++)
                PrintLog.Append(job, "spooled", template, printerName,
                                page: i + 1, pages: records.Count, values: records[i], copies: copies);
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
