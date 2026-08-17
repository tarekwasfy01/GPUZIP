using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuZip.Core;

internal static unsafe class PreflateCodec
{
    private const string LibraryName = "preflate_rs_0_7.dll";
    private const int CompressionLevel = 14;
    private const uint VerifyFlag = 0x20;
    private const int IoChunkSize = 4 * 1024 * 1024;
    private const int ErrorBufferSize = 4096;
    private static readonly Lazy<string?> LibraryPath = new(FindLibraryPath);

    static PreflateCodec()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(PreflateCodec).Assembly, ResolveLibrary); }
        catch (InvalidOperationException) { }
    }

    public static bool IsAvailable => LibraryPath.Value is not null;

    public static bool TryCompress(ReadOnlySpan<byte> input, out byte[] compressed)
    {
        compressed = Array.Empty<byte>();
        if (!IsAvailable || input.Length < 64 * 1024) return false;
        try
        {
            compressed = Process(input, true, 0);
            return compressed.Length < input.Length;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            compressed = Array.Empty<byte>();
            return false;
        }
    }

    public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedLength)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("This GPUZIP archive uses the Preflate MSIX/ZIP codec, but preflate_rs_0_7.dll is not available.");
        var result = Process(input, false, expectedLength);
        if (result.Length != expectedLength)
            throw new InvalidDataException($"Preflate reconstructed {result.Length} bytes, expected {expectedLength}.");
        return result;
    }

    private static byte[] Process(ReadOnlySpan<byte> input, bool compress, int expectedLength)
    {
        var context = compress
            ? create_compression_context((uint)CompressionLevel | VerifyFlag)
            : create_decompression_context(0, expectedLength > 0 ? (ulong)expectedLength : 0);
        if (context == 0) throw new InvalidOperationException("Could not create Preflate context.");

        try
        {
            using var output = expectedLength > 0 ? new MemoryStream(expectedLength) : new MemoryStream(Math.Max(64 * 1024, input.Length));
            var outputBuffer = new byte[IoChunkSize];
            var errorBuffer = new byte[ErrorBufferSize];
            var offset = 0;
            while (offset < input.Length)
            {
                var length = Math.Min(IoChunkSize, input.Length - offset);
                var result = Invoke(context, input.Slice(offset, length), false, outputBuffer, errorBuffer, compress, out var written);
                if (result < 0) ThrowNativeError(result, errorBuffer, compress);
                if (written > 0) output.Write(outputBuffer, 0, written);
                offset += length;
            }
            while (true)
            {
                var result = Invoke(context, ReadOnlySpan<byte>.Empty, true, outputBuffer, errorBuffer, compress, out var written);
                if (result < 0) ThrowNativeError(result, errorBuffer, compress);
                if (written > 0) output.Write(outputBuffer, 0, written);
                if (result == 1) break;
            }
            return output.ToArray();
        }
        finally
        {
            if (compress) free_compression_context(context); else free_decompression_context(context);
        }
    }

    private static int Invoke(nint context, ReadOnlySpan<byte> input, bool complete, byte[] output, byte[] error, bool compress, out int written)
    {
        ulong nativeWritten = 0;
        int result;
        fixed (byte* pOut = output)
        fixed (byte* pErr = error)
        fixed (byte* pIn = input)
        {
            result = compress
                ? compress_buffer(context, input.IsEmpty ? null : pIn, (ulong)input.Length, complete, pOut, (ulong)output.Length, &nativeWritten, pErr, (ulong)error.Length)
                : decompress_buffer(context, input.IsEmpty ? null : pIn, (ulong)input.Length, complete, pOut, (ulong)output.Length, &nativeWritten, pErr, (ulong)error.Length);
        }
        if (nativeWritten > (ulong)output.Length) throw new InvalidDataException("Preflate returned an invalid output length.");
        written = checked((int)nativeWritten);
        return result;
    }

    private static void ThrowNativeError(int result, byte[] errorBuffer, bool compress)
    {
        var end = Array.IndexOf(errorBuffer, (byte)0);
        if (end < 0) end = errorBuffer.Length;
        var message = Encoding.UTF8.GetString(errorBuffer, 0, end).Trim();
        if (message.Length == 0) message = "unknown native error";
        throw new InvalidOperationException($"Preflate {(compress ? "compression" : "decompression")} failed ({result}): {message}");
    }

    private static string? FindLibraryPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("GPUZIP_PREFLATE_DLL");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, LibraryName),
            Path.Combine(AppContext.BaseDirectory, "Tools", "preflate", LibraryName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase)) return 0;
        var path = LibraryPath.Value;
        return path is null ? 0 : NativeLibrary.Load(path);
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern nint create_compression_context(uint flags);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern void free_compression_context(nint context);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern int compress_buffer(nint context, byte* inputBuffer, ulong inputBufferSize, [MarshalAs(UnmanagedType.I1)] bool inputComplete, byte* outputBuffer, ulong outputBufferSize, ulong* resultSize, byte* errorString, ulong errorStringBufferLength);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern nint create_decompression_context(uint flags, ulong capacity);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern void free_decompression_context(nint context);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] private static extern int decompress_buffer(nint context, byte* inputBuffer, ulong inputBufferSize, [MarshalAs(UnmanagedType.I1)] bool inputComplete, byte* outputBuffer, ulong outputBufferSize, ulong* resultSize, byte* errorString, ulong errorStringBufferLength);
}
