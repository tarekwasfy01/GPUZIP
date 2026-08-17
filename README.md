# GPUZIP

GPUZIP is an experimental **lossless archive format and Windows archive manager** that combines reversible data transforms, adaptive per-block transform selection, Brotli compression, optional CUDA offloading, and the official 7-Zip engine.

The project is designed around a simple idea: **there is no single preprocessing transform that is best for every kind of data**. Instead of forcing one method on the entire archive, GPUZIP evaluates several reversible transform pipelines for every block and keeps the representation that produces the smallest result.

> **Status:** GPUZIP and the `.gpuz` format are experimental. The adaptive pipeline selection described below is project-specific. The underlying ideas—delta prediction, XOR prediction, byte shuffling and Brotli—are established techniques and are referenced in the papers below.

## Highlights

- Lossless `.gpuz` archive format.
- Adaptive transform selection **per block**.
- Byte-delta and byte-XOR prediction.
- 16-, 32- and 64-bit word-delta prediction.
- 16-, 32- and 64-bit byte shuffling.
- Combined **Delta + Shuffle** pipelines for structured numerical data.
- Brotli as the final general-purpose entropy/LZ compression stage.
- Raw fallback when compression would make a block larger.
- Optional NVIDIA CUDA acceleration for suitable transform candidates.
- CPU fallback at all times; decoding does **not** require CUDA.
- Per-block SHA-256 verification.
- 7-Zip integration for `7z`, `zip`, `tar`, `gzip`, `bzip2`, `xz` and formats supported for extraction by the bundled 7-Zip engine.

## The `.gpuz` compression pipeline

GPUZIP currently uses a default block size of **4 MiB**. Each block is processed independently.

```text
Input data
    |
    v
Split into blocks
    |
    v
Generate reversible candidates
    |
    +--> No transform
    +--> Byte Delta
    +--> Byte XOR
    +--> Byte Shuffle 2 / 4 / 8
    +--> Word Delta 2 / 4 / 8
    +--> Word Delta + matching Byte Shuffle
    |
    v
Fast Brotli probe of every candidate
    |
    v
Keep the most promising candidates
    |
    v
Final high-quality Brotli compression
    |
    v
Compare with uncompressed block
    |
    +--> Store smallest representation
    |
    v
Store transform metadata + SHA-256
```

The implementation can be found in [`BlockCodec.cs`](CUDA/src/GpuZip.Core/BlockCodec.cs) and [`ReversibleTransforms.cs`](CUDA/src/GpuZip.Core/ReversibleTransforms.cs).

## Compression methods

### 1. Byte Delta prediction

For a byte sequence

```text
x0, x1, x2, x3, ...
```

GPUZIP stores a reversible first-order difference representation:

```text
x0, x1-x0, x2-x1, x3-x2, ...
```

with arithmetic performed modulo 256.

This is useful when neighboring byte values change slowly. Instead of presenting the backend compressor with a sequence of large absolute values, it often produces many small or repeated differences.

The original bytes are reconstructed exactly by cumulative addition, so the transform is fully lossless.

### 2. Byte XOR prediction

GPUZIP also tests an XOR predictor:

```text
x0, x1 XOR x0, x2 XOR x1, x3 XOR x2, ...
```

XOR prediction can expose repeated bit patterns when adjacent values are similar. Long runs of equal or nearly equal values can become zero-heavy or otherwise more regular, which can improve the following compressor.

The transform is exactly reversible because each original byte is recovered using the previously reconstructed byte.

### 3. Word Delta prediction: 16 / 32 / 64 bit

Binary scientific, numerical and structured data often consists of multi-byte integer or floating-point words rather than unrelated individual bytes.

GPUZIP therefore interprets a block as little-endian words of:

- 2 bytes / 16 bit
- 4 bytes / 32 bit
- 8 bytes / 64 bit

and computes the modular difference between consecutive words.

For 32-bit words, conceptually:

```text
w0, w1, w2, w3
       |
       v
w0, w1-w0, w2-w1, w3-w2
```

This preserves the correlation of typed numerical values better than byte-wise delta coding for many datasets.

### 4. Byte Shuffle: 16 / 32 / 64 bit

Byte shuffling reorganizes multi-byte values by byte position.

For four 32-bit values stored as:

```text
[A0 A1 A2 A3] [B0 B1 B2 B3] [C0 C1 C2 C3] [D0 D1 D2 D3]
```

GPUZIP's 4-byte shuffle produces:

```text
A0 B0 C0 D0 | A1 B1 C1 D1 | A2 B2 C2 D2 | A3 B3 C3 D3
```

This does not compress data by itself. It rearranges the bytes so that bytes with the same significance are adjacent. In arrays of integers or floating-point numbers, corresponding byte lanes often have similar statistical properties, allowing the following compressor to find longer and more regular patterns.

GPUZIP tests shuffle widths of **2, 4 and 8 bytes**.

### 5. Delta + Shuffle pipelines

GPUZIP also tests combined predictors:

```text
DeltaWord16 -> ByteShuffle2
DeltaWord32 -> ByteShuffle4
DeltaWord64 -> ByteShuffle8
```

The two operations attack different kinds of redundancy:

1. **Delta** removes correlation between consecutive numerical values.
2. **Shuffle** groups the corresponding byte lanes of the resulting deltas.

This combination can be particularly effective for arrays, sensor data, scientific measurements, simulation results and other structured binary data.

### 6. Brotli backend

After each reversible candidate is generated, GPUZIP uses **Brotli** as the final lossless compressor.

The thorough search first performs a low-quality Brotli probe for all candidate pipelines, ranks them by resulting size, and then recompresses the best candidates at the configured final quality. The default final Brotli quality is currently **11**.

Brotli itself combines LZ-style dictionary/back-reference coding with entropy coding. GPUZIP does not modify the Brotli format; the transforms operate *before* Brotli.

### 7. Adaptive per-block selection

This is the central design of the `.gpuz` codec.

A transform that helps a floating-point array may make an already-compressed JPEG, ZIP or encrypted block worse. GPUZIP therefore does not assume that one transform is globally optimal.

For every block it evaluates multiple candidates and stores only the best result. The candidate set currently includes:

| Pipeline | Intended pattern |
|---|---|
| Raw / no transform | Already incompressible or naturally Brotli-friendly data |
| `DeltaByte` | Slowly changing byte streams |
| `XorByte` | Adjacent values with similar bit patterns |
| `ByteShuffle2` | 16-bit structured values |
| `ByteShuffle4` | 32-bit structured values |
| `ByteShuffle8` | 64-bit structured values |
| `DeltaWord2` | Correlated 16-bit values |
| `DeltaWord4` | Correlated 32-bit values |
| `DeltaWord8` | Correlated 64-bit values |
| `DeltaWord2 + ByteShuffle2` | Correlated 16-bit numerical arrays |
| `DeltaWord4 + ByteShuffle4` | Correlated 32-bit numerical arrays |
| `DeltaWord8 + ByteShuffle8` | Correlated 64-bit numerical arrays |

If none of the compressed candidates beats the original block, GPUZIP stores the block without Brotli compression. This prevents the adaptive search from expanding incompressible data unnecessarily.

## CUDA acceleration

CUDA is used as an **optional compute offloader**, not as a replacement for the CPU.

The CPU remains responsible for archive structure, candidate selection, Brotli, file I/O and control flow. For sufficiently large blocks, supported reversible transforms can be generated on an NVIDIA GPU through the CUDA Driver API.

The current CUDA backend accelerates the byte-delta and byte-XOR candidate generation. The CPU implementation remains the reference fallback.

```text
CPU
 |-- file I/O
 |-- block management
 |-- candidate ranking
 |-- Brotli
 |
 +---- large suitable transform ----> CUDA GPU
                                      |
                                      +--> Delta/XOR candidate
                                      |
CPU <---------------------------------+
```

This architecture has two important consequences:

- A machine without an NVIDIA GPU can still create and extract `.gpuz` archives.
- A `.gpuz` archive created with CUDA can be decoded on a CPU-only system because the archive stores the transform identity, not GPU-specific output code.

## Integrity and bit-exact decoding

Every encoded block stores a SHA-256 digest of the original uncompressed block. During extraction GPUZIP reconstructs the original bytes and verifies the digest.

The transforms themselves are integer/byte permutations or modular predictors. No floating-point approximation, quantization or rounding is used by the `.gpuz` codec. The format is therefore intended to be **bit-exact and lossless**.

## Scientific background and related papers

GPUZIP's adaptive combination of candidate transforms is an experimental project design. The individual building blocks are related to established work in lossless compression:

### Brotli

**J. Alakuijala, A. Farruggia, P. Ferragina, E. Kliuchnikov, R. Obryk, Z. Szabadka, L. Vandevenne — “Brotli: A General-Purpose Data Compressor,” ACM Transactions on Information Systems, 2019.**

https://research.google/pubs/brotli-a-general-purpose-data-compressor/

The standardized Brotli compressed data format is described in **RFC 7932**:

https://www.rfc-editor.org/rfc/rfc7932.html

GPUZIP uses Brotli as the final compressor after its reversible preprocessing transforms.

### Shuffle / Bitshuffle for typed numerical data

**K. Masui et al. — “A compression scheme for radio data in high performance computing,” Astronomy and Computing 12 (2015), 181–190.**

https://arxiv.org/abs/1503.00638

The paper introduces Bitshuffle for scientific numerical data and demonstrates the general benefit of reorganizing correlated bit/byte planes before a conventional compressor. GPUZIP currently performs **byte-level** shuffling rather than the paper's bit-level Bitshuffle algorithm, so this paper should be understood as closely related background rather than an identical implementation.

### Delta and XOR prediction

**T. Pelkonen et al. — “Gorilla: A Fast, Scalable, In-Memory Time Series Database,” Proceedings of the VLDB Endowment 8(12), 2015.**

https://doi.org/10.14778/2824032.2824078

Gorilla uses delta-based timestamp coding and XOR-based floating-point value coding to exploit similarity between consecutive values. GPUZIP's byte/word predictors are different implementations for general binary blocks, but they rely on the same broad principle: transform correlated neighboring values into a representation with lower apparent entropy before final coding.

### HDF5-style byte shuffling

The HDF5 Shuffle filter documents the same fundamental byte-lane rearrangement used by GPUZIP's `ByteShuffle2/4/8` transforms: corresponding bytes of multi-byte values are grouped together before compression.

https://support.hdfgroup.org/documentation/hdf5/latest/group___d_c_p_l.html

## Why not simply use one compressor?

General-purpose compressors are deliberately data-agnostic. They do not always recognize higher-level structure such as a stream of little-endian 32-bit measurements.

GPUZIP adds a small reversible modeling layer before Brotli:

```text
structured bytes
      |
      v
reversible modeling
      |
      v
more regular byte stream
      |
      v
Brotli
```

The important part is the adaptive decision. A predictor is used only when its measured compressed representation wins for that block.

## 7-Zip compatibility

GPUZIP also bundles the official 7-Zip engine. The GUI can create common formats such as:

- 7z
- ZIP
- TAR
- GZip
- BZip2
- XZ

and can browse/test/extract formats supported by the bundled `7zz.exe`, including RAR/RAR5 extraction where supported by 7-Zip.

The `.gpuz` format itself is independent of 7-Zip and is implemented in [`GpuZip.Core`](CUDA/src/GpuZip.Core/).

## Build

The Windows build is automated with GitHub Actions. The release workflow builds the WinUI 3 application, runs the project self-tests and startup smoke test, creates the self-extracting OneFile launcher, generates a GitHub Artifact Attestation and uploads the resulting `GPUZIP.exe` artifact.

For a local release build from PowerShell:

```powershell
cd CUDA
.\build-release.ps1
```

## Source layout

```text
CUDA/
├─ src/GpuZip.Core/       .gpuz codec, transforms, CUDA backend, 7-Zip adapter
├─ src/GpuZip.Cli/        command-line interface and diagnostics
├─ src/GpuZip.App/        WinUI 3 desktop application
├─ src/GpuZip.Launcher/   self-extracting OneFile launcher
├─ tests/                  self-tests and benchmarks
├─ third_party/7zip/       official upstream 7-Zip source
└─ build-release.ps1       release build script
```

## Important terminology

GPUZIP should not be interpreted as claiming that Delta coding, XOR coding, byte shuffling or Brotli are newly invented algorithms. The experimental contribution of the project is the **particular `.gpuz` block format, candidate set, adaptive probe-and-select strategy, optional CUDA offloading and integration of these components into one lossless archive pipeline**.

## License

GPUZIP's original code is provided under the MIT License.

The bundled 7-Zip source and binaries retain their upstream LGPL/BSD/unRAR licensing terms. See [`CUDA/THIRD-PARTY-NOTICES.md`](CUDA/THIRD-PARTY-NOTICES.md) and [`CUDA/7zip-license.txt`](CUDA/7zip-license.txt).
