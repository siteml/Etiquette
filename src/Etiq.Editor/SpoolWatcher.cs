using System.Runtime.InteropServices;
using Etiq.Core;

namespace Etiq.Editor;

/// <summary>
/// Answers the question GDI cannot: did the job actually LEAVE the queue?
/// A successful PrintDocument.Print() only means "spooled" — the label can
/// still sit in the queue forever (printer off, cover open, out of tape).
/// This watcher polls the spooler (winspool EnumJobs, no packages) for the
/// job by its unique DocumentName and appends the outcome to the print log:
///   completed — the job left the queue without an error flag
///   error     — the queue reported Error/Offline/PaperOut/Blocked
///   stuck     — still queued when the watch window ran out
/// Best-effort like the log itself: any watcher failure is logged as
/// detail, never surfaced as a print failure.
/// </summary>
internal static class SpoolWatcher
{
    private const int PollMs = 2000, WindowMs = 60000;

    // JOB_STATUS_* flags that mean "needs a human"
    private const int BadStatus = 0x0002 /*ERROR*/ | 0x0020 /*OFFLINE*/ |
                                  0x0040 /*PAPEROUT*/ | 0x0200 /*BLOCKED_DEVQ*/ |
                                  0x0800 /*USER_INTERVENTION*/;

    public static void Watch(string printer, string documentName, string job, string? template)
    {
        Task.Run(async () =>
        {
            try
            {
                bool seen = false;
                for (int elapsed = 0; elapsed <= WindowMs; elapsed += PollMs)
                {
                    var status = FindJobStatus(printer, documentName);
                    if (status is null)
                    {
                        if (seen || elapsed >= PollMs)   // was there (or had time to appear) and is gone
                        {
                            PrintLog.Append(job, "completed", template, printer);
                            return;
                        }
                    }
                    else
                    {
                        seen = true;
                        if ((status.Value & BadStatus) != 0)
                        {
                            PrintLog.Append(job, "error", template, printer,
                                detail: $"queue status 0x{status.Value:X}");
                            return;   // one verdict per job; the queue keeps the rest of the story
                        }
                    }
                    await Task.Delay(PollMs).ConfigureAwait(false);
                }
                PrintLog.Append(job, "stuck", template, printer,
                    detail: $"still queued after {WindowMs / 1000}s");
            }
            catch (Exception ex)
            {
                PrintLog.Append(job, "stuck", template, printer, detail: "watcher failed: " + ex.Message);
            }
        });
    }

    /// <summary>The job's status flags, or null when no job with that
    /// document name is in the queue.</summary>
    private static int? FindJobStatus(string printer, string documentName)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) return null;
        try
        {
            EnumJobs(h, 0, 255, 1, IntPtr.Zero, 0, out int needed, out _);
            if (needed == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobs(h, 0, 255, 1, buf, needed, out _, out int count)) return null;
                int size = Marshal.SizeOf<JOB_INFO_1>();
                for (int i = 0; i < count; i++)
                {
                    var ji = Marshal.PtrToStructure<JOB_INFO_1>(buf + i * size);
                    if (documentName.Equals(ji.pDocument, StringComparison.Ordinal))
                        return ji.Status;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_1
    {
        public int JobId;
        public string pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public int Status, Priority, Position, TotalPages, PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public short wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobs(IntPtr hPrinter, int firstJob, int noJobs, int level,
        IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);
}
