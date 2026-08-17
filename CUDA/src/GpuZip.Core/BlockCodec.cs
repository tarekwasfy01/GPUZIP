using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading;

namespace GpuZip.Core;

internal sealed class BlockCodec : IDisposable
{
    private static readonly TransformId[][] FastPipelines =
    [
        [],
        [TransformId.DeltaByte],
        [TransformId.XorByte],
        [TransformId.DeltaWord4, TransformId.ByteShuffle4]
    ];

    private static readonly TransformId[][] GpuFocusedPipelines =
    [
        [],
        [TransformId.DeltaByte],
        [TransformId.XorByte],
        [TransformId.DeltaByte, TransformId.ByteShuffle2],
        [TransformId.DeltaByte, TransformId.ByteShuffle4],
        [TransformId.DeltaByte, TransformId.ByteShuffle8],
        [TransformId.XorByte, TransformId.ByteShuffle2],
        [TransformId.XorByte, TransformId.ByteShuffle4],
        [TransformId.XorByte, TransformId.ByteShuffle8]
    ];

    private static readonly TransformId[][] ThoroughPipelines =
    [
        [],
        [TransformId.DeltaByte],
        [TransformId.XorByte],
        [TransformId.ByteShuffle2],
        [TransformId.ByteShuffle4],
        [TransformId.ByteShuffle8],
        [TransformId.DeltaWord2],
        [TransformId.DeltaWord4],
        [TransformId.DeltaWord8],
        [TransformId.DeltaWord2, TransformId.ByteShuffle2],
        [TransformId.DeltaWord4, TransformId.ByteShuffle4],
        [TransformId.DeltaWord8, TransformId.ByteShuffle8]
    ];

    private readonly GpuZipCreateOptions _options;
    private readonly CudaTransformer[] _cudaWorkers;
    private readonly ConcurrentQueue<CudaTransformer> _cudaQueue = new();
    private readonly SemaphoreSlim? _cudaSlots;
    private int _cudaUsed;

    public bool CudaUsed => Volatile.Read(ref _cudaUsed) != 0;
    public int CudaWorkerCount => _cudaWorkers.Length;

    public BlockCodec(GpuZipCreateOptions options)
    {
        _options = options;
        if (!options.UseCuda)
        {
            _cudaWorkers = [];
            return;
        }

        var desiredWorkers = options.MaximumPerformance
            ? Math.Clamp(options.MaxParallelism > 0 ? options.MaxParallelism : Environment.ProcessorCount, 1, 4)
            : 1;
        var workers = new List<CudaTransformer>(desiredWorkers);
        for (var i = 0; i < desiredWorkers; i++)
        {
            if (!CudaTransformer.TryCreate(out var worker) || worker is null) break;
            workers.Add(worker);
        }
        _cudaWorkers = workers.ToArray();
        foreach (var worker in _cudaWorkers) _cudaQueue.Enqueue(worker);
        if (_cudaWorkers.Length > 0) _cudaSlots = new SemaphoreSlim(_cudaWorkers.Length, _cudaWorkers.Length);
    }

    public EncodedBlock Encode(ReadOnlySpan<byte> input, bool containerAware = true)
    {
        var best = new EncodedBlock([], PayloadCodec.Raw, input.ToArray());

        // MSIX/APPX/ZIP and other embedded DEFLATE streams can be precompressed
        // without losing a single source bit. Preflate reconstructs the original
        // DEFLATE stream exactly, so signed packages retain their original SHA-256.
        if (containerAware && _options.UseContainerRecompression &&
            PreflateCodec.TryCompress(input, out var preflate) && preflate.Length < best.Payload.Length)
        {
            best = new EncodedBlock([], PayloadCodec.PreflateZstd, preflate);
        }

        var pipelines = _options.MaximumPerformance && _cudaWorkers.Length > 0
            ? GpuFocusedPipelines
            : _options.ThoroughSearch ? ThoroughPipelines : FastPipelines;
        var scores = new List<(TransformId[] Pipeline, int ProbeLength)>(pipelines.Length);
        var probeQuality = _options.MaximumPerformance ? 1 : Math.Min(2, _options.BrotliQuality);

        foreach (var pipeline in pipelines)
        {
            var transformed = ApplyPipeline(input, pipeline);
            var probe = CompressBrotli(transformed, probeQuality, 22);
            scores.Add((pipeline, probe.Length));
        }

        var finalistCount = _options.MaximumPerformance
            ? Math.Min(3, scores.Count)
            : _options.ThoroughSearch ? Math.Min(4, scores.Count) : scores.Count;
        var finalQuality = _options.MaximumPerformance ? Math.Min(_options.BrotliQuality, 6) : _options.BrotliQuality;

        foreach (var candidate in scores.OrderBy(value => value.ProbeLength + value.Pipeline.Length).Take(finalistCount))
        {
            var transformed = ApplyPipeline(input, candidate.Pipeline);
            var compressed = CompressBrotli(transformed, finalQuality, 24);
            var candidateSize = compressed.Length + candidate.Pipeline.Length;
            var bestSize = best.Payload.Length + best.Pipeline.Length;
            if (candidateSize < bestSize) best = new EncodedBlock(candidate.Pipeline, PayloadCodec.Brotli, compressed);
        }

        return best;
    }

    public static byte[] Decode(EncodedBlock block, int originalLength)
    {
        if (block.Codec == PayloadCodec.PreflateZstd)
        {
            if (block.Pipeline.Length != 0) throw new InvalidDataException("Preflate blocks cannot contain reversible transforms.");
            return PreflateCodec.Decompress(block.Payload, originalLength);
        }

        var transformed = block.Codec switch
        {
            PayloadCodec.Raw => block.Payload,
            PayloadCodec.Brotli => DecompressBrotli(block.Payload, originalLength),
            _ => throw new InvalidDataException($"Unknown payload codec {block.Codec}.")
        };
        var result = ReversibleTransforms.ReversePipeline(transformed, block.Pipeline);
        if (result.Length != originalLength) throw new InvalidDataException("Decoded block length does not match header.");
        return result;
    }

    private byte[] ApplyPipeline(ReadOnlySpan<byte> input, TransformId[] pipeline)
    {
        if (pipeline.Length > 0 && input.Length >= 256 * 1024 && IsCudaTransform(pipeline[0]) && TryCudaTransform(input, pipeline[0], out var current))
        {
            Interlocked.Exchange(ref _cudaUsed, 1);
            for (var i = 1; i < pipeline.Length; i++) current = ReversibleTransforms.Apply(current, pipeline[i]);
            return current;
        }
        return ReversibleTransforms.ApplyPipeline(input, pipeline);
    }

    private bool TryCudaTransform(ReadOnlySpan<byte> input, TransformId transform, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (_cudaSlots is null) return false;
        _cudaSlots.Wait();
        CudaTransformer? worker = null;
        try
        {
            if (!_cudaQueue.TryDequeue(out worker)) return false;
            return worker.TryTransform(input, transform, out output);
        }
        finally
        {
            if (worker is not null) _cudaQueue.Enqueue(worker);
            _cudaSlots.Release();
        }
    }

    private static bool IsCudaTransform(TransformId transform) => transform is TransformId.DeltaByte or TransformId.XorByte;

    private static byte[] CompressBrotli(ReadOnlySpan<byte> input, int quality, int window)
    {
        var safetyMargin = Math.Max(64 * 1024, input.Length / 32);
        var maxLength = checked(Math.Max(BrotliEncoder.GetMaxCompressedLength(input.Length), input.Length) + safetyMargin);
        var output = new byte[maxLength];
        if (!BrotliEncoder.TryCompress(input, output, out var written, quality, window))
            throw new InvalidOperationException($"Brotli compression failed for {input.Length} bytes (quality {quality}, window {window}, destination {output.Length}).");
        return output.AsSpan(0, written).ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> input, int expectedLength)
    {
        var output = new byte[expectedLength];
        if (!BrotliDecoder.TryDecompress(input, output, out var written) || written != expectedLength)
            throw new InvalidDataException("Invalid Brotli payload.");
        return output;
    }

    public void Dispose()
    {
        _cudaSlots?.Dispose();
        foreach (var worker in _cudaWorkers) worker.Dispose();
    }
}

internal sealed record EncodedBlock(TransformId[] Pipeline, PayloadCodec Codec, byte[] Payload);
