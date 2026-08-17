# Third-party notices

## 7-Zip

GPUZIP includes the standalone `7zz.exe` built from the official 7-Zip source:

- Project: https://github.com/ip7z/7zip
- Copyright: Igor Pavlov and contributors
- License: GNU LGPL 2.1 or later, with BSD-licensed components and the unRAR
  restriction for RAR decompression code.

The complete authoritative terms are included as `7zip-license.txt`. RAR code
is used only to open, test, and extract RAR archives. GPUZIP does not create RAR
archives and does not use the unRAR code to recreate the proprietary RAR encoder.

The full corresponding 7-Zip source used for the build is retained under
`third_party/7zip`.
