using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GpuZip.Core;

var root = Path.Combine(Path.GetTempPath(), "GpuZipBenchmark-" + Guid.NewGuid().ToString("N"));
var corpus = Path.Combine(root, "corpus");
Directory.CreateDirectory(corpus);

try
{
    Console.WriteLine("Generating deterministic mixed corpus...");
    await GenerateCorpusAsync(corpus);
    var originalBytes = Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    var sourceFingerprint = await FingerprintAsync(corpus);
    var sevenZip = new SevenZipService();
    var results = new List<BenchResult>();

    var gpuzPath = Path.Combine(root, "benchmark.gpuz");
    var gpuzCreate = await GpuZipArchive.CreateAsync(gpuzPath, [corpus], new()
    {
        BlockSize = 4 * 1024 * 1024,
        BrotliQuality = 11,
        ThoroughSearch = true,
        UseCuda = true
    });
    var gpuzExtract = Path.Combine(root, "extract-gpuz");
    var gpuzExtractResult = await GpuZipArchive.ExtractAsync(gpuzPath, gpuzExtract);
    await VerifyAsync(Path.Combine(gpuzExtract, "corpus"), sourceFingerprint);
    results.Add(new("GPUZIP Ultra", new FileInfo(gpuzPath).Length, gpuzCreate.Elapsed, gpuzExtractResult.Elapsed, gpuzCreate.CudaUsed));

    foreach (var format in new[] { "zip", "7z" })
    {
        var archive = Path.Combine(root, "benchmark." + format);
        var create = await sevenZip.CreateAsync(archive, [corpus], format);
        var extractDirectory = Path.Combine(root, "extract-" + format);
        var extract = await sevenZip.ExtractAsync(archive, extractDirectory);
        await VerifyAsync(Path.Combine(extractDirectory, "corpus"), sourceFingerprint);
        results.Add(new(format == "zip" ? "ZIP Deflate -mx=9" : "7z LZMA2 -mx=9", new FileInfo(archive).Length, create.Elapsed, extract.Elapsed, false));
    }

    Console.WriteLine();
    Console.WriteLine($"Corpus: {originalBytes:N0} bytes, {Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories).Count():N0} files");
    Console.WriteLine("Method                 Bytes        Ratio      Pack      Extract   CUDA");
    Console.WriteLine("--------------------------------------------------------------------------");
    foreach (var result in results.OrderBy(value => value.Bytes))
    {
        Console.WriteLine($"{result.Name,-22} {result.Bytes,12:N0} {result.Bytes * 100.0 / originalBytes,8:F2}% {result.Pack.TotalSeconds,8:F2}s {result.Extract.TotalSeconds,9:F2}s   {result.Cuda}");
    }

    Console.WriteLine();
    Console.WriteLine($"Winner by size: {results.MinBy(value => value.Bytes)!.Name}");
    Console.WriteLine("All extracted trees matched the source SHA-256 fingerprint.");
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static async Task GenerateCorpusAsync(string directory)
{
    var textBuilder = new StringBuilder(8 * 1024 * 1024);
    for (var i = 0; textBuilder.Length < 8 * 1024 * 1024; i++)
        textBuilder.Append("public static int Predict").Append(i % 97).Append("(int previous) => previous + ").Append(i % 31).AppendLine(";");
    await File.WriteAllTextAsync(Path.Combine(directory, "source-and-text.txt"), textBuilder.ToString());

    var numeric = new byte[8 * 1024 * 1024];
    for (var i = 0; i < numeric.Length / sizeof(int); i++) BitConverter.GetBytes(i * 7 + (i / 1000)).CopyTo(numeric, i * sizeof(int));
    await File.WriteAllBytesAsync(Path.Combine(directory, "sensor-int32.bin"), numeric);

    var random = new Random(0x47505A);
    var noisy = new byte[4 * 1024 * 1024];
    random.NextBytes(noisy);
    await File.WriteAllBytesAsync(Path.Combine(directory, "incompressible.bin"), noisy);

    var records = Path.Combine(directory, "records");
    Directory.CreateDirectory(records);
    for (var file = 0; file < 100; file++)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < 200; row++)
            builder.Append("{\"device\":").Append(file % 12).Append(",\"timestamp\":").Append(1_800_000_000 + row).Append(",\"value\":").Append((file * 17 + row) % 1000).AppendLine("}");
        await File.WriteAllTextAsync(Path.Combine(records, $"record-{file:D3}.json"), builder.ToString());
    }
}

static async Task<string> FingerprintAsync(string root)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        hash.AppendData(Encoding.UTF8.GetBytes(relative));
        hash.AppendData(await File.ReadAllBytesAsync(file));
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static async Task VerifyAsync(string extractedRoot, string expected)
{
    if (!Directory.Exists(extractedRoot)) throw new DirectoryNotFoundException($"Expected extracted root: {extractedRoot}");
    var actual = await FingerprintAsync(extractedRoot);
    if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidDataException("Extracted corpus fingerprint mismatch.");
}

sealed record BenchResult(string Name, long Bytes, TimeSpan Pack, TimeSpan Extract, bool Cuda);
