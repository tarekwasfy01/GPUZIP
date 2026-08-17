using System.Runtime.InteropServices;

namespace GpuZip.Core;

internal sealed unsafe partial class CudaTransformer : IDisposable
{
    private const string Ptx = """
.version 7.0
.target sm_75
.address_size 64

.visible .entry gpuz_transform(
    .param .u64 input_ptr,
    .param .u64 output_ptr,
    .param .u32 length,
    .param .u32 mode)
{
    .reg .pred %p<4>;
    .reg .b16 %h<4>;
    .reg .b32 %r<8>;
    .reg .b64 %rd<8>;

    ld.param.u64 %rd1, [input_ptr];
    ld.param.u64 %rd2, [output_ptr];
    ld.param.u32 %r1, [length];
    ld.param.u32 %r2, [mode];
    mov.u32 %r3, %tid.x;
    mov.u32 %r4, %ctaid.x;
    mov.u32 %r5, %ntid.x;
    mad.lo.s32 %r0, %r4, %r5, %r3;
    setp.ge.u32 %p0, %r0, %r1;
    @%p0 bra DONE;
    cvt.u64.u32 %rd3, %r0;
    add.s64 %rd4, %rd1, %rd3;
    add.s64 %rd5, %rd2, %rd3;
    ld.global.u8 %h0, [%rd4];
    setp.eq.u32 %p1, %r0, 0;
    @%p1 bra STORE;
    add.s64 %rd6, %rd4, -1;
    ld.global.u8 %h1, [%rd6];
    setp.eq.u32 %p2, %r2, 1;
    @%p2 sub.u16 %h0, %h0, %h1;
    @!%p2 xor.b16 %h0, %h0, %h1;
STORE:
    st.global.u8 [%rd5], %h0;
DONE:
    ret;
}
""";

    private nint _context;
    private nint _module;
    private nint _function;

    private CudaTransformer(nint context, nint module, nint function, string deviceName)
    {
        _context = context;
        _module = module;
        _function = function;
        DeviceName = deviceName;
    }

    public string DeviceName { get; }

    public static CudaDeviceInfo Probe()
    {
        try
        {
            Check(Cuda.cuInit(0));
            Check(Cuda.cuDeviceGet(out var device, 0));
            var name = new byte[256];
            Check(Cuda.cuDeviceGetName(name, name.Length, device));
            return new(true, System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0'), "CUDA driver backend ready");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or CudaException)
        {
            return new(false, "CPU fallback", ex.Message);
        }
    }

    public static bool TryCreate(out CudaTransformer? transformer)
    {
        transformer = null;
        nint context = 0;
        nint module = 0;
        try
        {
            Check(Cuda.cuInit(0));
            Check(Cuda.cuDeviceGet(out var device, 0));
            var name = new byte[256];
            Check(Cuda.cuDeviceGetName(name, name.Length, device));
            Check(Cuda.cuCtxCreate_v2(out context, 0, device));
            var ptx = Marshal.StringToCoTaskMemAnsi(Ptx);
            try
            {
                Check(Cuda.cuModuleLoadData(out module, ptx));
                Check(Cuda.cuModuleGetFunction(out var function, module, "gpuz_transform"));
                transformer = new CudaTransformer(context, module, function,
                    System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0'));
                context = 0;
                module = 0;
                return true;
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptx);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or CudaException)
        {
            if (module != 0) _ = Cuda.cuModuleUnload(module);
            if (context != 0) _ = Cuda.cuCtxDestroy_v2(context);
            transformer = null;
            return false;
        }
    }

    public bool TryTransform(ReadOnlySpan<byte> input, TransformId id, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (id is not (TransformId.DeltaByte or TransformId.XorByte)) return false;
        if (input.IsEmpty) { output = Array.Empty<byte>(); return true; }

        ulong deviceInput = 0;
        ulong deviceOutput = 0;
        try
        {
            Check(Cuda.cuMemAlloc_v2(out deviceInput, (nuint)input.Length));
            Check(Cuda.cuMemAlloc_v2(out deviceOutput, (nuint)input.Length));
            fixed (byte* inputPointer = input)
            {
                Check(Cuda.cuMemcpyHtoD_v2(deviceInput, (nint)inputPointer, (nuint)input.Length));
            }

            var length = (uint)input.Length;
            var mode = id == TransformId.DeltaByte ? 1u : 2u;
            var inputArg = deviceInput;
            var outputArg = deviceOutput;
            nint* parameters = stackalloc nint[4];
            parameters[0] = (nint)(&inputArg);
            parameters[1] = (nint)(&outputArg);
            parameters[2] = (nint)(&length);
            parameters[3] = (nint)(&mode);
            var blocks = (uint)((input.Length + 255) / 256);
            Check(Cuda.cuLaunchKernel(_function, blocks, 1, 1, 256, 1, 1, 0, 0, (nint)parameters, 0));
            Check(Cuda.cuCtxSynchronize());

            output = new byte[input.Length];
            fixed (byte* outputPointer = output)
            {
                Check(Cuda.cuMemcpyDtoH_v2((nint)outputPointer, deviceOutput, (nuint)output.Length));
            }
            return true;
        }
        catch (CudaException)
        {
            output = Array.Empty<byte>();
            return false;
        }
        finally
        {
            if (deviceInput != 0) _ = Cuda.cuMemFree_v2(deviceInput);
            if (deviceOutput != 0) _ = Cuda.cuMemFree_v2(deviceOutput);
        }
    }

    public void Dispose()
    {
        if (_module != 0) { _ = Cuda.cuModuleUnload(_module); _module = 0; }
        if (_context != 0) { _ = Cuda.cuCtxDestroy_v2(_context); _context = 0; }
        GC.SuppressFinalize(this);
    }

    private static void Check(int result)
    {
        if (result != 0) throw new CudaException(result);
    }

    private sealed class CudaException(int code) : Exception($"CUDA driver error {code}");

    private static partial class Cuda
    {
        private const string Library = "nvcuda.dll";
        [LibraryImport(Library)] internal static partial int cuInit(uint flags);
        [LibraryImport(Library)] internal static partial int cuDeviceGet(out int device, int ordinal);
        [LibraryImport(Library)] internal static partial int cuDeviceGetName([Out] byte[] name, int len, int dev);
        [LibraryImport(Library)] internal static partial int cuCtxCreate_v2(out nint context, uint flags, int dev);
        [LibraryImport(Library)] internal static partial int cuCtxDestroy_v2(nint context);
        [LibraryImport(Library)] internal static partial int cuCtxSynchronize();
        [LibraryImport(Library)] internal static partial int cuModuleLoadData(out nint module, nint image);
        [LibraryImport(Library)] internal static partial int cuModuleUnload(nint module);
        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int cuModuleGetFunction(out nint function, nint module, string name);
        [LibraryImport(Library)] internal static partial int cuMemAlloc_v2(out ulong devicePointer, nuint bytes);
        [LibraryImport(Library)] internal static partial int cuMemFree_v2(ulong devicePointer);
        [LibraryImport(Library)] internal static partial int cuMemcpyHtoD_v2(ulong destination, nint source, nuint bytes);
        [LibraryImport(Library)] internal static partial int cuMemcpyDtoH_v2(nint destination, ulong source, nuint bytes);
        [LibraryImport(Library)] internal static partial int cuLaunchKernel(nint function, uint gx, uint gy, uint gz, uint bx, uint by, uint bz, uint sharedMemoryBytes, nint stream, nint kernelParams, nint extra);
    }
}
