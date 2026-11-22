JournalDesk vendors sootmark-ntfslog 0.1.0 from crates.io (upstream commit
`b88585646801ee2c1e2acf91a078da6ab57af778`, MIT or Apache-2.0). The upstream
README and both license texts accompany the source.

The local changes in `src/walk.rs` and `src/record.rs` check a circular
page's write pass before accepting a self-addressing candidate, and report
prior-pass headers in the page's free space. The integration regression is
in the parent engine's `tests/transaction_walk.rs`.
