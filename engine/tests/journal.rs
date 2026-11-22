use rusqlite::Connection;
use std::{
    fs,
    path::PathBuf,
    process::Command,
    sync::atomic::{AtomicUsize, Ordering},
};

struct Fixture {
    root: PathBuf,
}
impl Fixture {
    fn new() -> Self {
        static SERIAL: AtomicUsize = AtomicUsize::new(0);
        let root = std::env::temp_dir().join(format!(
            "journaldesk-test-{}-{}",
            std::process::id(),
            SERIAL.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&root).unwrap();
        Self { root }
    }
    fn run(&self, bytes: &[u8]) -> Connection {
        let input = self.root.join("input");
        let output = self.root.join("index");
        fs::write(&input, bytes).unwrap();
        assert!(
            Command::new(env!("CARGO_BIN_EXE_journal-index"))
                .arg(&input)
                .arg(&output)
                .output()
                .unwrap()
                .status
                .success()
        );
        Connection::open(output).unwrap()
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

fn record(version: u16, words: &[u16]) -> Vec<u8> {
    let t = if version == 2 { 24 } else { 40 };
    let header = t + 36;
    let size = (header + words.len() * 2 + 7) & !7;
    let mut b = vec![0; size];
    b[..4].copy_from_slice(&(size as u32).to_le_bytes());
    b[4..6].copy_from_slice(&version.to_le_bytes());
    b[8] = 31;
    if version == 3 {
        b[23] = 0xab;
    }
    b[t..t + 8].copy_from_slice(&400i64.to_le_bytes());
    b[t + 32..t + 34].copy_from_slice(&((words.len() * 2) as u16).to_le_bytes());
    b[t + 34..header].copy_from_slice(&(header as u16).to_le_bytes());
    let (name_words, _) = b[header..].as_chunks_mut::<2>();
    for (chunk, word) in name_words.iter_mut().zip(words) {
        *chunk = word.to_le_bytes();
    }
    b
}

#[test]
fn carving_preserves_unaligned_dump_offsets_and_rejects_nameless_noise() {
    let fixture = Fixture::new();
    let position = 1024 * 1024 - 3;
    let mut data = vec![0x81; position];
    let mut candidate = record(2, &[b'A' as u16]);
    candidate[16] = 5;
    candidate[32..40].copy_from_slice(&130000000000000000i64.to_le_bytes());
    candidate[40] = 1;
    data.extend_from_slice(&candidate);
    data.extend_from_slice(&[0; 13]);
    candidate[40] = 0;
    data.extend(candidate);
    let input = fixture.root.join("dump");
    let output = fixture.root.join("carved");
    fs::write(&input, data).unwrap();
    assert!(
        Command::new(env!("CARGO_BIN_EXE_journal-index"))
            .arg("carve")
            .arg(&input)
            .arg(&output)
            .output()
            .unwrap()
            .status
            .success()
    );
    let db = Connection::open(output).unwrap();
    let result: (i64, i64) = db
        .query_row("select count(*),min(pos) from records", [], |r| {
            Ok((r.get(0)?, r.get(1)?))
        })
        .unwrap();
    assert_eq!(result, (1, position as i64));
}

#[test]
fn aligned_recovery_and_unpaired_utf16_survive_a_read_boundary() {
    let fixture = Fixture::new();
    let mut data = vec![0; 1024 * 1024 - 16];
    data.extend_from_slice(&[0xff; 8]);
    let pos = data.len() as i64;
    data.extend(record(3, &[0xd800, 0x0041]));
    data.extend([1, 2, 3]);
    let db = fixture.run(&data);
    let row = db
        .query_row(
            "select pos,name,length(file),hex(file),escaped,resynced,segment from records",
            [],
            |r| {
                Ok((
                    r.get::<_, i64>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, i64>(2)?,
                    r.get::<_, String>(3)?,
                    r.get::<_, bool>(4)?,
                    r.get::<_, bool>(5)?,
                    r.get::<_, i64>(6)?,
                ))
            },
        )
        .unwrap();
    assert_eq!(
        row,
        (
            pos,
            r"\u{D800}A".to_owned(),
            16,
            "1F0000000000000000000000000000AB".to_owned(),
            true,
            true,
            1
        )
    );
    let spans = db
        .prepare("select pos,size from warnings order by pos")
        .unwrap()
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?)))
        .unwrap()
        .collect::<Result<Vec<_>, _>>()
        .unwrap();
    assert_eq!(spans, [(pos - 8, 8), (data.len() as i64 - 3, 3)]);
}

fn v4() -> Vec<u8> {
    let mut b = vec![0; 112];
    b[..4].copy_from_slice(&112u32.to_le_bytes());
    b[4] = 4;
    b[8] = 31;
    b[23] = 0xab;
    b[40..48].copy_from_slice(&500i64.to_le_bytes());
    b[48] = 1;
    b[56] = 1;
    b[60] = 2;
    b[62] = 24;
    b[64..72].copy_from_slice(&1024i64.to_le_bytes());
    b[72..80].copy_from_slice(&128i64.to_le_bytes());
    b[88..96].copy_from_slice(&4096i64.to_le_bytes());
    b[96..104].copy_from_slice(&64i64.to_le_bytes());
    b
}
#[test]
fn v4_uses_extent_stride_and_does_not_invent_a_time_or_filename() {
    let fixture = Fixture::new();
    let mut data = record(3, &[0x0061]);
    data.extend(v4());
    let db = fixture.run(&data);
    let counts: (i64, i64, i64, i64) = db
        .query_row(
            "select count(*),count(stamp),count(name),count(attr) from records",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .unwrap();
    assert_eq!(counts, (2, 1, 1, 1));
    assert_eq!(
        db.query_row("select name,hits from files", [], |r| Ok((
            r.get::<_, String>(0)?,
            r.get::<_, i64>(1)?
        )))
        .unwrap(),
        ("a".to_owned(), 2)
    );
    assert_eq!(
        db.query_row("select stride,remaining from range_records", [], |r| Ok((
            r.get::<_, i64>(0)?,
            r.get::<_, i64>(1)?
        )))
        .unwrap(),
        (24, 1)
    );
    let extents = db
        .prepare("select start,length from extents order by ordinal")
        .unwrap()
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?)))
        .unwrap()
        .collect::<Result<Vec<_>, _>>()
        .unwrap();
    assert_eq!(extents, [(1024, 128), (4096, 64)]);
}
#[test]
fn malformed_extent_count_is_not_accepted_and_existing_output_is_preserved() {
    let fixture = Fixture::new();
    let mut b = v4();
    b[60] = 250;
    b.extend(record(2, &[0x0062]));
    let db = fixture.run(&b);
    assert_eq!(
        db.query_row("select records from summary", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        1
    );
    assert_eq!(
        db.query_row("select pos from records", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        112
    );
    let dest = fixture.root.join("index");
    let before = fs::read(&dest).unwrap();
    assert!(
        !Command::new(env!("CARGO_BIN_EXE_journal-index"))
            .arg(fixture.root.join("input"))
            .arg(&dest)
            .output()
            .unwrap()
            .status
            .success()
    );
    assert_eq!(before, fs::read(dest).unwrap());
}
