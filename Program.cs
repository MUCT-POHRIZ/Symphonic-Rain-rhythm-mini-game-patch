using System.Diagnostics;
using System.Security.Principal;

namespace SRPerfectPatch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 单实例：避免重复挂载
        using var mutex = new Mutex(true, "SRPerfectPatch.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("SR Perfect Patch 已经在运行（见系统托盘）。", "SR Perfect Patch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
