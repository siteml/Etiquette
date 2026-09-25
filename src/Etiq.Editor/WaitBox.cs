namespace Etiq.Editor;

/// <summary>
/// The "printing…" popup. Two phases, one window:
/// 1. Sending — the seconds the UI thread is busy rendering and spooling a
///    batch: painted once (no message pump runs, so a second click cannot
///    sneak in), wait cursor on the owner.
/// 2. Printing — after the spool call returns the window stays up, now
///    with a live message pump, until PrintQueueGuard reports our job
///    gone from the queue (plus its short hold); then it closes itself.
///    The operator sees WHAT is printing and WHERE without reading the
///    status line, and the popup vanishing is the "you may print again".
/// </summary>
internal sealed class WaitBox : IDisposable
{
    private readonly Form _f;
    private readonly Label _lbl;
    private readonly Control? _owner;
    private readonly Cursor? _cursor;
    private bool _watching;

    private WaitBox(IWin32Window owner, string text)
    {
        _f = new Form
        {
            Text = "Printing", ClientSize = new Size(360, 90),
            FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false,
        };
        Ui.AutoScale(_f);
        _lbl = new Label
        {
            Text = text, Left = 16, Top = 16, Width = 328, Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var bar = new ProgressBar { Left = 16, Top = 60, Width = 328, Height = 16, Style = ProgressBarStyle.Marquee };
        _f.Controls.AddRange(new Control[] { _lbl, bar });
        _owner = owner as Control;
        if (_owner is not null && !_owner.IsDisposed) { _cursor = _owner.Cursor; _owner.Cursor = Cursors.WaitCursor; }
        // CenterParent only works for ShowDialog — place it by hand over the
        // owner's top-level window (owner may be a panel button, not the form)
        var host = _owner?.FindForm();
        if (host is not null && !host.IsDisposed)
        {
            var b = host.Bounds;
            _f.Location = new Point(b.Left + (b.Width - _f.Width) / 2, b.Top + (b.Height - _f.Height) / 2);
        }
        else _f.StartPosition = FormStartPosition.CenterScreen;
        _f.Show(owner);
        _f.Refresh();
    }

    /// <summary>Phase 1: show now, painted once, before the blocking work.</summary>
    public static WaitBox Show(IWin32Window owner, string text) => new(owner, text);

    /// <summary>Phase 2: the spool call has returned — restore the cursor,
    /// swap the text, and keep the window up until PrintQueueGuard says
    /// our job has left the queue (closes immediately when it already
    /// has). Dispose() afterwards is harmless.</summary>
    public void WatchQueue(string text)
    {
        RestoreCursor();
        _lbl.Text = text;
        _f.Refresh();
        if (!PrintQueueGuard.OwnBusy) { Dispose(); return; }
        _watching = true;
        PrintQueueGuard.Changed += OnQueueChanged;
    }

    private void OnQueueChanged()
    {
        if (PrintQueueGuard.OwnBusy) return;
        Dispose();
    }

    private void RestoreCursor()
    {
        if (_owner is not null && !_owner.IsDisposed) _owner.Cursor = _cursor ?? Cursors.Default;
    }

    public void Dispose()
    {
        if (_watching) { PrintQueueGuard.Changed -= OnQueueChanged; _watching = false; }
        RestoreCursor();
        if (_f.IsDisposed) return;
        _f.Close();
        _f.Dispose();
    }
}
