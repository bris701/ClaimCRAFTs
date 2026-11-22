using System.IO;
using System.Text;
using System.Windows;

namespace ClaimCRAFTs;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Report unexpected UI exceptions instead of letting a WinExe
        // silently disappear when launched by double-clicking.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFailure("Unhandled UI exception", args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportFailure("Application startup", ex);
            Shutdown(1);
        }
    }

    private static void ReportFailure(string phase, Exception error)
    {
        var path = "(unable to write log file)";
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaimCRAFTs");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "crash.log");
            var log = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {phase}")
                .AppendLine(error.ToString())
                .AppendLine(new string('-', 72))
                .ToString();
            File.AppendAllText(path, log);
        }
        catch
        {
            // Logging must never conceal the original startup failure.
        }

        MessageBox.Show(
            $"ClaimCRAFTs couldn't start properly.\n\n" +
            $"{error.GetType().Name}: {error.Message}\n\n" +
            $"Diagnostic log: {path}",
            "ClaimCRAFTs - Startup error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
