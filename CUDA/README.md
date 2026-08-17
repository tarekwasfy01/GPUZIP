# GPUZIP

GPUZIP is a Windows archive manager built with WinUI 3. It combines the current
official 7-Zip engine with an experimental, bit-exact `.gpuz` format that searches
several reversible predictor/transform pipelines per block.

## Features

- Create, browse, test, and extract `.gpuz` archives.
- Create common 7-Zip formats (`7z`, `zip`, `tar`, `gzip`, `bzip2`, `xz`).
- Browse, test, and extract every format supported by the bundled `7zz.exe`,
  including RAR/RAR5 extraction.
- CUDA acceleration for byte-delta and byte-XOR candidate generation through the
  NVIDIA driver API. A CPU implementation is always available and every archive
  can be decoded without an NVIDIA GPU.
- Per-block SHA-256 integrity verification and safe path validation on extraction.

## Build

Run from PowerShell:

```powershell
.\build-release.ps1
```

The build script compiles the official 7-Zip source, builds the Core, CLI,
self-tests and WinUI 3 application, executes the end-to-end tests, then publishes
the unpackaged x64 app to `release\GPUZIP-win-x64`.

## Source layout

- `src/GpuZip.Core` — `.gpuz` format, CUDA driver backend, 7-Zip adapter.
- `src/GpuZip.Cli` — command-line interface and diagnostics.
- `src/GpuZip.App` — WinUI 3 desktop application.
- `tests/GpuZip.SelfTest` — dependency-free end-to-end tests.
- `third_party/7zip` — official upstream 7-Zip source.

## Licensing

GPUZIP's original code is provided under the MIT license. The bundled 7-Zip
engine has its own LGPL/BSD/unRAR terms. See `THIRD-PARTY-NOTICES.md` and the
complete upstream license copied into release output.
