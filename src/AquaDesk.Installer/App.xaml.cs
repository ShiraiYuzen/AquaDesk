using System;
using System.IO;
using System.Windows;

namespace AquaDesk.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "AquaDesk-installer-crash.log"),
                    args.Exception.ToString());
            }
            catch { /* 日志写不进去就算了，别掩盖原始异常 */ }

            args.Handled = true;
            Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "AquaDesk-installer-crash.log"),
                    args.ExceptionObject?.ToString() ?? "unknown");
            }
            catch { /* 同上 */ }
        };
    }
}
