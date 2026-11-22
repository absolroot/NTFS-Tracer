<p align="center">
  <img src="docs/media/logo.svg" width="96" height="96" alt="NTFS Tracer logo">
</p>

<h1 align="center">NTFS Tracer</h1>

<p align="center"><strong>Search NTFS changes, trace a file's history, and verify the original bytes.</strong></p>

<p align="center">Windows x64 · Rust indexing engine · C# / WPF desktop app</p>

<p align="center"><a href="README.ko.md">한국어</a> · <a href="README.ja.md">日本語</a> · <a href="https://github.com/absolroot/NTFS-Tracer/releases/latest">Download for Windows</a> · <a href="#quick-start">Quick start</a> · <a href="#performance">Performance</a></p>

NTFS Tracer is a Windows forensic workbench for the USN change journal, Master File Table, and NTFS transaction log. Open extracted artifacts or a supported disk image, search across the journal, then follow a rename or move from the file's history to its underlying records. Each source record can be checked at its original offset.

## From a search hit to the evidence

![English NTFS Tracer demo showing journal search, a rename with two source records, and the selected record in Hex](docs/media/investigation.gif)

The demo uses a synthetic USN journal. Search for `report`, open the file's rename history, then inspect the new-name record in Hex at source offset `0x1D0`.

[Journal search](docs/media/journal-search.png) · [Rename history](docs/media/file-history.png) · [Source Hex](docs/media/source-hex.png)

| What you want to do | What NTFS Tracer shows |
| --- | --- |
| Find a past file or folder | Search by current or previous file name, path, file ID, or parent ID. Refine by time, change reason, or exact USN. |
| Reconstruct a rename or move | Follow one file across its history, compare old and new names and paths, and open the source record for each side. |
| Check how a path was resolved | See whether a path comes from journal evidence or was supplemented from the acquired MFT. MFT-derived portions may reflect the acquisition-time location. |
| Inspect an NTFS transaction | Search the transaction log, inspect LSNs and Redo/Undo data, and jump to the source offset. |
| Verify, pivot, and export | Inspect decoded fields, Hex, or JSON for a USN record, view nearby records, return to the search, and export matching records or a file history to CSV. |
| Examine incomplete material | Scan supplied files for candidate USN or transaction records, inspect investigation leads, or open NTFS Log Tracker SQLite and CSV results. |

### Working with NTFS Log Tracker results

Open an existing [NTFS Log Tracker](https://sites.google.com/site/forensicnote/ntfs-log-tracker) SQLite database or CSV through the read-only results viewer. To follow a journal record into file history and verify its original bytes, open the extracted NTFS artifacts in NTFS Tracer. The timings below compare versions of NTFS Tracer on the same inputs.

## Performance

The Rust engine parses NTFS artifacts into reusable SQLite indexes. When indexing multiple sources, the app can start showing USN records once the journal index is ready, while MFT and transaction indexing continue. The investigation view pages through results and can reuse a verified path-sort cache.

| Measured operation | Before | After |
| --- | ---: | ---: |
| First USN results from an empty app index, app 0.8.13 → 0.8.14 | 3.88 s | **1.97 s** |
| Inspect source Hex in the same workflow | 3.96 s | **2.03 s** |
| Complete all three artifact indexes in the same workflow | 3.64 s | 3.71 s |
| Index a transaction log with 171,581 records, Rust engine 0.4.0 → 0.4.1 | 1.17 s | **0.84 s** |
| Reopen the path sort for 333,831 records after app restart, app 0.8.12 | 3.63 s first sort | **186 ms** with cache |

The first three figures are medians of three runs per version using the same extracted artifacts, a warm OS file cache, and one logical processor. The standalone engine figures are medians of three runs per version on the same transaction log. The path-sort figures are single GUI runs. See [benchmark details](docs/performance.md) for inputs and evidence checks.

## Quick start

1. Download and extract the [Windows x64 package](https://github.com/absolroot/NTFS-Tracer/releases/latest) and run **NtfsTracer.exe**. The full package includes the runtime. The shared-runtime package requires **.NET 10 Windows Desktop Runtime (x64)**.
2. In **Settings**, select extracted files, an extraction folder, or a disk image. For an image, choose the NTFS volume and artifacts before extracting and analyzing.
3. Search **All Records**, open **File History**, or switch to **NTFS Transactions**. Select a record to inspect its details and source bytes. Press **F1** for the in-app guide.

| Input | Use it for |
| --- | --- |
| `$J` | USN changes, names, file history, and investigation leads |
| `$MFT` from the same volume | Supplement paths in the journal and transaction log |
| `$LogFile` | LSNs, transactions, and Redo/Undo payloads |
| Disk or logical evidence image | Extract NTFS artifacts to analyze in the same workflow |

Supported image families include RAW, E01/Ex01, L01/Lx01, QCOW/QCOW2, VHD/VHDX, VMDK, and selected AFF1 variants. See the [image format and variant matrix](modules/EvidenceImage/README.md). Physical images must contain an NTFS volume. The app opens evidence read-only and writes extractions and indexes separately. New installations use `%LOCALAPPDATA%\NtfsTracer`. Existing JournalDesk settings and indexes can be reused.

## Build from source

On Windows x64, install the .NET 10 SDK, the Rust MSVC toolchain, and Visual Studio C++ Build Tools. For linting, install the Rust components `rustfmt` and `clippy`.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

For EWF support, build `JournalDesk.libewf.dll` using the [libewf instructions](modules/EvidenceImage/Logical/Native/README.md) before publishing.

[Rust engine](engine/) · [Index format](engine/INDEX_FORMAT.md) · [WPF app](gui/) · [Image module](modules/EvidenceImage/) · [Regression tests](tests/) · [Image fixtures](tests/EvidenceImage/README.md)

## Third-party notices

[Dependency and redistribution notices](THIRD-PARTY-NOTICES.md)



## License

Project-owned code is available under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires prior written permission, including internal investigations at for-profit companies, paid client work, and inclusion in commercial products. For permission, [open a licensing inquiry](https://github.com/absolroot/NTFS-Tracer/issues/new) with a brief description of the intended use and a way to contact you. Keep case details and evidence out of the public issue.

Third-party components retain their own terms and may be used under those terms. See [third-party notices](THIRD-PARTY-NOTICES.md), including the terms and replacement instructions for libewf.
