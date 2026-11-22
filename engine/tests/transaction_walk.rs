use ntfslog::{Body, Operation};

const PAGE: usize = 4096;
// For the assumed geometry of this 36-page image the LSN has 15 offset bits.
fn lsn(pass: u64, page: usize, within: usize) -> u64 {
    (pass << 15) | ((page * PAGE + within) / 8) as u64
}

fn put16(page: &mut [u8], at: usize, value: u16) {
    page[at..at + 2].copy_from_slice(&value.to_le_bytes());
}
fn put32(page: &mut [u8], at: usize, value: u32) {
    page[at..at + 4].copy_from_slice(&value.to_le_bytes());
}
fn put64(page: &mut [u8], at: usize, value: u64) {
    page[at..at + 8].copy_from_slice(&value.to_le_bytes());
}

fn record(page: &mut [u8], at: usize, lsn: u64, length: u32) {
    put64(page, at, lsn);
    put32(page, at + 24, length);
    put32(page, at + 32, 1);
    put32(page, at + 36, 64);
}

fn protect(page: &mut [u8], newest: u64, free: u16) {
    page[..4].copy_from_slice(b"RCRD");
    put16(page, 4, 40);
    put16(page, 6, 9);
    put64(page, 8, newest);
    put16(page, 24, free);
    put16(page, 40, 0x9876);
    for i in 1..=8 {
        let tail = i * 512 - 2;
        page.copy_within(tail..tail + 2, 40 + i * 2);
        put16(page, tail, 0x9876);
    }
}

#[test]
fn continuation_bytes_cannot_hide_a_record_by_naming_a_future_pass() {
    let mut bytes = vec![0; PAGE * 36];
    let previous = lsn(4, 34, 3952);
    let next = lsn(4, 35, 176);
    let old = lsn(3, 35, 384);
    let first = &mut bytes[34 * PAGE..35 * PAGE];
    record(first, 3952, previous, 208);
    put16(first, 3952 + 40, 1);
    protect(first, previous, 3952);
    let second = &mut bytes[35 * PAGE..];
    // The multi-page payload occupies [64, 176). A coincidental header at
    // 104 would step over the real record at 176. Its low bits fit exactly.
    record(second, 104, lsn(700, 35, 104), 128);
    record(second, 176, next, 40);
    put64(second, 184, previous);
    put16(second, 176 + 48, 11);
    record(second, 384, old, 32);
    protect(second, next, 256);

    let log = ntfslog::parse(&bytes);
    let records = log.records;
    assert_eq!(
        records.iter().map(|r| r.lsn).collect::<Vec<_>>(),
        [old, previous, next]
    );
    assert!(records[0].prior_pass_slack);
    assert!(!records[1].prior_pass_slack && !records[2].prior_pass_slack);
    assert_eq!(records[2].previous_lsn, previous);
    assert!(
        matches!(&records[2].body, Body::Update(u) if u.redo == Operation::SetNewAttributeSizes)
    );
}
