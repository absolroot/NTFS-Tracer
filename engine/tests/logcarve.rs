use rusqlite::Connection;
use sha2::{Digest, Sha256};
use std::{
    fs,
    path::PathBuf,
    process::Command,
    sync::atomic::{AtomicUsize, Ordering},
};

struct Run(PathBuf);
impl Drop for Run {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
impl Run {
    fn new() -> Self {
        static SERIAL: AtomicUsize = AtomicUsize::new(0);
        let path = std::env::temp_dir().join(format!(
            "journaldesk-logcarve-test-{}-{}",
            std::process::id(),
            SERIAL.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
    fn carve(&self, bytes: &[u8]) -> Connection {
        let source = self.0.join("input.dump");
        let dest = self.0.join("result.sqlite");
        fs::write(&source, bytes).unwrap();
        let run = || {
            Command::new(env!("CARGO_BIN_EXE_ntfstracer-cli"))
                .arg("logcarve")
                .arg(&source)
                .arg(&dest)
                .output()
                .unwrap()
        };
        let result = run();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert!(
            !run().status.success(),
            "must reject an existing destination"
        );
        assert_eq!(fs::read(&source).unwrap(), bytes);
        Connection::open(dest).unwrap()
    }
}

fn put16(b: &mut [u8], at: usize, v: u16) {
    b[at..at + 2].copy_from_slice(&v.to_le_bytes());
}
fn put32(b: &mut [u8], at: usize, v: u32) {
    b[at..at + 4].copy_from_slice(&v.to_le_bytes());
}
fn put64(b: &mut [u8], at: usize, v: u64) {
    b[at..at + 8].copy_from_slice(&v.to_le_bytes());
}

fn header(b: &mut [u8], at: usize) {
    put64(b, at, 0x10000 + (at / 8) as u64);
    put32(b, at + 24, 32);
    put32(b, at + 32, 1);
    put32(b, at + 36, 80);
}
fn page() -> Vec<u8> {
    let mut b = vec![0; 4096];
    b[0..4].copy_from_slice(b"RCRD");
    put16(&mut b, 4, 40);
    put16(&mut b, 6, 9);
    put16(&mut b, 20, 1);
    put16(&mut b, 22, 1);
    put16(&mut b, 24, 600);
    header(&mut b, 480); // A protected sector trailer falls inside the client ID.
    header(&mut b, 688); // Remnant beyond the page's declared free-space start.
    header(&mut b, 800);
    put64(&mut b, 800, 0x10111); // LSN low bits do not identify this header.
    header(&mut b, 1000);
    put16(&mut b, 1040, 1); // Multi-page record: do not infer the continuation.
    header(&mut b, 1600);
    put32(&mut b, 1624, u32::MAX);
    put16(&mut b, 40, 0xCAFE);
    for sector in 1..=8 {
        let tail = sector * 512 - 2;
        let original = u16::from_le_bytes([b[tail], b[tail + 1]]);
        put16(&mut b, 40 + 2 * sector, original);
        put16(&mut b, tail, 0xCAFE);
    }
    b
}

#[test]
fn unaligned_boundaries_duplicate_physical_copies_slack_and_torn_pages() {
    let test = Run::new();
    let p = page();
    let first = (1024 * 1024 - 2) as usize; // RCRD itself crosses the source read boundary.
    let mut input = vec![0x47; first];
    input.extend_from_slice(&p);
    input.extend_from_slice(b".");
    let second = input.len();
    input.extend_from_slice(&p);
    let torn = input.len();
    let mut bad = p.clone();
    bad[1022] ^= 1;
    input.extend_from_slice(&bad);
    let short = input.len();
    input.extend_from_slice(&p[..95]);
    let db = test.carve(&input);
    let summary: (String, usize, usize, usize) = db
        .query_row("SELECT * FROM carve_summary", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))
        })
        .unwrap();
    assert_eq!(
        summary,
        (format!("{:x}", Sha256::digest(&input)), input.len(), 4, 0)
    );
    let mut select = db
        .prepare("SELECT pos,status,bad_stride FROM carved_pages ORDER BY pos")
        .unwrap();
    let pages = select
        .query_map([], |r| {
            Ok((
                r.get::<_, usize>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, Option<usize>>(2)?,
            ))
        })
        .unwrap()
        .collect::<Result<Vec<_>, _>>()
        .unwrap();
    assert_eq!(
        pages,
        vec![
            (first, "usa_ok".into(), None),
            (second, "usa_ok".into(), None),
            (torn, "usa_mismatch".into(), Some(1)),
            (short, "truncated".into(), None)
        ]
    );
    let mut select = db
        .prepare("SELECT pos,region,raw_header FROM carved_headers ORDER BY pos")
        .unwrap();
    let rows = select
        .query_map([], |r| {
            Ok((
                r.get::<_, usize>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, Vec<u8>>(2)?,
            ))
        })
        .unwrap()
        .collect::<Result<Vec<_>, _>>()
        .unwrap();
    assert_eq!(rows.len(), 4);
    for (row, (pos, region)) in rows.iter().zip([
        (first + 480, "before_free"),
        (first + 688, "slack"),
        (second + 480, "before_free"),
        (second + 688, "slack"),
    ]) {
        assert_eq!(row.0, pos);
        assert_eq!(row.1, region);
        assert_eq!(row.2, input[pos..pos + 48]);
    }
}

#[test]
fn rejects_signature_noise_and_invalid_geometry_and_zero_usa() {
    let test = Run::new();
    let mut dump = b"RCRD".to_vec();
    dump.extend_from_slice(&[0x99; 60]);
    let mut b = page();
    put16(&mut b, 4, 510);
    dump.extend_from_slice(&b);
    let mut b = page();
    put16(&mut b, 6, 8);
    dump.extend_from_slice(&b);
    let mut b = page();
    put16(&mut b, 24, 127);
    dump.extend_from_slice(&b);
    let mut b = page();
    put16(&mut b, 40, 0);
    dump.extend_from_slice(&b);
    dump.extend_from_slice(b"RCRD");
    let db = test.carve(&dump);
    assert_eq!(
        db.query_row("SELECT rejected FROM carve_summary", [], |r| r
            .get::<_, i64>(0))
            .unwrap(),
        5
    );
    assert_eq!(
        db.query_row("SELECT status FROM carved_pages", [], |r| r
            .get::<_, String>(0))
            .unwrap(),
        "invalid_usa"
    );
    assert_eq!(
        db.query_row("SELECT count(*) FROM carved_headers", [], |r| r
            .get::<_, i64>(0))
            .unwrap(),
        0
    );
}
