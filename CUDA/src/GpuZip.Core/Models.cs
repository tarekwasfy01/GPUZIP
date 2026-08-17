namespace GpuZip.Core;

public enum ArchiveEntryKind : byte
{
    Directory = 0,
    File = 1
}

public sealed record ArchiveEntryInfo(
    string Path,
    ArchiveEntryKind Kind,
    long OriginalSize,
    long PackedSize,
    DateTime LastWriteTimeUtc,
    int BlockCount,
    string Method);

public sealed record ArchiveProgress(
    string Operation,
    string CurrentPath,
    int CompletedEntries,
    int TotalEntries,
    long InputBytes,
    long OutputBytes);

public sealed record GpuZipCreateOptions
{
    public int BlockSize { get; init; } = 4 * 1024 * 1024;
    public int BrotliQuality { get; init; } = 11;
    public bool UseCuda { get; init; } = true;
    public bool ThoroughSearch { get; init; } = true;
    public bool MaximumPerformance { get; init; } = true;
    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public bool UseContainerRecompression { get; init; } = true;
}

public sealed record ArchiveOperationResult(
    int EntryCount,
    long InputBytes,
    long OutputBytes,
    TimeSpan Elapsed,
    bool CudaUsed,
    string Summary);

public sealed record CudaDeviceInfo(bool Available, string Name, string Detail);

internal enum TransformId : byte
{
    DeltaByte = 1,
    XorByte = 2,
    ByteShuffle2 = 3,
    ByteShuffle4 = 4,
    ByteShuffle8 = 5,
    DeltaWord2 = 6,
    DeltaWord4 = 7,
    DeltaWord8 = 8
}

internal enum PayloadCodec : byte
{
    Raw = 0,
    Brotli = 1,
    PreflateZstd = 2
}
