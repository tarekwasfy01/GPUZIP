using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GpuZip.Core;

public sealed class SevenZipService
{
    public SevenZipService(string? executablePath = null)
    {
        ExecutablePath = executablePath ?? FindExecutable();
        FileManagerPath = FindFileManager();
    }

    public string ExecutablePath { get; }
    public string FileManagerPath { get; }
    public bool IsAvailable => File.Exists(ExecutablePath);
    public bool IsFileManagerAvailable => File.Exists(FileManagerPath);

    public void OpenFileManager(string? archivePath = null)
    {
        if (!IsFileManagerAvailable)
            throw new FileNotFoundException("Bundled 7zFM.exe was not found.", FileManagerPath);

        var start = new ProcessStartInfo(FileManagerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(FileManagerPath) ?? AppContext.BaseDirectory
        };
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            start.ArgumentList.Add(archivePath);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Could not start the 7-Zip File Manager.");
    }

    public async Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["l", "-slt", "--", archivePath], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "list");
        var entries = new List<ArchiveEntryInfo>();
        string? path = null;
        long size = 0;
        long packed = 0;
        var modified = DateTime.MinValue;
        var attributes = string.Empty;
        var method = string.Empty;
        var inEntries = false;

        foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("----------", StringComparison.Ordinal)) { inEntries = true; continue; }
            if (!inEntries) continue;
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (path is not null) AddEntry();
                path = line[7..]; size = 0; packed = 0; modified = DateTime.MinValue; attributes = string.Empty; method = string.Empty;
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal)) long.TryParse(line[7..], out size);
            else if (line.StartsWith("Packed Size = ", StringComparison.Ordinal)) long.TryParse(line[14..], out packed);
            else if (line.StartsWith("Modified = ", StringComparison.Ordinal)) DateTime.TryParse(line[11..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out modified);
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal)) attributes = line[13..];
            else if (line.StartsWith("Method = ", StringComparison.Ordinal)) method = line[9..];
        }
        if (path is not null) AddEntry();
        return entries;

        void AddEntry()
        {
            var kind = attributes.StartsWith('D') ? ArchiveEntryKind.Directory : ArchiveEntryKind.File;
            entries.Add(new(path!, kind, size, packed, modified.ToUniversalTime(), kind == ArchiveEntryKind.File ? 1 : 0, method));
        }
    }

    public async Task<ArchiveOperationResult> CreateAsync(string archivePath, IEnumerable<string> inputs, string format, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var inputList = inputs.Select(Path.GetFullPath).ToList();
        if (inputList.Count == 0) throw new ArgumentException("No input files selected.", nameof(inputs));
        if (inputList.Any(path => !File.Exists(path) && !Directory.Exists(path)))
            throw new FileNotFoundException("One or more selected input paths no longer exist.");
        var normalizedFormat = NormalizeFormat(format);
        if (normalizedFormat is "gzip" or "bzip2" or "xz" && (inputList.Count != 1 || !File.Exists(inputList[0])))
            throw new ArgumentException($"{normalizedFormat} is a single-stream format and requires exactly one file.", nameof(inputs));

        var args = new List<string>
        {
            "a", "-y", $"-t{normalizedFormat}", "-mx=9", "-mmt=on"
        };
        if (normalizedFormat == "7z") args.Add("-m0=lzma2");
        args.Add("--");
        args.Add(archivePath);
        args.AddRange(inputList);

        var result = await RunAsync(args, null, cancellationToken, highPriority: true).ConfigureAwait(false);
        EnsureSuccess(result, "create");
        stopwatch.Stop();
        var inputBytes = inputList.Sum(GetPathSize);
        return new(inputList.Count, inputBytes, new FileInfo(archivePath).Length, stopwatch.Elapsed, false,
            $"7-Zip maximum compression completed with multithreading enabled. {result.Output.Trim()}");
    }

    public async Task<ArchiveOperationResult> ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(destination);
        var result = await RunAsync(["x", "-y", $"-o{destination}", "--", archivePath], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "extract");
        stopwatch.Stop();
        return new(0, new FileInfo(archivePath).Length, 0, stopwatch.Elapsed, false, result.Output.Trim());
    }

    public async Task<ArchiveOperationResult> TestAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(["t", "--", archivePath], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "test");
        stopwatch.Stop();
        return new(0, new FileInfo(archivePath).Length, 0, stopwatch.Elapsed, false, result.Output.Trim());
    }

    public async Task<string> VersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["i"], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "inspect");
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "7-Zip";
    }

    private async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        bool highPriority = false)
    {
        if (!IsAvailable) throw new FileNotFoundException("Bundled 7zz.exe was not found.", ExecutablePath);
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start 7-Zip.");
        if (highPriority)
        {
            try { process.PriorityClass = ProcessPriorityClass.High; } catch { }
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"7-Zip {operation} failed ({result.ExitCode}).\n{result.Error}\n{result.Output}");
    }

    private static string NormalizeFormat(string format) => format.ToLowerInvariant() switch
    {
        "7z" => "7z", "zip" => "zip", "tar" => "tar", "gzip" or "gz" => "gzip",
        "bzip2" or "bz2" => "bzip2", "xz" => "xz",
        _ => throw new NotSupportedException($"Creating {format} archives is not supported.")
    };

    private static long GetPathSize(string path) => File.Exists(path)
        ? new FileInfo(path).Length
        : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;

    private static string FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "7zip", "7zz.exe"),
            Path.Combine(AppContext.BaseDirectory, "7zz.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "third_party", "7zip", "CPP", "7zip", "Bundles", "Alone2", "b", "g_x64", "7zz.exe"))
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string FindFileManager()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "7zip", "7zFM.exe"),
            Path.Combine(AppContext.BaseDirectory, "7zFM.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "third_party", "7zip", "CPP", "7zip", "Bundles", "Fm", "x64", "7zFM.exe"))
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
