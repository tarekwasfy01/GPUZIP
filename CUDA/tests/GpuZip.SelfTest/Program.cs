using System.Security.Cryptography;
using GpuZip.Core;

var root = Path.Combine(Path.GetTempPath(), "GpuZipSelfTest-" + Guid.NewGuid().ToString("N"));
var input = Path.Combine(root, "input");
var output = Path.Combine(root, "output");
var archive = Path.Combine(root, "roundtrip.gpuz");

try
{
    Directory.CreateDirectory(Path.Combine(input, "nested"));
    await File.WriteAllTextAsync(Path.Combine(input, "readme.txt"), string.Concat(Enumerable.Repeat("GPUZIP predicts repeated text.\n", 20_000)));
    var numeric = new byte[2 * 1024 * 1024];
    for (var i = 0; i < numeric.Length / sizeof(int); i++) BitConverter.GetBytes(i * 3).CopyTo(numeric, i * sizeof(int));
    await File.WriteAllBytesAsync(Path.Combine(input, "nested", "series.bin"), numeric);
    var random = RandomNumberGenerator.GetBytes(512 * 1024);
    await File.WriteAllBytesAsync(Path.Combine(input, "random.bin"), random);
    await File.WriteAllBytesAsync(Path.Combine(input, "empty.bin"), []);

    var cuda = GpuZipArchive.GetCudaDeviceInfo();
    Console.WriteLine($"CUDA probe: {cuda.Available} {cuda.Name} ({cuda.Detail})");
    var result = await GpuZipArchive.CreateAsync(archive, [input], new() { BlockSize = 256 * 1024, ThoroughSearch = true, UseCuda = true });
    Console.WriteLine($"Created {archive}: {result.InputBytes} -> {result.OutputBytes}, CUDA used={result.CudaUsed}");
    if (GpuZipArchive.List(archive).Count < 5) throw new Exception("Archive list is incomplete.");
    await GpuZipArchive.TestAsync(archive);
    await GpuZipArchive.ExtractAsync(archive, output);

    var extractedRoot = Path.Combine(output, "input");
    foreach (var sourceFile in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
    {
        var targetFile = Path.Combine(extractedRoot, Path.GetRelativePath(input, sourceFile));
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceFile));
        var targetHash = SHA256.HashData(await File.ReadAllBytesAsync(targetFile));
        if (!sourceHash.SequenceEqual(targetHash)) throw new Exception($"Roundtrip mismatch: {sourceFile}");
    }

    var corrupt = Path.Combine(root, "corrupt.gpuz");
    File.Copy(archive, corrupt);
    await using (var corruptStream = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
    {
        corruptStream.Position = corruptStream.Length - 17;
        var value = corruptStream.ReadByte();
        corruptStream.Position--;
        corruptStream.WriteByte((byte)(value ^ 0x5a));
    }
    try
    {
        await GpuZipArchive.TestAsync(corrupt);
        throw new Exception("Corruption was not detected.");
    }
    catch (InvalidDataException)
    {
        Console.WriteLine("Corruption detection: passed");
    }

    Console.WriteLine("GPUZIP self-test: PASSED");
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
