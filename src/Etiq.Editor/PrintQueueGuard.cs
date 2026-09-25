namespace Etiq.Editor;

/// <summary>
/// "Is our last job still in the queue?" — the answer that keeps impatient
/// operators from sending a 100-label batch three times. PrintService
/// registers every job it spools (printer + DocumentName + label count);
/// this polls the spooler until the job is gone, and while any job is in
/// flight <see cref="Busy"/> is true: PrintService refuses new jobs and
/// MainForm greys its print buttons. A job that errors (paper out, cover
/// open) STAYS in the queue, so Busy stays true until someone clears or
/// resumes it — which is right: more jobs behind it would only jam harder.
/// The lock is released <see cref="ReleaseDelayMs"/> after the job has
/// left the queue — the spooler is done then, the printer is only just
/// starting. Safety valve: a job still queued after <see cref="MaxWaitMs"/>
/// is forgotten (spooler stuck, printer gone) so the app never locks forever.
///
/// Shared printers: the data panel's printer is also WATCHED — any job in
/// its queue, from another station or another program, counts as busy
/// too (buttons grey, status says so), and PrintService refuses to add a
/// job behind foreign ones. One queue, one job at a time, whoever sent it.
/// </summary>
internal static class PrintQueueGuard
{
    private const int PollMs = 1500, MaxWaitMs = 20 * 60 * 1000;
    // the job leaving the queue means the spooler handed it over, not that
    // the printer has started: hold the lock a moment longer so a click
    // right then does not queue a second batch behind a printer still
    // buffering the first
    private const int ReleaseDelayMs = 3000;

    private sealed record Job(string Printer, string DocumentName, int Labels, DateTime Started);
    private static readonly List<Job> _jobs = new();
    private static SynchronizationContext? _ui;

    /// <summary>Raised (on the UI thread that first called Track) whenever
    /// a job starts or leaves the queue.</summary>
    public static event Action? Changed;

    public static bool Busy { get { lock (_jobs) return _jobs.Count > 0 || _foreign > 0; } }

    /// <summary>Our own jobs only (no foreign-queue watch): what the
    /// printing popup waits on.</summary>
    public static bool OwnBusy { get { lock (_jobs) return _jobs.Count > 0; } }

    // the watched (panel) printer and how many jobs its queue holds
    private static string? _watched;
    private static int _foreign;
    private static CancellationTokenSource? _watchCts;
    private const int WatchPollMs = 3000;

    /// <summary>Watch a printer's whole queue (null = stop). Called when the
    /// data panel is built or its printer pick changes.</summary>
    public static void WatchPrinter(string? printer)
    {
        _ui ??= SynchronizationContext.Current;
        if (string.Equals(printer, _watched, StringComparison.OrdinalIgnoreCase) && _watchCts is not null) return;
        _watchCts?.Cancel();
        _watchCts = null;
        _watched = printer;
        bool was = _foreign > 0;
        _foreign = 0;
        if (was) Notify();
        if (printer is null) return;
        var cts = _watchCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                int n = 0;
                try { n = Math.Max(0, SpoolWatcher.QueueJobCount(printer)); } catch { n = 0; }
                if (!cts.IsCancellationRequested && n != _foreign) { _foreign = n; Notify(); }
                try { await Task.Delay(WatchPollMs, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        });
    }

    /// <summary>Why a print to `printer` must wait right now (a fresh queue
    /// read, not the poll), or null when it may go.</summary>
    public static string? WhyBlocked(string printer)
    {
        if (Describe() is { } own) return own;
        int n;
        try { n = SpoolWatcher.QueueJobCount(printer); } catch { n = 0; }
        return n > 0
            ? $"Printer {printer} is busy — {n} job{(n == 1 ? "" : "s")} in its queue from another program or station; wait for it to finish"
            : null;
    }

    /// <summary>Labels still owed by in-flight jobs (sum), for the status line.</summary>
    public static int LabelsInFlight { get { lock (_jobs) return _jobs.Sum(j => j.Labels); } }

    /// <summary>What the operator should read while waiting, or null when idle.</summary>
    public static string? Describe()
    {
        lock (_jobs)
        {
            if (_jobs.Count > 0)
            {
                var j = _jobs[0];
                int n = _jobs.Sum(x => x.Labels);
                return $"Printing {n} label{(n == 1 ? "" : "s")} on {j.Printer} — wait for the printer to start before printing again";
            }
        }
        if (_foreign > 0 && _watched is not null)
            return $"Printer {_watched} is busy — {_foreign} job{(_foreign == 1 ? "" : "s")} in its queue from another program or station; wait for it to finish";
        return null;
    }

    public static void Track(string printer, string documentName, int labels)
    {
        _ui ??= SynchronizationContext.Current;
        var job = new Job(printer, documentName, labels, DateTime.UtcNow);
        lock (_jobs) _jobs.Add(job);
        Notify();
        Task.Run(async () =>
        {
            bool seen = false;
            try
            {
                while ((DateTime.UtcNow - job.Started).TotalMilliseconds < MaxWaitMs)
                {
                    int? status = null;
                    try { status = SpoolWatcher.FindJobStatus(printer, documentName); } catch { /* spooler hiccup: keep waiting */ }
                    if (status is null)
                    {
                        // gone — printed, or cancelled by hand; either way the
                        // queue is free again. Allow one poll for it to appear.
                        if (seen || (DateTime.UtcNow - job.Started).TotalMilliseconds > 2 * PollMs) break;
                    }
                    else seen = true;
                    await Task.Delay(PollMs).ConfigureAwait(false);
                }
                await Task.Delay(ReleaseDelayMs).ConfigureAwait(false);
            }
            finally
            {
                lock (_jobs) _jobs.Remove(job);
                Notify();
            }
        });
    }

    private static void Notify()
    {
        if (_ui is not null) _ui.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }
}
