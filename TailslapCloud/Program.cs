using System;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        try { Logger.Log("App starting"); } catch { }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => { try { Logger.Log("UI Exception: " + e.Exception); } catch { } };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { Logger.Log("Non-UI Exception: " + e.ExceptionObject); } catch { } };

        // Ensure per-monitor DPI scaling for all dialogs
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        using var mutex = new Mutex(true, "TailslapCloud_SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
