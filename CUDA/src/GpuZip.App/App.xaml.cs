using System.IO;
using System.Windows;

namespace GpuZip.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogStartup("Unhandled WPF exception: " + args.Exception);
            MessageBox.Show(
                "GPUZIP encountered an unexpected UI error.\n\n" + args.Exception.Message +
                "\n\nDetails: %LOCALAPPDATA%\\GPUZIP\\app-startup.log",
                "GPUZIP error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogStartup("Unhandled AppDomain exception: " + args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogStartup("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        LogStartup("WPF application startup entered.");
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            LogStartup("MainWindow shown successfully.");
        }
        catch (Exception ex)
        {
            LogStartup("MainWindow startup failed: " + ex);
            MessageBox.Show(
                "GPUZIP could not initialize its main window.\n\n" + ex.Message +
                "\n\nDetails: %LOCALAPPDATA%\\GPUZIP\\app-startup.log",
                "GPUZIP startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal static void LogStartup(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GPUZIP");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "app-startup.log"),
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never become a startup dependency.
        }
    }
}
