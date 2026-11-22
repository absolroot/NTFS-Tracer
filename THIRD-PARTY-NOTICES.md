# Third-party notices

Dependency notices are collected under `Notices/` in the Windows ZIPs. In this source tree, see [the application notices](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/gui/Notices), [the engine](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/engine), and [the image module](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/modules/EvidenceImage).

| Component | License | Included notice or source |
| --- | --- | --- |
| Rust engine crates | MIT, MIT OR Apache-2.0, or Zlib, depending on crate | Version inventory and original license texts under `Notices/licenses/rust/` |
| Sootmark USN code | MIT | `sootmark-LICENSE.txt` |
| Sootmark NTFS log and common crates | MIT OR Apache-2.0 | Per-crate texts under `Notices/licenses/rust/` and vendor notices in the source tree |
| LTRData.DiscUtils 1.0.91 and LTRData.Extensions 1.0.24 | MIT | `Notices/DiscUtils.txt` and original package notices under `Notices/licenses/` |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.11 | MIT | `Notices/licenses/Microsoft.Data.Sqlite-MIT.txt` |
| SQLitePCLRaw 2.1.12 packages | Apache-2.0 | `Notices/licenses/SQLitePCLRaw-Apache-2.0.txt` |
| SQLite, including the engine's bundled SQLite and e_sqlite3 | Public domain | [SQLite copyright and public-domain dedication](https://www.sqlite.org/copyright.html) |
| Microsoft Fluent System Icons | MIT | `Notices/Fluent-System-Icons.txt` |
| Rust standard library | MIT OR Apache-2.0, with component-specific notices | `Notices/runtime/Rust-Standard-Library-COPYRIGHT.html` |
| .NET runtime and Windows Desktop runtime | MIT, with separate third-party notices | Microsoft package notices under `Notices/runtime/` in the self-contained ZIP |
| libewf 20260924 and bundled libyal code | LGPL-3.0-or-later, with upstream file notices | `Notices/ewf/` contains the license texts, source archives, and build recipe |
| zlib 1.4 | Zlib | `Notices/ewf/COPYING.zlib` and the original source archive |

## Replacing libewf

NTFS Tracer uses libewf for EWF images. The Windows packages ship the library as the separate `JournalDesk.libewf.dll`. The app loads that DLL from the application directory. You may replace it with an interface-compatible modified build and reverse engineer the combined work to debug modifications to the library, as provided by the LGPL.

Both ZIPs contain the source for the shipped library, the zlib source it builds with, the GPL and LGPL license texts, and the CMake recipe. Follow `Notices/ewf/README.md` in the ZIP to rebuild the DLL and install it next to `NtfsTracer.exe`.
