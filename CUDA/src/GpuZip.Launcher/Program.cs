using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new InvalidOperationException("GPUZIP launcher path could not be determined.");

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).Substring(0, 16);
            var runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GPUZIP", "Runtime", hash);
            var marker = Path.Combine(runtimeDir, ".ready");
            var appExe = Path.Combine(runtimeDir, "GpuZip.App.exe");

            if (!File.Exists(marker) || !File.Exists(appExe))
            {
                if (Directory.Exists(runtimeDir))
                    Directory.Delete(runtimeDir, true);
                Directory.CreateDirectory(runtimeDir);

                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("GpuZipPayload")
                    ?? throw new InvalidOperationException("Embedded GPUZIP payload was not found.");
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
                archive.ExtractToDirectory(runtimeDir, overwriteFiles: true);
                File.WriteAllText(marker, hash);
            }

            var psi = new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = runtimeDir,
                UseShellExecute = false
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("GPUZIP could not be started.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GPUZIP");
                Directory.CreateDirectory(logDir);
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
