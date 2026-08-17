using System.IO.Compression;
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

    var syntheticMsix = Path.Combine(input, "signed-package-simulation.msix");
    var sharedPayload = RandomNumberGenerator.GetBytes(512 * 1024);
    await using (var msixStream = new FileStream(syntheticMsix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    using (var zip = new ZipArchive(msixStream, ZipArchiveMode.Create, leaveOpen: false))
    {
        for (var entryIndex = 0; entryIndex < 8; entryIndex++)
        {
            var entry = zip.CreateEntry($"payload/file-{entryIndex:D2}.bin", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(sharedPayload);
        }
        var manifest = zip.CreateEntry("AppxManifest.xml", CompressionLevel.Optimal);
        await using var manifestStream = new StreamWriter(manifest.Open());
        await manifestStream.WriteAsync("<Package><Identity Name=\"GPUZIP.Preflate.SelfTest\" /></Package>");
    }
    var originalMsixHash = SHA256.HashData(await File.ReadAllBytesAsync(syntheticMsix));

    var cuda = GpuZipArchive.GetCudaDeviceInfo();
    Console.WriteLine($"CUDA probe: {cuda.Available} {cuda.Name} ({cuda.Detail})");
    var result = await GpuZipArchive.CreateAsync(archive, [input], new()
    {
        BlockSize = 8 * 1024 * 1024,
        ThoroughSearch = true,
        UseCuda = true,
        UseContainerRecompression = true
    });
    Console.WriteLine($"Created {archive}: {result.InputBytes} -> {result.OutputBytes}, CUDA used={result.CudaUsed}");

    var listed = GpuZipArchive.List(archive);
    if (listed.Count < 6) throw new Exception("Archive list is incomplete.");
    var msixEntry = listed.Single(entry => entry.Path.EndsWith("signed-package-simulation.msix", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Synthetic MSIX codec: {msixEntry.Method}; {msixEntry.OriginalSize} -> {msixEntry.PackedSize}");
    if (!msixEntry.Method.Contains("PreflateZstd", StringComparison.Ordinal))
        throw new Exception("Synthetic MSIX did not select the PreflateZstd container codec.");

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

    var reconstructedMsix = Path.Combine(extractedRoot, "signed-package-simulation.msix");
    var reconstructedMsixHash = SHA256.HashData(await File.ReadAllBytesAsync(reconstructedMsix));
    if (!originalMsixHash.SequenceEqual(reconstructedMsixHash))
        throw new Exception("MSIX container SHA-256 changed after Preflate roundtrip.");
    Console.WriteLine($"MSIX bit-exact SHA-256 roundtrip: {Convert.ToHexString(originalMsixHash)}");

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
