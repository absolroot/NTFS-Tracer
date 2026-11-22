<p align="center">
  <img src="docs/media/logo.svg" width="96" height="96" alt="NTFS Tracer logo">
</p>

<h1 align="center">NTFS Tracer</h1>

<p align="center"><strong>Search NTFS changes and follow what happened to a file.</strong></p>

<p align="center"><strong><a href="https://github.com/absolroot/NTFS-Tracer/releases/latest">Download for Windows x64</a></strong> · <a href="#quick-start">Quick start</a> · <a href="#supported-inputs">Supported inputs</a> · <a href="#performance">Performance</a></p>

<p align="center"><a href="README.ko.md">한국어</a> · <a href="README.ja.md">日本語</a></p>

NTFS Tracer is a Windows workbench for the USN change journal, Master File Table, and NTFS transaction log. Exporting records, reapplying filters, and switching tools can interrupt an investigation. NTFS Tracer brings journal search and per-file timelines together so an analyst can see when a file was created, renamed, moved, or deleted, and which records support that sequence.

## From a search hit to the evidence

[![English NTFS Tracer demo showing a journal search and the old and new names in a file's rename history](docs/media/investigation.gif)](docs/media/investigation.gif)

**Search for `report` → open file history → check the previous and new names.** The v0.8.29 demo uses 391 synthetic records across 131 files.

[Journal search](docs/media/journal-search.png) · [File history](docs/media/file-history.png)

## Quick start

1. Extract the Windows x64 ZIP and run **NtfsTracer.exe**. The shared-runtime ZIP requires **.NET 10 Windows Desktop Runtime (x64)**.
2. Open the source that matches your evidence.
   - **Extracted artifacts** · Choose **Select files…** or **Select extracted folder…**, then **Start analysis**. A `$J` alone is enough for USN history.
   - **Disk image** · Choose **Select files…**, select the NTFS volume and artifacts, then **Extract and analyze**.
   - **Tracker results** · Choose **File → Open Tracker results…**. For TSV, choose **All files** in the file picker.
3. Search **All records**, open **File history**, or switch to **NTFS transactions**. Select an event to see its details and contributing records. **F1** opens the in-app guide.

## Supported inputs

### Disk and evidence images

| Format | Supported variants |
| --- | --- |
| RAW (`.raw`, `.img`, `.dd`) | NTFS volume or MBR/GPT disk. Consecutive split RAW sets start at `.000` or `.001`. |
| EnCase EWF (`.E01`, `.Ex01`) | Physical images. Select the first segment and keep the complete set together. Encryption and bzip2 are unsupported. |
| Logical EWF (`.L01`, `.Lx01`) | Select the first segment and choose artifacts from the collected file catalog. The same EWF limits apply. |
| QCOW / QCOW2 | Standalone QCOW1 and QCOW2 v2/v3 with standard L2 tables. Backing files, external data, and encryption are unsupported. |
| VHD / VHDX | Fixed and dynamic VHD and standalone VHDX. Differencing disks are unsupported. |
| VMDK | Single sparse, stream-optimized, and descriptors referencing FLAT extents in the same directory. Parent chains are unsupported. |
| AFF (`.aff`) | Single AFF1 with uncompressed, zlib, or zero pages |

Physical images must contain a readable NTFS volume. A locked BitLocker volume must be unlocked outside NTFS Tracer. See the [image module](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/README.md) for the full variant limits.

### Extracted artifacts and existing results

| Input | Use it for |
| --- | --- |
| `$J` / `$UsnJrnl:$J` | USN change search, file history, and investigation leads |
| `$MFT` from the same volume | Attach to `$J` or `$LogFile` to supplement paths using file and folder names recorded at acquisition |
| `$LogFile` | NTFS transactions, LSNs, and Redo/Undo payloads |
| NTFS Log Tracker SQLite, CSV or TSV | Browse and filter existing results in the read-only viewer |

You can also select a folder containing extracted artifacts. The app opens evidence read-only and writes extractions and indexes separately. New installations use `%LOCALAPPDATA%\NtfsTracer`. Existing JournalDesk settings and indexes can be reused.

## Investigation features

| Task | What you can check |
| --- | --- |
| Find a file | Search names, aliases, paths, file IDs and parent IDs. Refine USN results by time, reason, or exact USN. |
| Reconstruct an event | Follow one file through changes, compare the two sides of a rename or move, and see which path portions came from the journal or the acquired MFT. |
| Review transactions | Inspect LSNs and Redo/Undo details and use nearby records for context. |
| Inspect and export | Review decoded fields, [Hex](docs/media/source-hex.png), or JSON. Return to your search or export results to CSV. |
| Examine incomplete evidence | Scan supplied files for candidate records. Check investigation leads and carving results against the source bytes. |

## Compare workflows

Suppose you know a file's current name and want to find its previous name and surrounding activity. The tools differ in where you narrow the evidence and what you get back.

| Tool | Start with | How you find the rename | What you work with |
| --- | --- | --- | --- |
| **NTFS Tracer** | An extracted `$J` or a supported disk image | Search the current name with **Include previous names** enabled, then open **File history**. | A per-file timeline with the previous and new names together, the contributing USN records underneath, and a return path to the original search. |
| [MFTECmd](https://github.com/EricZimmerman/MFTECmd) | An extracted `$J`; optionally a matching `$MFT` | Export with the CLI, then filter rows for the same file reference and compare the rename reasons. | CSV or JSON with one row per journal record. With `-m`, MFTECmd can reconstruct historical parent paths by rewinding directory changes. |
| [NTFS Log Tracker](https://sites.google.com/site/forensicnote/ntfs-log-tracker) | Extracted NTFS artifacts | Analyze the artifacts in the GUI and search its results by name or file reference. When `$LogFile` is available, check Tracker’s reconstructed rename or move events as additional evidence. | Searchable and exportable results from USN records and, when supplied, reconstructed `$LogFile` events. |
| [Velociraptor](https://docs.velociraptor.app/artifact_references/pages/windows.forensics.usn/) | A Windows endpoint or collected `$J` | Run `Windows.Forensics.Usn`. Use `FileNameRegex` to find the file, then clear the filename filter and query by `MFT_ID_Regex` to include changes under other names. | Filtered records as artifact results, which can be collected from multiple endpoints and queried further. |

This table compares documented steps and outputs. The timings below come from a separate lookup workload.

## Performance

The Rust engine parses NTFS artifacts into reusable SQLite indexes. When indexing multiple sources, the app can start showing USN records once the journal index is ready, while MFT and transaction indexing continue. The investigation view pages through results and can reuse a verified path-sort cache.

### 16 file-reference lookups on 480,000 records

| Method | Index creation | Time to retrieve the records |
| --- | ---: | ---: |
| [Sootmark USN](https://github.com/Sootmark/usn) harness, all 16 references known in advance | — | **444 ms** in one pass |
| Sootmark USN harness, 16 separate lookups | — | **6.66 s** across 16 passes |
| **NTFS Tracer**, 16 indexed lookups | **2.67 s** once | **3.10 ms** after indexing |

For a known list, a single scan returns results without waiting for an index. A reusable index saves time when new questions come up during an investigation. All methods returned the same names, USNs, and source offsets. These are backend measurements from a synthetic journal. The comparison above describes workflows for MFTECmd, NTFS Log Tracker, and Velociraptor. [Measurement method and raw timings](docs/performance.md)

## Build from source

The desktop app uses C# and WPF and the indexing engine uses Rust. On Windows x64, install the .NET 10 SDK, the Rust MSVC toolchain, and Visual Studio C++ Build Tools. For linting, install `rustfmt` and `clippy`.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

For EWF support, build `JournalDesk.libewf.dll` using the [libewf instructions](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/Logical/Native/README.md) before publishing.

[Rust engine](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/engine) · [Index format](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/engine/INDEX_FORMAT.md) · [WPF app](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/gui) · [Regression tests](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/tests)

## Display language

Choose from 16 interface languages under **View → Display language**. Changes apply immediately.

## Third-party notices

[Dependency and redistribution notices](THIRD-PARTY-NOTICES.md)

## License

Project-owned code is available under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires prior written permission, including internal investigations at for-profit companies, paid client work, and inclusion in commercial products. For permission, [open a licensing inquiry](https://github.com/absolroot/NTFS-Tracer/issues/new) with a brief description of the intended use and a way to contact you. Keep case details and evidence out of the public issue.

Third-party components retain their own terms and may be used under those terms. See [third-party notices](THIRD-PARTY-NOTICES.md), including the terms and replacement instructions for libewf.
