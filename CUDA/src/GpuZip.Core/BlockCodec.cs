using System.IO.Compression;

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
    public bool CudaUsed { get; private set; }

    public BlockCodec(GpuZipCreateOptions options)
    {
        _options = options;
        if (options.UseCuda) CudaTransformer.TryCreate(out _cuda);
    }

    public EncodedBlock Encode(ReadOnlySpan<byte> input)
    {
        var pipelines = _options.ThoroughSearch ? ThoroughPipelines : FastPipelines;
        var best = new EncodedBlock([], PayloadCodec.Raw, input.ToArray());
        var candidates = new List<(TransformId[] Pipeline, byte[] Transformed, byte[] Probe)>();

        foreach (var pipeline in pipelines)
        {
            byte[] transformed;
            if (pipeline.Length == 1 && input.Length >= 64 * 1024 && _cuda is not null &&
                _cuda.TryTransform(input, pipeline[0], out transformed))
            {
                CudaUsed = true;
            }
            else
            {
                transformed = ReversibleTransforms.ApplyPipeline(input, pipeline);
            }

            var probeQuality = Math.Min(2, _options.BrotliQuality);
            var probe = CompressBrotli(transformed, probeQuality, 22);
            candidates.Add((pipeline, transformed, probe));
        }

        var finalistCount = _options.ThoroughSearch ? Math.Min(4, candidates.Count) : candidates.Count;
        foreach (var candidate in candidates
                     .OrderBy(value => value.Probe.Length + value.Pipeline.Length)
                     .Take(finalistCount))
        {
            var compressed = _options.BrotliQuality <= 2
                ? candidate.Probe
                : CompressBrotli(candidate.Transformed, _options.BrotliQuality, 24);
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

    private static byte[] CompressBrotli(ReadOnlySpan<byte> input, int quality, int window)
    {
        // Leave extra room for metadata emitted by high-quality modes. Some runtime
        // versions return a bound that is exact for the default encoder parameters.
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
