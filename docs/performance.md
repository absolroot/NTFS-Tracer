# Performance measurements

## Time to first results

App 0.8.13 and 0.8.14 were each run three times, alternating version order, with a new process and an empty application index on every run. The operating system file cache was warm. The app and engine ran at low priority on one logical processor, with one indexing job at a time. Inputs were an extracted USN journal (41,222,144 bytes), MFT (143,392,768 bytes), and transaction log (30,359,552 bytes).

| Median elapsed time from analysis command | 0.8.13 | 0.8.14 |
| --- | ---: | ---: |
| First journal row laid out | 3.88 s | 1.97 s |
| Selected record visible in Hex | 3.96 s | 2.03 s |
| All three indexes completed | 3.64 s | 3.71 s |

Index completion and visible UI layout are measured independently. The first record's USN, source offset, and byte count agreed across runs. All three SQLite index files had matching SHA-256 values between versions. The Hex selection survived MFT attachment.

## Standalone transaction indexing

The Rust CLI created a fresh SQLite index on each run. The same extracted transaction log yielded 171,581 records.

| Engine version | Runs | Median |
| --- | ---: | ---: |
| 0.4.0 | 3 | 1,168.2 ms |
| 0.4.1 | 3 | 839.3 ms |

Schema, row order, source offsets, move candidates, and prior-cycle residual records agreed. The engine version field changed. These figures describe the stated inputs and historical builds.

## Path sorting with and without a saved order

App 0.8.12 was tested with 333,831 USN records and an MFT from the same volume. The artifact indexes were already available and the OS file cache remained warm. From selecting the path column until the selected row and Hex pane were laid out again, the first sort took 3.63 s. Repeating it in a new app process with the saved sort cache took 186 ms. Each of these GUI timings represents one run. Across eight further column or direction changes, timings ranged from 64 to 154 ms.

Source offsets agreed across the entire sorted dataset. Ascending and descending CSV exports, each with 15,123 filtered rows, matched the previous implementation by SHA-256.
