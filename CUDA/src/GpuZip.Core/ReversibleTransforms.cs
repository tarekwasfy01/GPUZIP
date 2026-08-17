using System.Buffers.Binary;

namespace GpuZip.Core;

internal static class ReversibleTransforms
{
    public static byte[] Apply(ReadOnlySpan<byte> input, TransformId id) => id switch
    {
        TransformId.DeltaByte => DeltaByte(input),
        TransformId.XorByte => XorByte(input),
        TransformId.ByteShuffle2 => Shuffle(input, 2),
        TransformId.ByteShuffle4 => Shuffle(input, 4),
        TransformId.ByteShuffle8 => Shuffle(input, 8),
        TransformId.DeltaWord2 => DeltaWords(input, 2),
        TransformId.DeltaWord4 => DeltaWords(input, 4),
        TransformId.DeltaWord8 => DeltaWords(input, 8),
        _ => throw new InvalidDataException($"Unknown transform {id}.")
    };

    public static byte[] Reverse(ReadOnlySpan<byte> input, TransformId id) => id switch
    {
        TransformId.DeltaByte => UndeltaByte(input),
        TransformId.XorByte => UnxorByte(input),
        TransformId.ByteShuffle2 => Unshuffle(input, 2),
        TransformId.ByteShuffle4 => Unshuffle(input, 4),
        TransformId.ByteShuffle8 => Unshuffle(input, 8),
        TransformId.DeltaWord2 => UndeltaWords(input, 2),
        TransformId.DeltaWord4 => UndeltaWords(input, 4),
        TransformId.DeltaWord8 => UndeltaWords(input, 8),
        _ => throw new InvalidDataException($"Unknown transform {id}.")
    };

    public static byte[] ApplyPipeline(ReadOnlySpan<byte> input, IReadOnlyList<TransformId> pipeline)
    {
        var current = input.ToArray();
        foreach (var transform in pipeline)
        {
            current = Apply(current, transform);
        }
        return current;
    }

    public static byte[] ReversePipeline(ReadOnlySpan<byte> input, IReadOnlyList<TransformId> pipeline)
    {
        var current = input.ToArray();
        for (var i = pipeline.Count - 1; i >= 0; i--)
        {
            current = Reverse(current, pipeline[i]);
        }
        return current;
    }

    private static byte[] DeltaByte(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        if (input.IsEmpty) return output;
        output[0] = input[0];
        for (var i = 1; i < input.Length; i++) output[i] = unchecked((byte)(input[i] - input[i - 1]));
        return output;
    }

    private static byte[] UndeltaByte(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        if (input.IsEmpty) return output;
        output[0] = input[0];
        for (var i = 1; i < input.Length; i++) output[i] = unchecked((byte)(output[i - 1] + input[i]));
        return output;
    }

    private static byte[] XorByte(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        if (input.IsEmpty) return output;
        output[0] = input[0];
        for (var i = 1; i < input.Length; i++) output[i] = (byte)(input[i] ^ input[i - 1]);
        return output;
    }

    private static byte[] UnxorByte(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        if (input.IsEmpty) return output;
        output[0] = input[0];
        for (var i = 1; i < input.Length; i++) output[i] = (byte)(input[i] ^ output[i - 1]);
        return output;
    }

    private static byte[] Shuffle(ReadOnlySpan<byte> input, int width)
    {
        var output = new byte[input.Length];
        var words = input.Length / width;
        var full = words * width;
        for (var lane = 0; lane < width; lane++)
        {
            for (var word = 0; word < words; word++) output[lane * words + word] = input[word * width + lane];
        }
        input[full..].CopyTo(output.AsSpan(full));
        return output;
    }

    private static byte[] Unshuffle(ReadOnlySpan<byte> input, int width)
    {
        var output = new byte[input.Length];
        var words = input.Length / width;
        var full = words * width;
        for (var lane = 0; lane < width; lane++)
        {
            for (var word = 0; word < words; word++) output[word * width + lane] = input[lane * words + word];
        }
        input[full..].CopyTo(output.AsSpan(full));
        return output;
    }

    private static byte[] DeltaWords(ReadOnlySpan<byte> input, int width)
    {
        var output = input.ToArray();
        var count = input.Length / width;
        if (count < 2) return output;
        for (var i = count - 1; i >= 1; i--)
        {
            var current = ReadWord(input.Slice(i * width, width), width);
            var previous = ReadWord(input.Slice((i - 1) * width, width), width);
            WriteWord(output.AsSpan(i * width, width), width, current - previous);
        }
        return output;
    }

    private static byte[] UndeltaWords(ReadOnlySpan<byte> input, int width)
    {
        var output = input.ToArray();
        var count = input.Length / width;
        for (var i = 1; i < count; i++)
        {
            var delta = ReadWord(input.Slice(i * width, width), width);
            var previous = ReadWord(output.AsSpan((i - 1) * width, width), width);
            WriteWord(output.AsSpan(i * width, width), width, previous + delta);
        }
        return output;
    }

    private static ulong ReadWord(ReadOnlySpan<byte> value, int width) => width switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };

    private static void WriteWord(Span<byte> target, int width, ulong value)
    {
        switch (width)
        {
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(target, unchecked((ushort)value)); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(target, unchecked((uint)value)); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(target, value); break;
            default: throw new ArgumentOutOfRangeException(nameof(width));
        }
    }
}
