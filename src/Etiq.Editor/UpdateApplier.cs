using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace Etiq.Editor;

/// <summary>
/// Applies an update in place: download the release zip, extract it, and
/// swap the new files into the install folder. The running exe can't be
/// deleted or overwritten — but Windows happily lets it be RENAMED — so
/// locked files are moved aside to *.etiqold, the fresh files take their
/// place, and the leftovers are removed on the next launch
/// (CleanupLeftovers, called from Program.Main). Because "updating" is
/// just "swapping files", this also handles switching publish flavors
/// (standalone ↔ framework-dependent) with no extra work.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Folder the running exe lives in. Environment.ProcessPath
    /// is correct even for single-file publishes (where assembly Location
    /// is empty).</summary>
    public static string? InstallDir =>
        Path.GetDirectoryName(Environment.ProcessPath);

    /// <summary>Can this process write to its own install folder? False
    /// under Program Files without elevation — the caller should fall
    /// back to a plain browser download.</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            try
            {
                string dir = InstallDir ?? "";
                if (dir == "") return false;
                string probe = Path.Combine(dir, $".etiq-probe-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Delete *.etiqold leftovers from a previous update. The
    /// instance that renamed them may still be exiting when we start, so
    /// this retries quietly in the background for a few seconds; anything
    /// that still won't go is picked up on a later launch.</summary>
    public static void CleanupLeftovers()
    {
        string? dir = InstallDir;
        if (dir is null) return;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                bool anyLeft = false;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(
                                 dir, "*.etiqold", SearchOption.AllDirectories))
                        try { File.Delete(f); }
                        catch { anyLeft = true; }
                }
                catch { return; }
                if (!anyLeft) return;
                await Task.Delay(2000);
            }
        });
    }

    /// <summary>Download the asset (with a small progress dialog owned by
    /// the main form), extract, sanity-check, and swap the files into the
    /// install folder. True = installed, ready to relaunch; false = the
    /// user cancelled. Throws on network / disk / layout problems — the
    /// caller decides how to present that.</summary>
    public static async Task<bool> DownloadAndInstallAsync(
        Form owner, string url, string assetName)
    {
        using var cts = new CancellationTokenSource();
        using var dlg = new ProgressForm(assetName);
        dlg.Cancelled += () => cts.Cancel();
        owner.Enabled = false;
        dlg.Show(owner);
        try
        {
            string zip = await DownloadAsync(url, dlg.Report, cts.Token);
            dlg.SetStatus("Installing…");
            string extracted = Path.Combine(Path.GetDirectoryName(zip)!, "extracted");
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(zip, extracted);
                if (!File.Exists(Path.Combine(extracted, "etiqedit.exe")))
                    throw new InvalidDataException(
                        "the downloaded zip does not contain etiqedit.exe");
                Apply(extracted);
            }, cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            owner.Enabled = true;
            dlg.Close();
        }
    }

    /// <summary>Start the (now updated) exe and close the app. Passes the
    /// open document path so the new instance comes back where the user
    /// left off.</summary>
    public static void RelaunchAndExit(Form owner, string? reopenDoc)
    {
        string dir = InstallDir ?? "";
        var psi = new ProcessStartInfo(Path.Combine(dir, "etiqedit.exe"))
        {
            UseShellExecute = true,
            WorkingDirectory = dir,
        };
        if (reopenDoc is not null) psi.ArgumentList.Add(reopenDoc);
        Process.Start(psi);
        owner.Close();
    }

    // ---------- internals ----------

    /// <summary>Stream the asset to %TEMP%\etiqedit-update\update.zip,
    /// reporting (bytesDone, bytesTotal|-1) as it goes.</summary>
    private static async Task<string> DownloadAsync(
        string url, Action<long, long> progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "etiqedit-update");
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch { /* stale but busy — files inside get overwritten anyway */ }
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "update.zip");

        // the standalone zip is large: no overall timeout, cancel governs
        using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("etiqedit-update-check");
        using var resp = await http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(zip);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            progress(done, total);
        }
        return zip;
    }

    /// <summary>Move every extracted file over its counterpart in the
    /// install folder. A file that refuses deletion (the running exe) is
    /// renamed aside to *.etiqold first — renaming a running exe is legal
    /// on Windows even though deleting it isn't.</summary>
    private static void Apply(string sourceDir)
    {
        string dest = InstallDir
            ?? throw new InvalidOperationException("install folder unknown");
        foreach (string src in Directory.EnumerateFiles(
                     sourceDir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dest, Path.GetRelativePath(sourceDir, src));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string aside = target + ".etiqold";
                    // a leftover from an earlier update may sit there; if
                    // IT is also stuck, pick a unique name instead
                    try { if (File.Exists(aside)) File.Delete(aside); }
                    catch { aside = $"{target}.{Environment.ProcessId}.etiqold"; }
                    File.Move(target, aside);
                }
            }
            File.Move(src, target);
        }
    }

    /// <summary>Download/install progress: status line, bar, Cancel. No
    /// close box — Cancel is the one way out, so the cancellation path is
    /// always the same.</summary>
    private sealed class ProgressForm : Form
    {
        private readonly Label _status = new() { Left = 12, Top = 12, Width = 356 };
        private readonly ProgressBar _bar = new()
            { Left = 12, Top = 38, Width = 356, Height = 18 };
        private string _lastText = "";

        public event Action? Cancelled;

        public ProgressForm(string assetName)
        {
            Text = "Updating etiqedit";
            ClientSize = new Size(380, 100);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ControlBox = false; ShowInTaskbar = false;
            _status.Text = $"Downloading {assetName}…";
            var cancel = new Button { Text = "Cancel", Left = 286, Top = 64, Width = 82 };
            cancel.Click += (_, _) => { cancel.Enabled = false; Cancelled?.Invoke(); };
            Controls.AddRange(new Control[] { _status, _bar, cancel });
        }

        /// <summary>Progress callback — runs on the UI thread (the await
        /// continuations in DownloadAsync post back here).</summary>
        public void Report(long done, long total)
        {
            string text;
            if (total > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 1000;
                _bar.Value = (int)Math.Min(1000, done * 1000 / total);
                text = $"Downloading… {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB";
            }
            else
            {
                _bar.Style = ProgressBarStyle.Marquee;
                text = $"Downloading… {done / 1048576.0:0.0} MB";
            }
            // label writes are cheap but not free — skip identical frames
            if (text != _lastText) { _lastText = text; _status.Text = text; }
        }

        public void SetStatus(string s)
        {
            _status.Text = s;
            _bar.Style = ProgressBarStyle.Marquee;
        }
    }
}
