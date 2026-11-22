# JournalDesk.libewf.dll

NTFS Tracer uses a separate replaceable x64 DLL built from the unmodified libewf-python 20260924 source distribution. The application uses its C API. The DLL uses the bundled libyal dependencies and statically linked zlib 1.4 for deflate and Adler-32. The Python extension, bzip2 and the command-line tools are excluded. Encrypted EWF is unsupported.

libewf is copyright Joachim Metz and contributors. The package declares LGPL-3.0-or-later. Consult the source file notices and the included COPYING and COPYING.LESSER for license terms. This directory includes the complete original source distribution and the CMake build recipe used for the DLL.

- [Upstream code and documentation](https://github.com/libyal/libewf)
- [Pinned source distribution](https://pypi.org/project/libewf-python/20260924/#files)
- Source archive SHA-256: `72f67b7bcb80987662b506d9a72825afe7ad07e87c0d7dfbdb0d187a32add671`
- [zlib 1.4 source release](https://github.com/madler/zlib/releases/tag/v1.4) by Jean-loup Gailly and Mark Adler. The original source and the zlib license are included as `zlib-1.4.tar.gz` and `COPYING.zlib`.
- zlib source archive SHA-256: `02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584`

To rebuild on Windows with Visual Studio 2022 C++ Build Tools (x64) and CMake, run the following from this notices directory. Install a rebuilt DLL by copying it next to NtfsTracer.exe under the name `JournalDesk.libewf.dll`. Keep the original ABI when replacing the library.

```powershell
tar -xf libewf_python-20260924.tar.gz
tar -xf zlib-1.4.tar.gz
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 "-DEWF_SOURCE=$PWD/libewf_python-20260924" "-DZLIB_SOURCE=$PWD/zlib-1.4"
cmake --build build --config Release
Copy-Item build/Release/libewf.dll JournalDesk.libewf.dll
```
