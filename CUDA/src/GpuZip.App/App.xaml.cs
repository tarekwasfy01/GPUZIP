using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GpuZip.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        LogStartup("App constructor entered.");
        try
        {
            InitializeComponent();
            LogStartup("App XAML initialized.");
        }
        catch (Exception ex)
        {
            LogStartup("App InitializeComponent failed: " + ex);
            throw;
        }

        UnhandledException += (_, args) =>
        {
            LogStartup("Unhandled WinUI exception: " + args.Exception);

            // WinUI otherwise turns many unhandled UI exceptions into a native
            // fail-fast termination. Keep the process alive where possible and
            // leave diagnostics on disk rather than disappearing silently.
            args.Handled = true;
        };
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
            // Startup diagnostics must never become a startup dependency.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogStartup("OnLaunched entered.");
        try
        {
            _window = new MainWindow();
            LogStartup("MainWindow created.");
            _window.Activate();
            LogStartup("MainWindow activated.");
        }
        catch (Exception ex)
        {
            LogStartup("MainWindow startup failed: " + ex);

            // If the full UI has a managed initialization problem, show a tiny
            // fallback window instead of terminating with no visible error.
            try
            {
                _window = new Window
                {
                    Title = "GPUZIP startup diagnostics",
                    Content = new Grid
                    {
                        Padding = new Thickness(24),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "GPUZIP could not initialize its main window.\n\n" +
                                       ex.Message +
                                       "\n\nDetails were written to %LOCALAPPDATA%\\GPUZIP\\app-startup.log",
                                TextWrapping = TextWrapping.Wrap,
                                MaxWidth = 720
                            }
                        }
                    }
                };
                _window.Activate();
                LogStartup("Fallback diagnostics window activated.");
            }
            catch (Exception fallbackEx)
            {
                LogStartup("Fallback diagnostics window failed: " + fallbackEx);
                throw;
            }
        }
    }
}
