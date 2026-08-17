using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPUZIP");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "launcher.log");

        void Log(string text)
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
            }
            catch { }
        }

        try
        {
            Log("Launcher started.");
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine launcher executable path.");
            Log($"Launcher path: {exePath}");

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).Substring(0, 16);
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GPUZIP", "Runtime", hash);
            var marker = Path.Combine(runtimeDir, ".ready");
            var appExe = Path.Combine(runtimeDir, "GpuZip.App.exe");
            Log($"Runtime directory: {runtimeDir}");

            if (!File.Exists(marker) || !File.Exists(appExe))
            {
                Log("Extracting embedded payload.");
                if (Directory.Exists(runtimeDir))
                    Directory.Delete(runtimeDir, true);
                Directory.CreateDirectory(runtimeDir);

                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("GpuZipPayload")
                    ?? throw new InvalidOperationException("Embedded GPUZIP payload was not found.");
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
                archive.ExtractToDirectory(runtimeDir, overwriteFiles: true);
                File.WriteAllText(marker, hash);
                Log("Payload extraction completed.");
            }
            else
            {
                Log("Using existing extracted payload.");
            }

            if (!File.Exists(appExe))
                throw new FileNotFoundException("GpuZip.App.exe was not found after extraction.", appExe);

            Log($"Starting app: {appExe}");
            var psi = new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = runtimeDir,
                UseShellExecute = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("GPUZIP could not be started.");

            if (!process.WaitForExit(5000))
            {
                Log("App is still running after 5 seconds; launcher exits successfully.");
                return 0;
            }

            Log($"App exited quickly with code {process.ExitCode}.");
            if (process.ExitCode != 0)
            {
                System.Windows.Forms.MessageBox.Show(
                    $"GPUZIP exited immediately with code {process.ExitCode}.\n\nSee: {logPath}\n\nAlso check: %LOCALAPPDATA%\\GPUZIP\\app-crash.log",
                    "GPUZIP startup error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log("Launcher exception: " + ex);
            try
            {
                File.WriteAllText(Path.Combine(logDir, "launcher-error.txt"), ex.ToString());
            }
            catch { }

            System.Windows.Forms.MessageBox.Show(
                ex.Message + "\n\nDetails: %LOCALAPPDATA%\\GPUZIP\\launcher-error.txt",
                "GPUZIP startup error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }
}
