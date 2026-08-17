using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    public static async Task<PreflateFileCompressionResult?> TryCompressFileAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        var inputInfo = new FileInfo(inputPath);
        if (inputInfo.Length < 64 * 1024) return null;

        try
        {
            await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoChunkSize, true);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, IoChunkSize, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var written = await ProcessStreamAsync(input, input.Length, output, true, 0, hash, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var digest = hash.GetHashAndReset();

            if (written >= inputInfo.Length)
            {
                output.Close();
                File.Delete(outputPath);
                return null;
            }

            return new(inputInfo.Length, written, digest);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            return null;
        }
    }

    public static async Task<PreflateFileDecompressionResult> DecompressToStreamAsync(
        Stream compressedInput,
        long compressedLength,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("This GPUZIP archive uses whole-container Preflate, but preflate_rs_0_7.dll is not available.");
        if (compressedLength < 0) throw new ArgumentOutOfRangeException(nameof(compressedLength));
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var written = await ProcessStreamAsync(compressedInput, compressedLength, output, false, expectedLength, hash, cancellationToken).ConfigureAwait(false);
        if (written != expectedLength)
            throw new InvalidDataException($"Preflate reconstructed {written} bytes, expected {expectedLength}.");
        return new(written, hash.GetHashAndReset());
    }

    private static async Task<long> ProcessStreamAsync(
        Stream input,
        long inputLength,
        Stream output,
        bool compress,
        long expectedLength,
        IncrementalHash outputHash,
        CancellationToken cancellationToken)
    {
        var context = compress
            ? create_compression_context((uint)CompressionLevel | VerifyFlag)
            : create_decompression_context(0, expectedLength > 0 ? (ulong)expectedLength : 0);
        if (context == 0) throw new InvalidOperationException("Could not create Preflate context.");

        try
        {
            var inputBuffer = new byte[IoChunkSize];
            var outputBuffer = new byte[IoChunkSize];
            var errorBuffer = new byte[ErrorBufferSize];
            long remaining = inputLength;
            long totalWritten = 0;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(inputBuffer.Length, remaining);
                var read = await ReadExactlyOrLessAsync(input, inputBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Unexpected end of Preflate input stream.");
                remaining -= read;

                if (compress) outputHash.AppendData(inputBuffer, 0, read);
                var result = Invoke(context, inputBuffer.AsSpan(0, read), false, outputBuffer, errorBuffer, compress, out var produced);
                if (result < 0) ThrowNativeError(result, errorBuffer, compress);
                if (produced > 0)
                {
                    await output.WriteAsync(outputBuffer.AsMemory(0, produced), cancellationToken).ConfigureAwait(false);
                    if (!compress) outputHash.AppendData(outputBuffer, 0, produced);
                    totalWritten += produced;
                }
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Invoke(context, ReadOnlySpan<byte>.Empty, true, outputBuffer, errorBuffer, compress, out var produced);
                if (result < 0) ThrowNativeError(result, errorBuffer, compress);
                if (produced > 0)
                {
                    await output.WriteAsync(outputBuffer.AsMemory(0, produced), cancellationToken).ConfigureAwait(false);
                    if (!compress) outputHash.AppendData(outputBuffer, 0, produced);
                    totalWritten += produced;
                }
                if (result == 1) break;
            }

            return totalWritten;
        }
        finally
        {
            if (compress) free_compression_context(context); else free_decompression_context(context);
        }
    }

    private static async Task<int> ReadExactlyOrLessAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
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

internal sealed record PreflateFileCompressionResult(long InputBytes, long OutputBytes, byte[] Sha256);
internal sealed record PreflateFileDecompressionResult(long OutputBytes, byte[] Sha256);
