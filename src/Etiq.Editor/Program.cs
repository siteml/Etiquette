namespace Etiq.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // a WinExe dies invisibly on unhandled exceptions — always show them
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "etiqedit — unhandled exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "etiqedit — fatal exception");
        try
        {
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "etiqedit — startup failed");
        }
    }
}
