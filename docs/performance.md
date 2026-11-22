# Performance measurements

## Repeated file lookups with NTFS Tracer 0.8.29

The workload retrieves all USN records for each of 16 distinct file references. Both methods used the same generated 46,811,392-byte journal with 480,000 V2 records, 20,000 file references, 24 records per file, and zero padding between 4 KiB pages. No MFT was attached. The fixture SHA-256 was `22742433fa2fdc8a5c782067319679332af3b7f39f45006aef8a22fcc58536d4`.

The comparison uses the engine shipped with v0.8.29 and a small Rust harness that calls [Sootmark USN 0.1.0](https://github.com/Sootmark/usn). The streaming method reopens and scans the raw journal for each reference. The indexed method reuses one read-only SQLite connection and runs `SELECT pos,usn,name FROM records WHERE file=? ORDER BY usn,pos` for each reference. We compared every returned source offset, USN, and name in order, 384 records per method per round.

Each of seven rounds created a fresh index. The index time includes launching the engine, hashing the input, and writing the SQLite database. The query timer starts after the database is opened and its record count is verified. Methods ran sequentially in alternating order on the same Windows x64 machine (Ryzen 9 9900X) with the OS file cache left warm. Both lookup methods materialized their results in memory. The results quantify repeated indexed lookups versus repeated streaming scans. They do not measure GUI rendering, MFT path reconstruction, a single full CSV export, or end-to-end speed in MFTECmd, NTFS Log Tracker, or Velociraptor.

| Round | Build SQLite index | 16 Sootmark scans | 16 indexed queries |
| ---: | ---: | ---: | ---: |
| 1 | 2,699.392 ms | 6,766.205 ms | 5.126 ms |
| 2 | 2,587.693 ms | 6,859.883 ms | 3.032 ms |
| 3 | 2,758.153 ms | 6,640.001 ms | 3.125 ms |
| 4 | 2,632.639 ms | 6,583.549 ms | 2.747 ms |
| 5 | 2,644.017 ms | 6,656.664 ms | 3.283 ms |
| 6 | 2,726.219 ms | 6,689.927 ms | 2.825 ms |
| 7 | 2,672.911 ms | 6,591.185 ms | 3.102 ms |
| **Median** | **2,672.911 ms** | **6,656.664 ms** | **3.102 ms** |

### One-pass batch baseline

When all 16 references are already known, the same Sootmark harness can collect matches in a single streaming pass. In a separate seven-run series on the same machine and warm input, it checked each record against a hash map of the target references. All runs returned the same 384 records as SQLite in the same order within each reference. The median was **443.825 ms**. The individual times in milliseconds were 425.649, 435.754, 446.078, 459.570, 448.306, 437.242, and 443.825.

## Historical measurements

### Time to first results

App 0.8.13 and 0.8.14 were each run three times, alternating version order, with a new process and an empty application index on every run. The operating system file cache was warm. The app and engine ran at low priority on one logical processor, with one indexing job at a time. Inputs were an extracted USN journal (41,222,144 bytes), MFT (143,392,768 bytes), and transaction log (30,359,552 bytes).

| Median elapsed time from analysis command | 0.8.13 | 0.8.14 |
| --- | ---: | ---: |
| First journal row laid out | 3.88 s | 1.97 s |
| Selected record visible in Hex | 3.96 s | 2.03 s |
| All three indexes completed | 3.64 s | 3.71 s |

Index completion and visible UI layout are measured independently. The first record's USN, source offset, and byte count agreed across runs. All three SQLite index files had matching SHA-256 values between versions. The Hex selection survived MFT attachment.

### Standalone transaction indexing

The Rust CLI created a fresh SQLite index on each run. The same extracted transaction log yielded 171,581 records.

| Engine version | Runs | Median |
| --- | ---: | ---: |
| 0.4.0 | 3 | 1,168.2 ms |
| 0.4.1 | 3 | 839.3 ms |

Schema, row order, source offsets, move candidates, and prior-cycle residual records agreed. The engine version field changed. These figures describe the stated inputs and historical builds.

### Path sorting with and without a saved order

App 0.8.12 was tested with 333,831 USN records and an MFT from the same volume. The artifact indexes were already available and the OS file cache remained warm. From selecting the path column until the selected row and Hex pane were laid out again, the first sort took 3.63 s. Repeating it in a new app process with the saved sort cache took 186 ms. Each of these GUI timings represents one run. Across eight further column or direction changes, timings ranged from 64 to 154 ms.

Source offsets agreed across the entire sorted dataset. Ascending and descending CSV exports, each with 15,123 filtered rows, matched the previous implementation by SHA-256.
