using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GpuZip.Core;

public static class GpuZipArchive
{
    private static readonly byte[] Magic = "GPUZIP01"u8.ToArray();
    private const uint Version = 1;

    public static CudaDeviceInfo GetCudaDeviceInfo() => CudaTransformer.Probe();

    public static async Task<ArchiveOperationResult> CreateAsync(
        string archivePath,
        IEnumerable<string> inputPaths,
        GpuZipCreateOptions? options = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.BlockSize is < 64 * 1024 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options.BlockSize));
        var items = EnumerateInputs(inputPaths).ToList();
        if (items.Count == 0) throw new ArgumentException("At least one existing file or directory is required.", nameof(inputPaths));

        var stopwatch = Stopwatch.StartNew();
        long inputBytes = 0;
        await using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        using var codec = new BlockCodec(options);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(items.Count);
        writer.Write(options.BlockSize);

        var completed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write((byte)item.Kind);
            WriteSafeString(writer, item.RelativePath);
            writer.Write(item.LastWriteUtc.Ticks);
            writer.Write(item.Length);

            if (item.Kind == ArchiveEntryKind.Directory)
            {
                writer.Write(0);
            }
            else
            {
                var blockCount = checked((int)((item.Length + options.BlockSize - 1) / options.BlockSize));
                writer.Write(blockCount);
                await using var input = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, options.BlockSize, true);
                var buffer = new byte[options.BlockSize];
                while (true)
                {
                    var read = await ReadBlockAsync(input, buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    var original = buffer.AsMemory(0, read).ToArray();
                    var encoded = codec.Encode(original);
                    writer.Write(read);
                    writer.Write((byte)encoded.Codec);
                    writer.Write((byte)encoded.Pipeline.Length);
                    foreach (var transform in encoded.Pipeline) writer.Write((byte)transform);
                    writer.Write(encoded.Payload.Length);
                    writer.Write(SHA256.HashData(original));
                    writer.Write(encoded.Payload);
                    inputBytes += read;
                }
            }

            completed++;
            progress?.Report(new("Compressing", item.RelativePath, completed, items.Count, inputBytes, stream.Position));
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new(items.Count, inputBytes, stream.Length, stopwatch.Elapsed, codec.CudaUsed,
            $"Created {items.Count} entries using adaptive reversible pipelines.");
    }

    public static IReadOnlyList<ArchiveEntryInfo> List(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = OpenReader(stream, out var entryCount, out var blockSize);
        var entries = new List<ArchiveEntryInfo>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var kind = (ArchiveEntryKind)reader.ReadByte();
            var path = ReadSafeString(reader);
            var modified = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var originalSize = reader.ReadInt64();
            var blocks = reader.ReadInt32();
            ValidateEntryMetadata(kind, originalSize, blocks, blockSize);
            long packed = 0;
            var methods = new HashSet<string>(StringComparer.Ordinal);
            for (var block = 0; block < blocks; block++)
            {
                var originalLength = reader.ReadInt32();
                ValidateBlockLength(originalLength, blockSize);
                var payloadCodec = (PayloadCodec)reader.ReadByte();
                var transformCount = reader.ReadByte();
                if (transformCount > 16) throw new InvalidDataException("Invalid transform count.");
                var transformNames = new List<string>(transformCount);
                for (var transform = 0; transform < transformCount; transform++)
                {
                    var transformId = (TransformId)reader.ReadByte();
                    ValidateTransform(transformId);
                    transformNames.Add(transformId.ToString());
                }
                var payloadLength = reader.ReadInt32();
                ValidatePayloadLength(payloadLength, stream);
                if (reader.ReadBytes(32).Length != 32) throw new EndOfStreamException();
                stream.Seek(payloadLength, SeekOrigin.Current);
                packed += payloadLength;
                methods.Add(transformNames.Count == 0 ? payloadCodec.ToString() : $"{string.Join('+', transformNames)}+{payloadCodec}");
            }
            entries.Add(new(path, kind, originalSize, packed, modified, blocks, string.Join(", ", methods)));
        }
        return entries;
    }

    public static Task<ArchiveOperationResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DecodeArchiveAsync(archivePath, destinationDirectory, false, progress, cancellationToken);

    public static Task<ArchiveOperationResult> TestAsync(
        string archivePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DecodeArchiveAsync(archivePath, null, true, progress, cancellationToken);

    public static bool IsGpuZip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[Magic.Length];
            return stream.Read(magic) == Magic.Length && magic.SequenceEqual(Magic);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<ArchiveOperationResult> DecodeArchiveAsync(
        string archivePath,
        string? destinationDirectory,
        bool testOnly,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var reader = OpenReader(stream, out var entryCount, out var blockSize);
        var root = testOnly ? null : Path.GetFullPath(destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory)));
        if (root is not null) Directory.CreateDirectory(root);
        long outputBytes = 0;

        for (var entry = 0; entry < entryCount; entry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = (ArchiveEntryKind)reader.ReadByte();
            var path = ReadSafeString(reader);
            var modified = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var originalSize = reader.ReadInt64();
            var blocks = reader.ReadInt32();
            ValidateEntryMetadata(kind, originalSize, blocks, blockSize);
            var outputPath = root is null ? null : ResolveSafeOutputPath(root, path);
            if (kind == ArchiveEntryKind.Directory)
            {
                if (!testOnly) Directory.CreateDirectory(outputPath!);
            }
            else
            {
                if (!testOnly) Directory.CreateDirectory(Path.GetDirectoryName(outputPath!)!);
                await using var output = testOnly ? Stream.Null : new FileStream(outputPath!, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                long fileOutputBytes = 0;
                for (var block = 0; block < blocks; block++)
                {
                    var originalLength = reader.ReadInt32();
                    ValidateBlockLength(originalLength, blockSize);
                    var payloadCodec = (PayloadCodec)reader.ReadByte();
                    var transformCount = reader.ReadByte();
                    if (transformCount > 16) throw new InvalidDataException("Invalid transform count.");
                    var pipeline = new TransformId[transformCount];
                    for (var transform = 0; transform < transformCount; transform++)
                    {
                        pipeline[transform] = (TransformId)reader.ReadByte();
                        ValidateTransform(pipeline[transform]);
                    }
                    var payloadLength = reader.ReadInt32();
                    ValidatePayloadLength(payloadLength, stream);
                    var expectedHash = reader.ReadBytes(32);
                    if (expectedHash.Length != 32) throw new EndOfStreamException();
                    var payload = reader.ReadBytes(payloadLength);
                    if (payload.Length != payloadLength) throw new EndOfStreamException();
                    var decoded = BlockCodec.Decode(new(pipeline, payloadCodec, payload), originalLength);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(decoded), expectedHash))
                        throw new InvalidDataException($"Integrity check failed for {path}, block {block}.");
                    await output.WriteAsync(decoded, cancellationToken).ConfigureAwait(false);
                    fileOutputBytes += decoded.Length;
                    outputBytes += decoded.Length;
                }
                if (fileOutputBytes != originalSize)
                    throw new InvalidDataException($"Decoded size does not match header for {path}.");
                if (!testOnly) File.SetLastWriteTimeUtc(outputPath!, modified);
            }
            progress?.Report(new(testOnly ? "Testing" : "Extracting", path, entry + 1, entryCount, stream.Position, outputBytes));
        }

        stopwatch.Stop();
        return new(entryCount, stream.Length, outputBytes, stopwatch.Elapsed, false,
            testOnly ? "All blocks passed SHA-256 verification." : $"Extracted {entryCount} entries.");
    }

    private static BinaryReader OpenReader(Stream stream, out int entryCount, out int blockSize)
    {
        var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a GPUZIP archive.");
        var version = reader.ReadUInt32();
        if (version != Version) throw new InvalidDataException($"Unsupported GPUZIP version {version}.");
        entryCount = reader.ReadInt32();
        blockSize = reader.ReadInt32();
        if (entryCount is < 0 or > 10_000_000) throw new InvalidDataException("Invalid entry count.");
        if (blockSize is < 64 * 1024 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid block size.");
        return reader;
    }

    private static void ValidateEntryMetadata(ArchiveEntryKind kind, long originalSize, int blocks, int blockSize)
    {
        if (kind is not (ArchiveEntryKind.Directory or ArchiveEntryKind.File))
            throw new InvalidDataException("Invalid archive entry kind.");
        if (originalSize < 0) throw new InvalidDataException("Invalid entry size.");
        var expectedBlocks = kind == ArchiveEntryKind.File
            ? checked((int)((originalSize + blockSize - 1) / blockSize))
            : 0;
        if (blocks != expectedBlocks) throw new InvalidDataException("Invalid block count.");
        if (kind == ArchiveEntryKind.Directory && originalSize != 0)
            throw new InvalidDataException("Directory entries cannot contain data.");
    }

    private static void ValidateBlockLength(int originalLength, int blockSize)
    {
        if (originalLength is <= 0 || originalLength > blockSize)
            throw new InvalidDataException("Invalid original block length.");
    }

    private static void ValidatePayloadLength(int payloadLength, Stream stream)
    {
        if (payloadLength < 0 || payloadLength > 512 * 1024 * 1024)
            throw new InvalidDataException("Invalid payload length.");
        if (stream.CanSeek && payloadLength > stream.Length - stream.Position - 32)
            throw new EndOfStreamException();
    }

    private static void ValidateTransform(TransformId transform)
    {
        if (!Enum.IsDefined(transform)) throw new InvalidDataException($"Unknown transform {transform}.");
    }

    private static IEnumerable<InputItem> EnumerateInputs(IEnumerable<string> paths)
    {
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(rawPath);
            if (File.Exists(fullPath))
            {
                var name = UniqueRootName(Path.GetFileName(fullPath), usedRoots);
                var info = new FileInfo(fullPath);
                yield return new(fullPath, name, ArchiveEntryKind.File, info.Length, info.LastWriteTimeUtc);
            }
            else if (Directory.Exists(fullPath))
            {
                var rootName = UniqueRootName(new DirectoryInfo(fullPath).Name, usedRoots);
                var rootInfo = new DirectoryInfo(fullPath);
                yield return new(fullPath, rootName, ArchiveEntryKind.Directory, 0, rootInfo.LastWriteTimeUtc);
                foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories))
                {
                    var info = new DirectoryInfo(directory);
                    yield return new(directory, NormalizeArchivePath(Path.Combine(rootName, Path.GetRelativePath(fullPath, directory))), ArchiveEntryKind.Directory, 0, info.LastWriteTimeUtc);
                }
                foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    yield return new(file, NormalizeArchivePath(Path.Combine(rootName, Path.GetRelativePath(fullPath, file))), ArchiveEntryKind.File, info.Length, info.LastWriteTimeUtc);
                }
            }
        }
    }

    private static string UniqueRootName(string proposed, HashSet<string> used)
    {
        var name = proposed;
        var index = 2;
        while (!used.Add(name)) name = $"{proposed} ({index++})";
        return name;
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    private static string ResolveSafeOutputPath(string root, string archivePath)
    {
        var relative = archivePath.Replace('/', Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe archive path: {archivePath}");
        return output;
    }

    private static void WriteSafeString(BinaryWriter writer, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 1024 * 1024) throw new InvalidDataException("Archive path is too long.");
        writer.Write(value);
    }

    private static string ReadSafeString(BinaryReader reader)
    {
        var value = reader.ReadString();
        if (value.Length == 0 || Path.IsPathRooted(value) || value.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException($"Unsafe archive path: {value}");
        return value;
    }

    private static async Task<int> ReadBlockAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private sealed record InputItem(string SourcePath, string RelativePath, ArchiveEntryKind Kind, long Length, DateTime LastWriteUtc);
}
