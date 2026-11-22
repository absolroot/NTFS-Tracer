use rusqlite::Connection;
use std::{
    fs,
    path::PathBuf,
    process::{Command, Output},
    sync::atomic::{AtomicUsize, Ordering},
};

struct Fixture {
    dir: PathBuf,
}
impl Fixture {
    fn new() -> Self {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        let dir = std::env::temp_dir().join(format!(
            "journaldesk-mft-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&dir).unwrap();
        Self { dir }
    }
    fn run(&self, data: &[u8]) -> Output {
        let src = self.dir.join("source");
        fs::write(&src, data).unwrap();
        Command::new(env!("CARGO_BIN_EXE_journal-index"))
            .arg("mft")
            .arg(src)
            .arg(self.dir.join("db"))
            .output()
            .unwrap()
    }
    fn parse(&self, data: &[u8]) -> Connection {
        let r = self.run(data);
        assert!(
            r.status.success(),
            "{:?}",
            String::from_utf8_lossy(&r.stderr)
        );
        Connection::open(self.dir.join("db")).unwrap()
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.dir);
    }
}
fn word(buf: &mut [u8], p: usize, n: u16) {
    buf[p..p + 2].copy_from_slice(&n.to_le_bytes());
}
fn dword(buf: &mut [u8], p: usize, n: u32) {
    buf[p..p + 4].copy_from_slice(&n.to_le_bytes());
}
fn qword(buf: &mut [u8], p: usize, n: u64) {
    buf[p..p + 8].copy_from_slice(&n.to_le_bytes());
}
fn name_list(refs: &[(u64, u16)]) -> Vec<u8> {
    let mut value = Vec::new();
    for &(segment, attribute) in refs {
        let mut row = [0; 32];
        dword(&mut row, 0, 0x30);
        word(&mut row, 4, 32);
        qword(&mut row, 16, segment);
        word(&mut row, 24, attribute);
        value.extend_from_slice(&row);
    }
    attr(0x20, 6, &value)
}
fn frn(slot: u64, seq: u64) -> u64 {
    slot | seq << 48
}
fn attr(kind: u32, id: u16, value: &[u8]) -> Vec<u8> {
    let len = (24 + value.len() + 7) & !7;
    let mut b = vec![0; len];
    dword(&mut b, 0, kind);
    dword(&mut b, 4, len as u32);
    word(&mut b, 14, id);
    dword(&mut b, 16, value.len() as u32);
    word(&mut b, 20, 24);
    b[24..24 + value.len()].copy_from_slice(value);
    b
}
fn name(value: &str, parent: u64, ns: u8, id: u16) -> Vec<u8> {
    let words: Vec<_> = value.encode_utf16().collect();
    let mut b = vec![0; 66 + 2 * words.len()];
    qword(&mut b, 0, parent);
    b[64] = words.len() as u8;
    b[65] = ns;
    for (i, w) in words.into_iter().enumerate() {
        word(&mut b, 66 + 2 * i, w);
    }
    attr(0x30, id, &b)
}
fn record(slot: u32, seq: u16, base: u64, size: usize, attrs: &[Vec<u8>], links: u16) -> Vec<u8> {
    let mut b = vec![0; size];
    b[..4].copy_from_slice(b"FILE");
    word(&mut b, 4, 48);
    word(&mut b, 6, (size / 512 + 1) as u16);
    word(&mut b, 16, seq);
    word(&mut b, 18, links);
    let start = (48 + 2 * (size / 512 + 1) + 7) & !7;
    word(&mut b, 20, start as u16);
    word(&mut b, 22, 1);
    dword(&mut b, 28, size as u32);
    qword(&mut b, 32, base);
    dword(&mut b, 44, slot);
    let mut p = start;
    for a in attrs {
        b[p..p + a.len()].copy_from_slice(a);
        p += a.len();
    }
    dword(&mut b, p, u32::MAX);
    dword(&mut b, 24, (p + 8) as u32);
    word(&mut b, 48, 0xabcd);
    for i in 1..=size / 512 {
        b.copy_within(i * 512 - 2..i * 512, 48 + i * 2);
        word(&mut b, i * 512 - 2, 0xabcd);
    }
    b
}
#[test]
fn damaged_first_record_and_partial_tail_do_not_shift_later_slots() {
    let f = Fixture::new();
    let geom = 4096;
    let root = frn(5, 5);
    let mut buf = record(0, 1, 0, geom, &[name("$MFT", root, 3, 1)], 1);
    buf[510] = 0;
    buf.extend(record(1, 3, 0, geom, &[name("$MFTMirr", root, 3, 1)], 1));
    let name_crossing_sector_tail = "가".repeat(240);
    buf.extend(record(
        2,
        9,
        0,
        geom,
        &[
            name(&name_crossing_sector_tail, root, 1, 1),
            name("SHORT~1", root, 2, 2),
        ],
        2,
    ));
    buf.extend_from_slice(b"FILE");
    let db = f.parse(&buf);
    assert_eq!(
        db.query_row(
            "select record_size,records,skipped,bytes from mft_summary",
            [],
            |r| Ok((
                r.get::<_, i64>(0)?,
                r.get::<_, i64>(1)?,
                r.get::<_, i64>(2)?,
                r.get::<_, i64>(3)?
            ))
        )
        .unwrap(),
        (4096, 2, 2, 12292)
    );
    assert_eq!(
        db.query_row(
            "select name,hex(parent) from mft_names where file=? and namespace=1",
            [frn(2, 9).to_le_bytes()],
            |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))
        )
        .unwrap(),
        (name_crossing_sector_tail, "0500000000000500".into())
    );
}
#[test]
fn extension_names_require_matching_owner_generation_and_list_instance() {
    let f = Fixture::new();
    let root = frn(5, 5);
    let owner = frn(1, 3);
    let segment = frn(2, 7);
    let mut data = record(0, 1, 0, 1024, &[name("$MFT", root, 3, 1)], 1);
    data.extend(record(
        1,
        3,
        0,
        1024,
        &[name("원본", root, 1, 4), name_list(&[(segment, 7)])],
        2,
    ));
    data.extend(record(
        2,
        7,
        owner,
        1024,
        &[
            name("링크", frn(31, 1), 1, 7),
            name("옛 이름", frn(31, 1), 1, 8),
        ],
        0,
    ));
    data.extend(record(
        3,
        7,
        frn(1, 2),
        1024,
        &[name("이전 세대", frn(31, 1), 1, 7)],
        0,
    ));
    let db = f.parse(&data);
    assert_eq!(
        db.query_row(
            "select count(*) from mft_names where file=?",
            [owner.to_le_bytes()],
            |r| r.get::<_, i64>(0)
        )
        .unwrap(),
        2
    );
    assert_eq!(
        db.query_row(
            "select count(*) from mft_names where name in ('옛 이름','이전 세대')",
            [],
            |r| r.get::<_, i64>(0)
        )
        .unwrap(),
        0
    );
    assert_eq!(
        db.query_row(
            "select pos,names_complete from mft_entries where id=?",
            [owner.to_le_bytes()],
            |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?))
        )
        .unwrap(),
        (1024, 0)
    );
}
#[test]
fn local_and_extended_names_must_satisfy_references_and_link_counts() {
    let fixture = Fixture::new();
    let root = frn(5, 5);
    let base = frn(1, 1);
    let ext = frn(2, 1);
    let mut bytes = record(0, 1, 0, 1024, &[name("$MFT", root, 3, 1)], 1);
    bytes.extend(record(
        1,
        1,
        0,
        1024,
        &[
            name_list(&[(base, 1), (ext, 4), (ext, 4)]),
            name("base", root, 1, 1),
        ],
        2,
    ));
    bytes.extend(record(
        2,
        1,
        base,
        1024,
        &[name("extension", root, 1, 4)],
        0,
    ));
    bytes.extend(record(
        3,
        1,
        0,
        1024,
        &[
            name_list(&[(frn(3, 1), 9)]),
            name("missing local instance", root, 1, 2),
        ],
        1,
    ));
    bytes.extend(record(
        4,
        1,
        0,
        1024,
        &[name("missing hard link", root, 1, 2)],
        2,
    ));
    bytes.extend(record(
        5,
        1,
        0,
        1024,
        &[
            name_list(&[(frn(6, 1), 4)]),
            name("allocation mismatch", root, 1, 1),
        ],
        1,
    ));
    let mut deleted = record(
        6,
        1,
        frn(5, 1),
        1024,
        &[name("deleted extension", root, 1, 4)],
        0,
    );
    word(&mut deleted, 22, 0);
    bytes.extend(deleted);
    let db = fixture.parse(&bytes);
    let mut query = db
        .prepare("SELECT names_complete FROM mft_entries ORDER BY pos")
        .unwrap();
    let complete: Vec<i64> = query
        .query_map([], |row| row.get(0))
        .unwrap()
        .collect::<rusqlite::Result<_>>()
        .unwrap();
    assert_eq!(complete, [1, 1, 0, 0, 0]);
    assert_eq!(
        db.query_row("SELECT count(*) FROM mft_names", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        6
    );
}
#[test]
fn rejects_fake_header_and_preexisting_output() {
    let f = Fixture::new();
    let mut data = vec![1; 1024];
    data[..4].copy_from_slice(b"FILE");
    dword(&mut data, 28, 1024);
    assert!(!f.run(&data).status.success());
    assert!(!f.dir.join("db").exists());
    let unanchored = record(0, 7, 0, 1024, &[name("일반 파일", frn(12, 2), 1, 4)], 1);
    assert!(!f.run(&unanchored).status.success());
    assert!(!f.dir.join("db").exists());
    fs::write(f.dir.join("db"), "keep").unwrap();
    assert!(!f.run(&unanchored).status.success());
    assert_eq!(fs::read(f.dir.join("db")).unwrap(), b"keep");
}
