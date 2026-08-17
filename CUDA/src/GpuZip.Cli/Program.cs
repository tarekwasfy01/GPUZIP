using GpuZip.Core;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "info":
            {
                var cuda = GpuZipArchive.GetCudaDeviceInfo();
                Console.WriteLine($"CUDA: {(cuda.Available ? "available" : "unavailable")} - {cuda.Name}");
                Console.WriteLine(cuda.Detail);
                var sevenZip = new SevenZipService();
                Console.WriteLine(sevenZip.IsAvailable ? await sevenZip.VersionAsync() : "7-Zip: unavailable");
                return 0;
            }
            case "create" when args.Length >= 3:
            {
                var result = await GpuZipArchive.CreateAsync(args[1], args[2..], new(), ConsoleProgress());
                PrintResult(result);
                return 0;
            }
            case "list" when args.Length == 2:
            {
                var entries = GpuZipArchive.IsGpuZip(args[1])
                    ? GpuZipArchive.List(args[1])
                    : await new SevenZipService().ListAsync(args[1]);
                foreach (var entry in entries)
                    Console.WriteLine($"{entry.Kind,-9} {entry.OriginalSize,12:N0}  {entry.Method,-35} {entry.Path}");
                return 0;
            }
            case "extract" when args.Length == 3:
            {
                var result = GpuZipArchive.IsGpuZip(args[1])
                    ? await GpuZipArchive.ExtractAsync(args[1], args[2], ConsoleProgress())
                    : await new SevenZipService().ExtractAsync(args[1], args[2]);
                PrintResult(result);
                return 0;
            }
            case "test" when args.Length == 2:
            {
                var result = GpuZipArchive.IsGpuZip(args[1])
                    ? await GpuZipArchive.TestAsync(args[1], ConsoleProgress())
                    : await new SevenZipService().TestAsync(args[1]);
                PrintResult(result);
                return 0;
            }
            default:
                PrintHelp();
                return 2;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static Progress<ArchiveProgress> ConsoleProgress() => new(value =>
    Console.WriteLine($"{value.Operation}: {value.CompletedEntries}/{value.TotalEntries} {value.CurrentPath}"));

static void PrintResult(ArchiveOperationResult result)
{
    Console.WriteLine(result.Summary);
    Console.WriteLine($"Input: {result.InputBytes:N0} bytes; output: {result.OutputBytes:N0} bytes; elapsed: {result.Elapsed.TotalSeconds:F2}s; CUDA: {result.CudaUsed}");
}

static void PrintHelp()
{
    Console.WriteLine("""
GPUZIP CLI
  gpuzip info
  gpuzip create <archive.gpuz> <file-or-directory> [...]
  gpuzip list <archive>
  gpuzip extract <archive> <destination>
  gpuzip test <archive>
""");
}
