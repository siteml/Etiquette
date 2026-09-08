using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace Etiq.Editor;

/// <summary>
/// GDI print controller for LABEL printers: one DEVMODE at CreateDC, then
/// StartDoc → (StartPage / EndPage) × N → EndDoc — the loop labelprint
/// runs, and the one every label driver is tested against.
///
/// Why not StandardPrintController: it calls ResetDC(hdc, devmode) before
/// EVERY page. Office drivers shrug; Zebra ZDesigner treats a mid-job
/// ResetDC as a new form — the printer stops between labels (tear-off
/// backfeed, re-feed, on some heads a skipped label) instead of running
/// the job as one continuous set. Sheet mode keeps the standard controller.
/// </summary>
internal sealed class LabelPrintController : PrintController
{
    private IntPtr _dc;
    private Graphics? _graphics;

    public override void OnStartPrint(PrintDocument document, PrintEventArgs e)
    {
        base.OnStartPrint(document, e);
        // the ONE DEVMODE this job runs under: the document's page settings
        // (label form, orientation, copies=1) merged over the driver's own
        IntPtr hDevMode = document.PrinterSettings.GetHdevmode(document.DefaultPageSettings);
        try
        {
            IntPtr pDevMode = GlobalLock(hDevMode);
            try
            {
                _dc = CreateDCW("WINSPOOL", document.PrinterSettings.PrinterName, null, pDevMode);
            }
            finally { GlobalUnlock(hDevMode); }
        }
        finally { GlobalFree(hDevMode); }
        if (_dc == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDC failed for '{document.PrinterSettings.PrinterName}'.");

        var di = new DOCINFOW
        {
            cbSize = Marshal.SizeOf<DOCINFOW>(),
            lpszDocName = document.DocumentName,
        };
        if (StartDocW(_dc, ref di) <= 0)
        {
            int err = Marshal.GetLastWin32Error();
            DeleteDC(_dc); _dc = IntPtr.Zero;
            throw new InvalidOperationException($"StartDoc failed (Win32 {err}).");
        }
    }

    public override Graphics OnStartPage(PrintDocument document, PrintPageEventArgs e)
    {
        base.OnStartPage(document, e);
        if (StartPage(_dc) <= 0)
            throw new InvalidOperationException($"StartPage failed (Win32 {Marshal.GetLastWin32Error()}).");
        _graphics = Graphics.FromHdc(_dc);
        return _graphics;
    }

    public override void OnEndPage(PrintDocument document, PrintPageEventArgs e)
    {
        _graphics?.Dispose();
        _graphics = null;
        if (EndPage(_dc) <= 0)
            throw new InvalidOperationException($"EndPage failed (Win32 {Marshal.GetLastWin32Error()}).");
        base.OnEndPage(document, e);
    }

    public override void OnEndPrint(PrintDocument document, PrintEventArgs e)
    {
        try
        {
            if (_dc != IntPtr.Zero)
            {
                if (e.Cancel) AbortDoc(_dc); else EndDoc(_dc);
                DeleteDC(_dc);
                _dc = IntPtr.Zero;
            }
        }
        finally { base.OnEndPrint(document, e); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszOutput;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDatatype;
        public int fwType;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocW(IntPtr hdc, ref DOCINFOW di);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int AbortDoc(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr h);
}
