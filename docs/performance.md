# Performance measurements

## Previous-name lookup on a 378,975-record journal

We used a 96,828,480-byte (92.34 MiB) USN journal extracted from the first readable NTFS volume of a 22 GiB raw disk image. The test machine ran Windows 11 x64 (build 26200) on a Ryzen 9 9900X with 64 GiB of RAM and NVMe storage.

NTFS Tracer 0.8.32 and NTFS Log Tracker 1.9 received the same extracted $J. Each run used a fresh process and database on the same hidden Windows desktop. The OS file cache was warm. The programs ran sequentially in a predetermined order, with each tool running ahead of the other in three blocks. Disk-image extraction, file selection, human typing, and time spent reading the results were outside the measurement.

1. Start timing when analysis starts. Tracker makes all 378,975 rows available in its first list. Tracer starts with a page of 200 rows.
2. Search for the first file using its current name. In Tracer, enable **Include previous names**, select the new-name record, and open **File history**. In Tracker, find the file reference in the current-name search result and search again by that reference. Stop when both name records are available.
3. Keep the journal open and look up the previous name of a different file. Tracer returns to **All records** before searching again. Stop when both name records are available.

| Checkpoint | NTFS Tracer 0.8.32 median | NTFS Log Tracker 1.9 median |
| --- | ---: | ---: |
| First USN list from a fresh analysis | 1,954.3 ms | 19,566.2 ms |
| First previous name, including analysis | 2,241.7 ms | 20,006.3 ms |
| First lookup after the list appeared | 298.0 ms | 392.6 ms |
| Second previous name with the journal open | 216.1 ms | 356.2 ms |

NTFS Tracer finished the second lookup first in all six blocks. The median paired lead was 140.6 ms.

| Block | Tool first | Tracer first list | Tracker first list | Tracer first previous name | Tracker first previous name | Tracer second previous name | Tracker second previous name |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Tracer | 1,909.8 ms | 19,701.8 ms | 2,215.6 ms | 20,188.5 ms | 213.5 ms | 372.7 ms |
| 2 | Tracer | 1,945.2 ms | 20,028.0 ms | 2,235.3 ms | 20,414.0 ms | 218.7 ms | 314.2 ms |
| 3 | Tracker | 1,898.3 ms | 19,430.7 ms | 2,167.6 ms | 19,824.2 ms | 227.5 ms | 367.1 ms |
| 4 | Tracker | 1,963.3 ms | 18,530.7 ms | 2,248.1 ms | 18,892.6 ms | 188.4 ms | 365.9 ms |
| 5 | Tracker | 2,038.4 ms | 18,404.3 ms | 2,375.0 ms | 18,797.1 ms | 229.4 ms | 336.3 ms |
| 6 | Tracer | 2,005.3 ms | 19,804.5 ms | 2,333.1 ms | 20,291.4 ms | 204.9 ms | 346.6 ms |

### Evidence checks

The two tools returned the same set of USNs. For both rename pairs, the old and new names, USNs, change reasons, and timestamps agreed at Tracker's display precision. Across the full journal, 283 names differed at matching USNs. Reading those records directly confirmed that Tracer preserved the original UTF-16 names, including commas that Tracker displayed as slashes.

A separate UI check verified partial text selection in the overview, both Hex ranges against the journal bytes, and restoration of the selected row and search text when returning to **All records**.

### File history update

A UI-thread stack sample showed WPF waiting on a Windows Text Services Framework callback while changing a read-only overview value. Setting WPF's input-method flag to false left the delay in place. Tracer now sets each changed value before attaching the selectable text field to the view, reuses the field when it has keyboard focus, and selects the requested source record when opening file history. [WPF TextStore implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/TextStore.cs) · [Windows text selection notification](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacpsink-onselectionchange)

## Cross-checking the demo with MFTECmd

The README screenshot uses the smaller demo journal, which contains 391 records. We exported that journal with MFTECmd 2026.11.1, a separate command-line NTFS parser, and checked the five records for file reference `42-1`. The filenames, USNs, and source offsets matched. The old-name record contains `report-draft.docx` at USN 38648, and the new-name record contains `report-final.docx` at USN 38744. [MFTECmd](https://github.com/EricZimmerman/MFTECmd)

```powershell
.\MFTECmd.exe -f '.\$J' --csv .\out --csvf rename.csv --co false
```
