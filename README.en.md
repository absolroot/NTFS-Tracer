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

**Search for `report` → open file history → check the previous and new names.** The demo follows both names back to the USN records that support the rename.

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

Physical images must contain a readable NTFS volume. A locked BitLocker volume must be unlocked outside NTFS Tracer. See the [image module](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.30/modules/EvidenceImage/README.md) for the full variant limits.

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

**You know the file is now called `report-final.docx`. What was it called before?** We tried this on the same USN journal in both apps.

| | Steps | What appeared |
| --- | --- | --- |
| **NTFS Tracer** | Search for `report-final.docx` with **Include previous names** enabled, then choose **Open file history**. | The search found all five records associated with the file. The history grouped the rename into one event and displayed both names. |
| **NTFS Log Tracker 1.9** | Search **File/Directory Name** for `report-final.docx`. Copy the **FileReferenceNumber** from a result, clear the filename filter, and search again by that reference. | The first search showed two rows containing the current name. Searching by reference returned five rows, including the old-name record. |

Both workflows found the name change from `report-draft.docx` (USN 38648) to `report-final.docx` (USN 38744). When following a move, the same file reference also lets you trace records across parent folders. A full path depends on the available directory evidence.

<details>
<summary>Cross-check against MFTECmd</summary>

[MFTECmd](https://github.com/EricZimmerman/MFTECmd) 2026.11.1 exported the same five records for file reference `42-1`, with matching names, USNs, and source offsets.

```powershell
.\MFTECmd.exe -f '.\$J' --csv .\out --csvf rename.csv --co false
```

</details>

[![File history showing the old and new names in one rename activity, with both USN records beneath it](docs/media/file-history.png)](docs/media/file-history.png)

### Inspecting a transaction in `$LogFile`

| Tool | What the documented or tested view exposes |
| --- | --- |
| **ArtiFast Windows** | Its Windows LogFile artifact view lists entry offsets, record pages, current and previous LSNs, client Undo LSNs, and record types. Selecting a row exposes individual fields, including the transaction ID and Redo/Undo operations. |
| **NTFS Tracer** | Open **NTFS transactions**, search for the LSN, and select the record. The overview shows the source location and related LSNs. If the payload contains file attribute timestamps, it displays creation, content modification, metadata change, and last access values side by side for Redo and Undo. |

The ArtiFast row describes the published interface. [ArtiFast's Windows LogFile documentation](https://forensafe.com/blogs/windowslogfile.html)

### Designed around the analyst's place in the investigation

An analyst should be able to follow a name change without remembering a file reference between searches, and should be able to return to a selected record after closing the app. Experiments on interrupted tasks show that cues from the original task can help people resume it. That research informed these design choices. The timings below measure elapsed workflow time on the sample journal. [Altmann and Trafton, *Task Interruption: Resumption Lag and the Role of Cues*](https://interruptions.net/literature/Altmann-CogSci04.pdf)

On a build that includes investigation sessions, use **File → Save investigation…** and later **Resume last investigation** or **Open investigation…**. The saved file contains source paths and hashes, the active workspace, and the supported search and selection state. Keep the source files with the investigation. NTFS Tracer verifies their SHA-256 hashes when reopening and reuses compatible local indexes.

## Performance

### NTFS Tracer vs. NTFS Log Tracker

We started with an extracted USN journal and two current filenames. For each name, the task was to find what the file was called before. We used the steps above, including the second search by file reference in Tracker and opening **File history** in Tracer.

| What we timed | NTFS Tracer development build | NTFS Log Tracker 1.9 |
| --- | ---: | ---: |
| Start analysis on a fresh database and wait for the first USN list | **1.08 s** | 3.25 s |
| Start analysis and find both names for `report-final.docx` | **1.30 s** | 3.50 s |
| With the same journal open, find both names for `budget-final.docx` | 127 ms | 154 ms |

The table shows the median of ten paired trials on one PC using a small synthetic journal (6.1 MB, 63,007 records). Tracer reached the first file’s previous name sooner in all ten. For the second file, Tracer was faster in seven runs and Tracker in three. The timings cover automated app actions from the start button or search to the results. They exclude time spent choosing files, typing, or reading the records. [Steps, result checks, and all ten timings](docs/performance.md#finding-a-previous-name-from-the-current-name)

## Build from source

The desktop app uses C# and WPF and the indexing engine uses Rust. On Windows x64, install the .NET 10 SDK, the Rust MSVC toolchain, and Visual Studio C++ Build Tools. For linting, install `rustfmt` and `clippy`.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

For EWF support, build `NtfsTracer.libewf.dll` using the [libewf instructions](https://github.com/absolroot/NTFS-Tracer/blob/main/modules/EvidenceImage/Logical/Native/README.md) before publishing.

[Rust engine](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/engine) · [Index format](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.30/engine/INDEX_FORMAT.md) · [WPF app](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/gui) · [Regression tests](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/tests)

## Display language

Choose from 16 interface languages under **View → Display language**. Changes apply immediately.

## Third-party notices

[Dependency and redistribution notices](THIRD-PARTY-NOTICES.md)

## License

Project-owned code is available under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires prior written permission, including internal investigations at for-profit companies, paid client work, and inclusion in commercial products. For permission, [open a licensing inquiry](https://github.com/absolroot/NTFS-Tracer/issues/new) with a brief description of the intended use and a way to contact you. Keep case details and evidence out of the public issue.

Third-party components retain their own terms and may be used under those terms. See [third-party notices](THIRD-PARTY-NOTICES.md), including the terms and replacement instructions for libewf.
