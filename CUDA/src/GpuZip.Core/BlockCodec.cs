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
    private readonly CudaTransformer? _cuda;
    private int _cudaUsed;

    public bool CudaUsed => Volatile.Read(ref _cudaUsed) != 0;

    public BlockCodec(GpuZipCreateOptions options)
    {
        _options = options;
        if (options.UseCuda) CudaTransformer.TryCreate(out _cuda);
    }

    public EncodedBlock Encode(ReadOnlySpan<byte> input)
    {
        var pipelines = _options.ThoroughSearch ? ThoroughPipelines : FastPipelines;
        var best = new EncodedBlock([], PayloadCodec.Raw, input.ToArray());
        var scores = new List<(TransformId[] Pipeline, int ProbeLength)>(pipelines.Length);
        var probeQuality = Math.Min(2, _options.BrotliQuality);

        // First pass: only retain the score. This keeps memory bounded when many
        // blocks are encoded in parallel on high-core-count machines.
        foreach (var pipeline in pipelines)
        {
            var transformed = ApplyPipeline(input, pipeline);
            var probe = CompressBrotli(transformed, probeQuality, 22);
            scores.Add((pipeline, probe.Length));
        }

        var finalistCount = _options.ThoroughSearch ? Math.Min(4, scores.Count) : scores.Count;
        foreach (var candidate in scores
                     .OrderBy(value => value.ProbeLength + value.Pipeline.Length)
                     .Take(finalistCount))
        {
            var transformed = ApplyPipeline(input, candidate.Pipeline);
            var compressed = CompressBrotli(transformed, _options.BrotliQuality, 24);
            var candidateSize = compressed.Length + candidate.Pipeline.Length;
            var bestSize = best.Payload.Length + best.Pipeline.Length;
            if (candidateSize < bestSize)
            {
                best = new EncodedBlock(candidate.Pipeline, PayloadCodec.Brotli, compressed);
            }
        }

        return best;
    }

    public static byte[] Decode(EncodedBlock block, int originalLength)
    {
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
        if (pipeline.Length == 1 && input.Length >= 256 * 1024 && _cuda is not null &&
            _cuda.TryTransform(input, pipeline[0], out var transformed))
        {
            Interlocked.Exchange(ref _cudaUsed, 1);
            return transformed;
        }

        return ReversibleTransforms.ApplyPipeline(input, pipeline);
    }

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

    public void Dispose() => _cuda?.Dispose();
}

internal sealed record EncodedBlock(TransformId[] Pipeline, PayloadCodec Codec, byte[] Payload);
