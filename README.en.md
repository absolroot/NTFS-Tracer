<p align="center">
  <img src="docs/media/logo.svg" width="96" height="96" alt="NTFS Tracer logo">
</p>

<h1 align="center">NTFS Tracer</h1>

<h3 align="center">Fast NTFS log analysis, straight from a disk image.</h3>

<p align="center"><strong>Search filenames and trace file changes back to the original records.</strong></p>

<p align="center">In the first previous-name lookup, including analysis time, NTFS Tracer was <strong>over 8× as fast as NTFS Log Tracker 1.9</strong>.</p>

<p align="center">
  <a href="https://github.com/absolroot/NTFS-Tracer/releases/latest"><img src="https://badgen.net/github/release/absolroot/NTFS-Tracer/latest" alt="Latest release"></a>
  <a href="https://www.codefactor.io/repository/github/absolroot/ntfs-tracer/issues/main"><img src="https://www.codefactor.io/repository/github/absolroot/ntfs-tracer/badge/main" alt="CodeFactor code quality grade"></a>
  <a href="https://github.com/absolroot/NTFS-Tracer/actions/workflows/ci.yml"><img src="https://github.com/absolroot/NTFS-Tracer/actions/workflows/ci.yml/badge.svg?branch=main&amp;event=push" alt="Windows build and tests"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/absolroot/NTFS-Tracer?color=blue" alt="Apache 2.0 license"></a>
</p>

<p align="center"><strong><a href="https://github.com/absolroot/NTFS-Tracer/releases/latest">Download for Windows x64 or x86</a></strong> · <a href="#quick-start">Quick start</a> · <a href="#investigation-features">Investigation features</a> · <a href="#supported-inputs">Supported inputs</a> · <a href="#investigation-examples-and-tool-comparisons">Investigation examples</a></p>

<p align="center"><a href="README.ko.md">한국어</a> · <a href="README.ja.md">日本語</a></p>

## Why use NTFS Tracer?

- Extract and analyze `$J`, `$MFT`, and `$LogFile` directly from a disk image.
- Search by either filename and open the USN record and raw bytes behind each side of a rename.
- Reopen a saved investigation with its search settings and selected record.

## Quick start

### Investigate in the desktop app

1. Extract the ZIP for your Windows architecture (x64 or x86) and run **NtfsTracer.exe**. The shared-runtime ZIP requires **.NET 10 Windows Desktop Runtime** for the same architecture.
2. Open the source that matches your evidence.
   - **Extracted artifacts** · Choose **Select files…** or **Select extracted folder…**, then **Start analysis**. A `$J` alone is enough for USN history.
   - **Disk image** · Choose **Select files…**. The first readable NTFS volume is selected automatically. Choose the volume and artifacts you need, then select **Extract and analyze**.
   - **Tracker results** · Choose **File → Open Tracker results…**. For TSV, choose **All files** in the file picker.
3. Search **All records**, open **File history**, or switch to **NTFS transactions**. Select an event to see its details and contributing records. **F1** opens the in-app guide.

Before closing the app, choose **File → Save investigation…**. On your next launch, choose **Resume last investigation** to return to the saved sources, search settings, and selected record.

### Export USN to CSV

In the extracted release folder, run the command below with a `$J` containing only USN V2 records. Choose a new output filename.

```powershell
.\ntfstracer-cli.exe usn-csv '.\$J' '.\usn.csv'
```

## Investigation features

[![Full NTFS Tracer window showing journal search, file history, transactions, and investigation leads](docs/media/full-app-walkthrough-dark.gif)](docs/media/full-app-walkthrough-dark.gif)

- **Search the change journal**. Find a file by its current or previous name, path, or reference number. Narrow the USN journal by time or change reason.
- **Follow one file through changes**. Open the file history from a search result. Review a rename or move and see the records that support each side. Paths filled in from the acquired MFT are labeled with their source.
- **Compare transaction values**. Find a record by LSN in the transaction log. Compare file timestamps when both Undo and Redo contain values.
- **Review investigation leads**. Review candidates such as repeated renames before deletion, SDelete-related records, or timestamp changes. Open the supporting records to assess the lead.

Inspect decoded fields, Hex, or JSON, export results to CSV, or scan source files for remnants of USN and transaction records.

## Supported inputs

### Disk and evidence images

| Format | Extensions | Support |
| --- | --- | :---: |
| RAW | `.raw`, `.img`, `.dd`, `.000` / `.001` | ✓ |
| Physical EWF | `.e01`, `.ex01` | ✓ |
| Logical EWF | `.l01`, `.lx01` | ✓ |
| QCOW / QCOW2 | `.qcow`, `.qcow2`, `.qcow3` | ✓ |
| VHD / VHDX | `.vhd`, `.vhdx` | ✓ |
| VMDK | `.vmdk` | ✓ |
| AFF1 | `.aff` | ✓ |

Physical images require a readable NTFS volume. Select the first segment of a split image and unlock locked BitLocker volumes first. [Image variant limits](modules/EvidenceImage/README.md)

### Extracted artifacts and existing results

| Input | Support |
| --- | :---: |
| `$J` / `$UsnJrnl:$J` | ✓ |
| `$MFT` | ✓ |
| `$LogFile` | ✓ |
| NTFS Log Tracker `.db`, `.sqlite`, `.sqlite3`, `.csv`, `.tsv` | ✓ |

Use `$MFT` from the same volume with `$J` or `$LogFile` to fill in paths. To open TSV files, choose **All files** in the file picker.

The app opens evidence read-only and writes extractions and indexes separately. New installations use `%LOCALAPPDATA%\NtfsTracer`. Existing JournalDesk settings and indexes can be reused.

<a id="compare-workflows"></a>
<a id="performance"></a>

## Investigation examples and tool comparisons

### Find a previous name from the current filename

You found `report-final.docx` in the collected evidence and want to know what it used to be called. We opened the same USN journal (`$J`) in both tools. NTFS records the two names in separate entries, so the investigation has to connect records for the same file.

| Tool | How we found the previous name |
| --- | --- |
| <img src="docs/media/logo.svg" width="18" height="18" alt=""> **NTFS Tracer** | With **Include previous names** enabled, we searched for `report-final.docx` and chose **Open file history**. The search found five records for the file. Its history showed the old and new names together in one rename event. |
| <img src="docs/media/ntfs-log-tracker-icon.png" width="18" height="18" alt=""> **[NTFS Log Tracker 1.9](https://sites.google.com/site/forensicnote/ntfs-log-tracker)** | We searched **File/Directory Name** for `report-final.docx` and found two records. We copied the **FileReferenceNumber**, the NTFS identifier for the file, cleared the name filter, and searched by that number. This returned five records, including the old name. |

Both tools identified **`report-draft.docx`** as the previous name. The NTFS Tracer screenshot shows the two names and the USN records that support the rename.

[![File history showing the rename from report-draft.docx to report-final.docx and its two USN records](docs/media/file-history-dark.png)](docs/media/file-history-dark.png)

**Time to reach the previous name**

We ran the same workflow on a 92.34 MiB USN journal with 378,975 records extracted from a 22 GiB raw disk image. The figures are medians from six runs per app on the same Windows PC. Both apps received the same extracted journal. For the second lookup, we kept the journal open and searched for a different file.

| Checkpoint | NTFS Tracer 0.8.32 | NTFS Log Tracker 1.9 |
| --- | ---: | ---: |
| First USN list from a fresh analysis | **1.95 s** | 19.57 s |
| First previous name, including analysis | **2.24 s** | 20.01 s |
| Second previous name with the journal open | **216 ms** | 356 ms |

Both tools agreed on the old and new names, USNs, change reasons, and timestamps at the precision shown by Tracker. [Test setup and six runs](docs/performance.md#previous-name-lookup-on-a-378975-record-journal) · [MFTECmd cross-check for the example above](docs/performance.md#cross-checking-the-demo-with-mftecmd)

## Build from source

The desktop app uses C# and WPF and the indexing engine uses Rust. On Windows x64, install the .NET 10 SDK, the Rust MSVC toolchain, and Visual Studio C++ Build Tools. For linting, install `rustfmt` and `clippy`.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

For Windows x86, install the MSVC x86 tools and build the 32-bit engine before publishing:

```powershell
rustup target add i686-pc-windows-msvc
cargo build --release --locked --target i686-pc-windows-msvc --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -r win-x86 -p:PublishProfile=Portable -o artifacts/win-x86
```

For EWF support, build `NtfsTracer.libewf.dll` using the [libewf instructions](modules/EvidenceImage/Logical/Native/README.md) before publishing.

## Display language

Choose from 16 interface languages under **View → Display language**. Changes apply immediately.

English · 한국어 · 日本語 · Deutsch · Español · Français · Bahasa Indonesia · Italiano · Polski · Português (Brasil) · Türkçe · Tiếng Việt · Русский · Українська · 简体中文 · 繁體中文

## License

Project-owned code is licensed under the [Apache License 2.0](LICENSE).

Third-party components retain their own terms and may be used under those terms. See [third-party notices](THIRD-PARTY-NOTICES.md), including the terms and replacement instructions for libewf.
