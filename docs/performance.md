# Performance measurements

## Finding a previous name from the current name

This test starts with the current filename only. On the Windows machine described below, using the same small synthetic journal (6.1 MB, 63,007 records), we ran the development build of NTFS Tracer and NTFS Log Tracker 1.9 ten times each. Each pair used fresh databases and the same input. We shuffled the order in advance, with each app first in five pairs. The OS file cache was warm.

- **NTFS Tracer** searched for the full current name with **Include previous names** enabled, selected the new-name record, and opened **File history**. Timing continued until the rename was shown with both supporting records.
- **NTFS Log Tracker** searched **File/Directory Name** for the full current name. The harness read the displayed **FileReferenceNumber**, cleared the filename filter, and searched by that number. Timing continued until the search results showed the old and new names. This includes dismissing Tracker’s completion messages.

The first trial target in each pair was `report-final.docx`. The second was `budget-final.docx`, with the journal still open. The second search timing starts at the end of the prior task. Tracer returns to **All records** before searching again. The automated checks read the resulting rows. File pickers, human typing time, and the time to interpret the results were outside the measurement.

All 63,007 USN/name pairs matched in the databases in all ten pairs. The two target searches also agreed on names, USNs, change reasons, and timestamps. This test measures the steps to find the earlier name. The shared-prefix test below measures a single search where entering `report-` can already find both names.

| Pair | Run first | Tracer first USN list | Tracker first USN list | Tracer first previous name | Tracker first previous name | Tracer second previous name | Tracker second previous name |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Tracer | 1,112.66 | 3,047.80 | 1,349.38 | 3,278.12 | 141.64 | 144.62 |
| 2 | Tracer | 980.95 | 3,048.23 | 1,165.21 | 3,214.00 | 185.85 | 144.17 |
| 3 | Tracker | 1,079.50 | 4,569.79 | 1,323.64 | 4,777.63 | 192.83 | 151.42 |
| 4 | Tracer | 1,072.75 | 3,234.23 | 1,289.70 | 3,464.52 | 114.75 | 148.00 |
| 5 | Tracer | 888.57 | 2,992.84 | 1,046.02 | 3,240.97 | 127.71 | 258.23 |
| 6 | Tracker | 939.85 | 3,265.04 | 1,152.57 | 3,574.11 | 121.38 | 192.15 |
| 7 | Tracker | 1,079.09 | 3,367.90 | 1,301.86 | 3,523.23 | 108.11 | 156.57 |
| 8 | Tracer | 1,104.23 | 3,615.90 | 1,327.70 | 3,816.93 | 179.71 | 156.04 |
| 9 | Tracker | 1,082.63 | 3,192.83 | 1,338.98 | 3,486.89 | 126.13 | 171.64 |
| 10 | Tracker | 1,066.56 | 3,262.02 | 1,268.29 | 3,515.77 | 126.63 | 152.20 |
| **Median (ms)** | | **1,075.92** | **3,248.12** | **1,295.78** | **3,501.33** | **127.17** | **154.12** |

For the second target, Tracer finished first in seven pairs. The median paired difference (Tracker minus Tracer) was 29.41 ms, with a sign-based interval from -41.41 to 70.76 ms using the same 97.85% method as below. The measurements leave the direction of the typical difference uncertain for this second task.

## NTFS Log Tracker comparison

We used a 6,144,768-byte synthetic `$J` with **63,007 USN V2 records** on one Windows 11 x64 machine (Ryzen 9 9900X, Windows build 26200). NTFS Tracer was a v0.8.29 source build. The comparison program identified itself as [NTFS Log Tracker v1.9](https://sites.google.com/site/forensicnote/ntfs-log-tracker) in its window title. The input SHA-256 was `bc4ef1c77fbb6f5ae61311d90809589c0812821e8ccc226de60a3997a9c0bda1`. Tracker’s executable SHA-256 was `80178b865a31351aa4fc1e4ab75edc23b415fdf96921862e2277e5313d4e2bae`.

Both programs ran on the same hidden Windows desktop in fresh processes, with separate, empty output locations for each run. The OS file cache was warm. Each pair ran the two programs sequentially. We shuffled five pairs in each program order using a fixed seed before measurement. Pilot runs were excluded. The input was `$J` alone, so the results concern USN journal searches. MFT path reconstruction, transaction analysis, carving, and optional Tracker detection were outside the workload.

1. **New result database to initial display.** Start timing with the final analysis command after input and output settings have been supplied. Record an intermediate checkpoint when analysis finishes in the app. Tracker reports its completed USN count and re-enables the main window. In Tracer, the journal-open operation has returned and the new index is attached. This checkpoint includes app setup after database creation. Continue until the USN list is active and results are available. Tracker populates all 63,007 rows in this fixture, while Tracer initially displays a page of 200.
2. **First rename search.** From the same starting point, continue until a search for `report-` displays four rows for the first file, including its previous and new names.
3. **Next rename search.** With the source still open, start a new search for `budget-` and stop when the four matching rows are available. The scripted interactions include Tracker’s Search dialog and dismissal of its completion notice, and NTFS Tracer’s normal search-as-you-type delay. File selection dialogs, human typing and reasoning time are outside the timed interval. The Tracer harness checked WPF layout. The Tracker harness checked the visible Win32 list state. These checkpoints measure when each app has results available, without timing physical screen refreshes.

All ten pairs returned the same 63,007 USN/name pairs in their databases. For both searches, the visible results agreed on each row’s name, USN, normalized change reason, and timestamp in Korea Standard Time.

Individual times are in milliseconds.

| Pair | Run first | Tracer analysis complete | Tracker analysis complete | Tracer first list | Tracker first list | Tracer first search | Tracker first search | Tracer next search | Tracker next search |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Tracer | 820.56 | 2,907.64 | 979.43 | 2,973.89 | 1,285.90 | 3,058.20 | 290.34 | 58.42 |
| 2 | Tracer | 864.01 | 2,814.49 | 1,017.30 | 2,877.06 | 1,348.47 | 2,983.34 | 277.96 | 76.43 |
| 3 | Tracker | 771.01 | 2,931.08 | 898.02 | 2,952.86 | 1,212.21 | 3,076.73 | 277.35 | 69.00 |
| 4 | Tracer | 854.01 | 2,783.14 | 1,024.58 | 2,850.38 | 1,334.27 | 2,930.68 | 280.45 | 69.15 |
| 5 | Tracer | 829.30 | 2,685.26 | 981.13 | 2,744.91 | 1,310.33 | 2,830.08 | 262.32 | 57.76 |
| 6 | Tracker | 931.68 | 2,683.65 | 1,078.96 | 2,743.07 | 1,376.19 | 2,833.60 | 287.25 | 68.22 |
| 7 | Tracker | 759.45 | 2,793.86 | 890.82 | 2,854.34 | 1,226.45 | 2,935.63 | 276.96 | 59.37 |
| 8 | Tracer | 860.19 | 3,222.59 | 1,026.69 | 3,224.96 | 1,337.74 | 3,393.48 | 270.18 | 74.98 |
| 9 | Tracker | 835.30 | 3,098.57 | 1,001.54 | 3,135.76 | 1,306.15 | 3,315.49 | 270.94 | 100.13 |
| 10 | Tracker | 758.25 | 2,730.46 | 915.09 | 2,793.29 | 1,206.17 | 2,875.83 | 277.26 | 69.37 |
| **Median (ms)** | | **832.30** | **2,804.17** | **991.33** | **2,865.70** | **1,308.24** | **2,959.49** | **277.31** | **69.08** |

| Task | Faster in all 10 pairs | Median paired difference | Exact sign-based interval for the paired median |
| --- | --- | ---: | ---: |
| Analysis complete | Tracer | 2,003.31 ms | 1,855.96 to 2,263.27 ms |
| Initial display including database creation | Tracer | 1,920.87 ms | 1,763.78 to 2,134.22 ms |
| First rename search | Tracer | 1,689.42 ms | 1,519.74 to 2,009.34 ms |
| Next rename search | Tracker | 208.12 ms | 195.21 to 219.03 ms |

Paired differences are calculated within each pair and expressed in the faster program’s favor. The interval uses the second- and ninth-smallest signed differences. With ten independent pairs from a continuous distribution it has 97.85% coverage. These repetitions characterize the variability on one machine with one fixture. Workload generalization requires additional inputs and machines. [NIST sign-test reference](https://www.itl.nist.gov/div898/software/dataplot/refman1/auxillar/signtest.htm)

## Filename search scheduling

In the original v0.8.29 build, every filename edit restarted a 240 ms WPF timer before the background query could begin. For typical filename searches, the development build queues the latest edit at background dispatcher priority and starts it without a fixed delay. Filename edits with a path filter or a whole-result sort such as path or reason continue to use the timer. Date and other filter edits also use the timer. During IME composition the app waits for the committed text. The database query already cancels superseded requests.

We followed the event-driven scheduling approach in [fzf's change-triggered search examples](https://github.com/junegunn/fzf/blob/master/ADVANCED.md) and used the [WPF DispatcherTimer](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer?view=windowsdesktop-10.0) to coalesce edits waiting in the dispatcher queue. The alternatives timed were the existing fixed delay and the queued search with no fixed delay. Both builds used the same Rust executable, journal, and SQLite query logic. [WPF dispatcher priorities](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority?view=windowsdesktop-10.0)

The measurement starts when the second search begins and ends when all four matching records are available in the visible list. Each block used the same 63,007-record input and ran the original build, updated build, and Tracker sequentially in a pre-shuffled order on the machine described above. The before and after builds returned identical results in both searches, and the shared result fields agreed with Tracker.

| Block | v0.8.29 | Development build | NTFS Log Tracker 1.9 |
| ---: | ---: | ---: | ---: |
| 1 | 281.97 ms | 32.66 ms | 72.63 ms |
| 2 | 288.86 ms | 33.04 ms | 73.34 ms |
| 3 | 274.37 ms | 31.34 ms | 72.88 ms |
| 4 | 279.21 ms | 36.75 ms | 73.70 ms |
| 5 | 276.55 ms | 33.24 ms | 74.78 ms |
| 6 | 269.82 ms | 29.39 ms | 75.06 ms |
| 7 | 272.92 ms | 35.60 ms | 76.11 ms |
| 8 | 286.93 ms | 33.54 ms | 95.67 ms |
| 9 | 282.80 ms | 29.98 ms | 87.81 ms |
| 10 | 274.79 ms | 35.00 ms | 81.58 ms |
| **Median** | **277.88 ms** | **33.14 ms** | **74.92 ms** |

The median within-block improvement was **243.17 ms**. The second and ninth ordered differences were 239.79 and 253.40 ms. The development build returned the result before Tracker in 10 of 10 blocks. A separate WPF check supplied 30 rapid edits and verified the final rows. A simulated composition event delayed search until the commit event.

## File history in the desktop app

The v0.8.29 WPF build opened the same 480,000-record synthetic USN journal described below. Six runs used separate application processes and fresh settings and index directories on a Ryzen 9 9900X under Windows x64. The OS file cache was warm. No MFT was attached. The app was already running when timing began.

An offscreen harness opened the journal, waited for the first results to lay out, entered a complete file name, and opened the file history through the selected result. It then returned to the search, cleared the earlier filter, searched for a second file, and opened that file’s history. Both searches returned 24 journal rows for their respective filenames. The harness waited for row containers and WPF layout at each display checkpoint. The measurements exclude human typing and review time.

| Run | Open to first results | Open to first file history | Clear search to next file history |
| ---: | ---: | ---: | ---: |
| 1 | 3,282.94 ms | 3,792.91 ms | 673.23 ms |
| 2 | 2,939.40 ms | 3,417.37 ms | 720.58 ms |
| 3 | 2,926.53 ms | 3,406.37 ms | 699.33 ms |
| 4 | 2,850.51 ms | 3,343.50 ms | 717.42 ms |
| 5 | 2,964.77 ms | 3,479.06 ms | 689.49 ms |
| 6 | 3,053.91 ms | 3,529.86 ms | 721.01 ms |
| **Median** | **2,952.09 ms** | **3,448.22 ms** | **708.38 ms** |

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
