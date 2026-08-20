namespace Etiq.Editor;

/// <summary>Tiny shared modal prompts (used by MainForm and the canvas
/// context menu).</summary>
internal static class Prompts
{
    public static string? PromptText(IWin32Window owner, string title, string initial,
                                     bool multiline = false)
    {
        int boxH = multiline ? 140 : 23;
        using var dlg = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, boxH + 64),
        };
        var tb = new TextBox
        {
            Left = 12, Top = 12, Width = 336, Height = boxH, Text = initial,
            Multiline = multiline, AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = boxH + 24, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Top = boxH + 24, Width = 75 };
        dlg.Controls.AddRange(new Control[] { tb, ok, cancel });
        if (!multiline) dlg.AcceptButton = ok; // multiline: Enter = new line
        dlg.CancelButton = cancel;
        tb.SelectAll();
        return dlg.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
