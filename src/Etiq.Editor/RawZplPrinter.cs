using Etiq.Core;
using Etiq.Editor.Core;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Etiq.Editor;

/// <summary>
/// The `zpl-raster` transport (config/printers.json `path`): the SAME
/// LabelRenderer that draws the canvas, the preview and the driver page
/// draws each label into a 1-bit raster at the printer's head density,
/// ZplRaster wraps it in ^GFA with Zebra's alternative compression
/// (G–Y / g–z counts, ',' / '!' rest-of-row, ':' repeat row — the scheme
/// in the shop's own BarTender captures), and the whole batch goes to the
/// Windows queue as ONE RAW document through winspool (no port 9100: the
/// label printers are USB-local).
///
/// Why: the Seagull driver for the 105Se sends every graphic as plain hex
/// with no compression option, and over a USB 1.1 → IEEE-1284 adapter the
/// host cannot keep up with the printhead — the printer stalls between
/// labels. The compressed stream is ~20x smaller. Z64 was tried first;
/// the 105Se firmware predates it.
///
/// WYSIWYG by construction: no ZPL fonts or barcode commands are used,
/// only the rendered dots, so what the preview shows is what prints.
/// </summary>
internal static class RawZplPrinter
{
    /// <summary>What was sent, for the Last Print Details line.</summary>
    public sealed record Result(int Width, int Height, int Rotate, int Blocks, int Bytes, string? Dump);

    /// <summary>Where the last job's raster (PNG of the FIRST label, as
    /// sent) and the full ZPL stream are written for inspection: the print
    /// log directory when logging is on, else %TEMP%. Overwritten per job.</summary>
    public static string DumpDirectory => PrintLog.Directory is { Length: > 0 } d ? d : Path.GetTempPath();

    /// <summary>Render + send. pages = one entry per physical label (copies
    /// already expanded by PrintService). Throws on any failure — the
    /// caller logs and shows it.</summary>
    public static Result Print(EditorDoc doc,
                               IReadOnlyList<IReadOnlyDictionary<string, string>?> pages,
                               ITextMeasurer measurer, PrinterDef def, string queue,
                               string documentName, (int X, int Y) offset, bool compress = true)
    {
        var vb = doc.ViewBox;
        double dotsPerMil = def.DotsPerMmEffective * 25.4 / 1000.0;   // 8 dots/mm → 0.2032 dots per mil
        int labelW = Math.Max(1, (int)Math.Round(vb.W * dotsPerMil));
        int labelH = Math.Max(1, (int)Math.Round(vb.H * dotsPerMil));
        // rotation: explicit from the settings/registry, else the flip the
        // driver path gets from Landscape — a wide label that only fits the
        // head edge-first is turned 90° COUNTER-clockwise onto the feed
        // (270 here; verified against the driver's output on the 105Se)
        int rotate = def.Rotate ?? (vb.W > def.WidthMils && vb.H <= def.WidthMils ? 270 : 0);
        bool swap = rotate is 90 or 270;
        int w = swap ? labelH : labelW, h = swap ? labelW : labelH;

        var labels = new List<byte[]>(pages.Count);
        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            foreach (var values in pages)
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    // transforms PREPEND: a world point is offset, scaled to
                    // dots, rotated, then moved back onto the bitmap
                    switch (rotate)
                    {
                        case 90:  g.TranslateTransform(w, 0); g.RotateTransform(90);  break;
                        case 180: g.TranslateTransform(w, h); g.RotateTransform(180); break;
                        case 270: g.TranslateTransform(0, h); g.RotateTransform(-90); break;
                    }
                    g.ScaleTransform((float)dotsPerMil, (float)dotsPerMil);   // world = mils
                    // translation rounded to WHOLE DOTS so world 0 lands on a
                    // dot boundary — the renderer's dot-snapped barcodes then
                    // fill exact dots (no half-covered edge pixels)
                    g.TranslateTransform(
                        (float)(Math.Round((offset.X - vb.X) * dotsPerMil) / dotsPerMil),
                        (float)(Math.Round((offset.Y - vb.Y) * dotsPerMil) / dotsPerMil));
                    LabelRenderer.Draw(g, doc, values, measurer, dotPitch: 1.0 / dotsPerMil);
                }
                if (labels.Count == 0) TryDumpPng(bmp);   // what the printer gets, before 1-bit
                labels.Add(ToMono(bmp));
            }
        }

        // ^PW pins the print width to the raster; anything else about the
        // media/mode is the printer's own setting unless the registry says
        string prefix = $"^PW{w}" + (def.Zpl ?? "");
        string zpl = ZplRaster.BuildBatch(labels, w, h, prefix, compress);
        byte[] bytes = Encoding.Latin1.GetBytes(zpl);
        string? dump = TryDumpZpl(bytes);
        WriteRaw(queue, documentName, bytes);
        int blocks = zpl.Split("^XA").Length - 1;
        return new Result(w, h, rotate, blocks, bytes.Length, dump);
    }

    private static void TryDumpPng(Bitmap bmp)
    {
        try { bmp.Save(Path.Combine(DumpDirectory, "etiquette-raster-last.png"), ImageFormat.Png); }
        catch { /* diagnostics only */ }
    }

    private static string? TryDumpZpl(byte[] bytes)
    {
        try
        {
            string path = Path.Combine(DumpDirectory, "etiquette-raster-last.zpl");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch { return null; }
    }

    /// <summary>24bpp bitmap → 1-bit rows (MSB first, 1 = black) via the
    /// shared ZplRaster threshold: fixed 50 % luma, no dithering — text and
    /// bars on label stock are hard black/white and dithering would break
    /// barcode edges.</summary>
    private static byte[] ToMono(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var rgb = new byte[w * h * 3];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, rgb, y * w * 3, w * 3);
        }
        finally { bmp.UnlockBits(data); }
        // GDI+ 24bpp is B,G,R — luma weights are symmetric enough that
        // ToMono's R,G,B order only matters for colour, and this is mono
        return ZplRaster.ToMono(rgb, w, h);
    }

    /// <summary>One RAW spool document on the queue: OpenPrinter →
    /// StartDocPrinter(RAW) → StartPagePrinter → WritePrinter (whole
    /// batch) → EndPagePrinter → EndDocPrinter. The document name is the
    /// same "Etiquette &lt;template&gt; [job]" the spool watcher looks for.</summary>
    private static void WriteRaw(string queue, string documentName, byte[] bytes)
    {
        if (!OpenPrinterW(queue, out IntPtr hp, IntPtr.Zero))
            throw new InvalidOperationException($"OpenPrinter failed for '{queue}' (Win32 {Marshal.GetLastWin32Error()}).");
        try
        {
            var di = new DOC_INFO_1W { pDocName = documentName, pOutputFile = null, pDatatype = "RAW" };
            if (StartDocPrinterW(hp, 1, ref di) == 0)
                throw new InvalidOperationException($"StartDocPrinter failed (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!StartPagePrinter(hp))
                    throw new InvalidOperationException($"StartPagePrinter failed (Win32 {Marshal.GetLastWin32Error()}).");
                try
                {
                    // the spooler takes the whole buffer; loop anyway in
                    // case it accepts a short write
                    int done = 0;
                    while (done < bytes.Length)
                    {
                        byte[] chunk = done == 0 ? bytes : bytes[done..];
                        if (!WritePrinter(hp, chunk, chunk.Length, out int written) || written <= 0)
                            throw new InvalidOperationException($"WritePrinter failed after {done} bytes (Win32 {Marshal.GetLastWin32Error()}).");
                        done += written;
                    }
                }
                finally { EndPagePrinter(hp); }
            }
            finally { EndDocPrinter(hp); }
        }
        finally { ClosePrinter(hp); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1W
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinterW(IntPtr hPrinter, int level, ref DOC_INFO_1W pDocInfo);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);
}
