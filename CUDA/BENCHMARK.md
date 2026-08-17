# Compression benchmark

The benchmark uses a deterministic 21,953,090-byte mixed corpus with 103 files:
repetitive source-like text, monotonic 32-bit integers, JSON records, and 4 MiB
of incompressible pseudo-random data. Every extracted tree was verified against
the source with a SHA-256 fingerprint.

| Method | Archive bytes | Ratio | Pack | Extract | CUDA |
|---|---:|---:|---:|---:|:---:|
| GPUZIP Ultra | 4,253,453 | 19.38% | 23.57 s | 0.32 s | Yes |
| 7z LZMA2 `-mx=9` | 4,435,778 | 20.21% | 2.63 s | 0.29 s | No |
| ZIP Deflate `-mx=9` | 7,614,609 | 34.69% | 11.62 s | 0.28 s | No |

GPUZIP was 182,325 bytes (4.11%) smaller than 7z on this corpus, but took about
nine times as long to create. This is a targeted synthetic corpus, not proof that
GPUZIP is smaller for every file type. Run the benchmark again with:

```powershell
dotnet run --project tests\GpuZip.Benchmark\GpuZip.Benchmark.csproj -c Release
```
