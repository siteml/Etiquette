namespace Etiq.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // sweep *.etiqold files a previous in-place update renamed aside
        UpdateApplier.CleanupLeftovers();
        // a WinExe dies invisibly on unhandled exceptions — always show them
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "etiqedit — unhandled exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "etiqedit — fatal exception");
        try
        {
            // --import-connections <bundle>: provision this machine's
            // connection store from a password-protected *.etiqcreds file
            // and exit (the IT rollout path — see ConnectionsDialog)
            int imp = Array.FindIndex(args, a =>
                a.Equals("--import-connections", StringComparison.OrdinalIgnoreCase));
            if (imp >= 0)
            {
                ConnectionsDialog.ImportInteractive(
                    imp + 1 < args.Length ? args[imp + 1] : "");
                return;
            }
            // provisioning nicety: a *.etiqcreds bundle sitting next to
            // the exe (IT copied it in with the install) is offered for
            // import on startup — and for deletion once imported, so the
            // encrypted bundle doesn't linger on the station forever
            foreach (var bundle in SafeFindBundles())
            {
                if (MessageBox.Show(
                        $"A connections bundle was found:\n\n    {Path.GetFileName(bundle)}\n\n" +
                        "Import it for this machine?", "Etiquette — connections found",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    continue;
                if (!ConnectionsDialog.ImportInteractive(bundle)) continue;
                if (MessageBox.Show(
                        "Imported. Delete the bundle file now? (The credentials are stored " +
                        "protected on this machine; the file is no longer needed.)",
                        "Etiquette — connections found", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes)
                    try { File.Delete(bundle); } catch { /* locked/readonly: leave it */ }
            }

            // --station <file>: open locked in Data mode (print-station
            // use — see MainForm.StationLock); path = first non-flag arg
            bool station = args.Any(a => a.Equals("--station", StringComparison.OrdinalIgnoreCase));
            string? path = args.FirstOrDefault(a => !a.StartsWith("--"));
            Application.Run(new MainForm(path, station));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "etiqedit — startup failed");
        }
    }

    private static string[] SafeFindBundles()
    {
        try
        {
            return Directory.GetFiles(
                Path.GetDirectoryName(Application.ExecutablePath)!, "*.etiqcreds");
        }
        catch { return Array.Empty<string>(); }
    }
}
