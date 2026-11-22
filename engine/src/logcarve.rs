//! Standalone physical RCRD carving. Unlike the volume parser, this has no
//! trusted restart area, volume origin, circular layout or client history.
use crate::Result;
use crate::bytes::{u16_at, u32_at, u64_at};
use rusqlite::{Connection, Statement, params};
use sha2::{Digest, Sha256};
use std::{fs::File, io::Read, path::Path};

const STRIDE: usize = 512;
const MAX_PAGE: usize = 64 * 1024;
const HEADER: usize = 48;
const CHUNK: usize = 1024 * 1024;

struct Layout {
    size: usize,
    usa: usize,
    data: usize,
    next: usize,
}

impl Layout {
    fn read(b: &[u8]) -> Option<Self> {
        if b.len() < 40 {
            return None;
        }
        let usa = usize::from(u16_at(b, 4));
        let count = usize::from(u16_at(b, 6));
        let size = count.checked_sub(1)? * STRIDE;
        let end = usa + 2 * count;
        let data = end.next_multiple_of(8);
        let pages = u16_at(b, 20);
        let ordinal = u16_at(b, 22);
        let next = usize::from(u16_at(b, 24));
        (size.is_power_of_two()
            && (STRIDE..=MAX_PAGE).contains(&size)
            && usa >= 40
            && usa.is_multiple_of(2)
            && end <= STRIDE - 2
            && pages != 0
            && ordinal != 0
            && ordinal <= pages
            && (next == 0 || (next.is_multiple_of(8) && (data..=size).contains(&next))))
        .then_some(Self {
            size,
            usa,
            data,
            next,
        })
    }
}

fn inspect(b: &[u8], pos: u64, pages: &mut Statement, records: &mut Statement) -> Result<bool> {
    let Some(layout) = Layout::read(b) else {
        return Ok(false);
    };
    let raw = &b[..layout.size.min(b.len())];
    let array = layout.usa;
    let mut bad_stride = None;
    let status = if raw.len() < layout.size {
        "truncated"
    } else if u16_at(raw, array) == 0 || u16_at(raw, array) == u16::MAX {
        "invalid_usa"
    } else {
        bad_stride = (0..layout.size / STRIDE)
            .find(|&i| u16_at(raw, (i + 1) * STRIDE - 2) != u16_at(raw, array));
        if bad_stride.is_some() {
            "usa_mismatch"
        } else {
            "usa_ok"
        }
    };
    pages.execute(params![
        pos,
        layout.size,
        status,
        bad_stride,
        layout.next,
        format!("{:016X}", u64_at(raw, 32)),
        &raw[..layout.data.min(raw.len())]
    ])?;
    if status != "usa_ok" {
        return Ok(true);
    }
    let mut fixed = raw.to_vec();
    for sector in 1..=layout.size / STRIDE {
        let tail = STRIDE * sector - 2;
        fixed[tail..tail + 2].copy_from_slice(&raw[array + 2 * sector..array + 2 * sector + 2]);
    }

    let mut at = layout.data;
    while at + HEADER <= fixed.len() {
        let h = &fixed[at..at + HEADER];
        let lsn = u64_at(h, 0);
        let previous = u64_at(h, 8);
        let undo = u64_at(h, 16);
        let length = u32_at(h, 24) as usize;
        let kind = u32_at(h, 32);
        let flags = u16_at(h, 40);
        let end = at + HEADER + length;
        // Without restart geometry only the page-relative bits of an LSN can be verified.
        let candidate = lsn > (layout.size / 8) as u64
            && (lsn as usize & (layout.size / 8 - 1)) == at / 8
            && previous < lsn
            && undo < lsn
            && u16_at(h, 30) == 0
            && matches!(kind, 1 | 2)
            && length >= if kind == 1 { 32 } else { 64 }
            && flags & 1 == 0
            && end <= layout.size;
        if !candidate {
            at += 8;
            continue;
        }
        let region = match layout.next {
            0 => "unknown",
            n if at >= n => "slack",
            n if end > n => "overlap",
            _ => "before_free",
        };
        records.execute(params![
            pos + at as u64,
            pos,
            at,
            format!("{lsn:016X}"),
            format!("{previous:016X}"),
            format!("{undo:016X}"),
            length,
            kind,
            u32_at(h, 36),
            region,
            &raw[at..at + HEADER]
        ])?;
        at = end.next_multiple_of(8);
    }
    Ok(true)
}

pub fn index(mut source: File, output: &Path) -> Result<()> {
    let total = source.metadata()?.len();
    let mut db = Connection::open(output)?;
    let tx = db.transaction()?;
    tx.execute_batch("
        CREATE TABLE carved_pages(pos INTEGER PRIMARY KEY,size INTEGER,status TEXT,bad_stride INTEGER,
            next_offset INTEGER,last_end_lsn TEXT,raw_header BLOB);
        CREATE TABLE carved_headers(pos INTEGER,page_pos INTEGER,page_offset INTEGER,lsn TEXT,
            previous_lsn TEXT,undo_lsn TEXT,client_bytes INTEGER,kind INTEGER,txid INTEGER,region TEXT,
            raw_header BLOB,PRIMARY KEY(pos,page_pos)) WITHOUT ROWID;
        CREATE TABLE carve_summary(hash TEXT,bytes INTEGER,signatures INTEGER,rejected INTEGER);")?;
    let mut pages = tx.prepare("INSERT INTO carved_pages VALUES(?,?,?,?,?,?,?)")?;
    let mut records = tx.prepare("INSERT INTO carved_headers VALUES(?,?,?,?,?,?,?,?,?,?,?)")?;
    let mut hash = Sha256::new();
    let mut buffer = vec![0; CHUNK];
    let mut pending = Vec::with_capacity(CHUNK + MAX_PAGE);
    let (mut base, mut signatures, mut rejected) = (0u64, 0u64, 0u64);
    loop {
        let n = source.read(&mut buffer)?;
        hash.update(&buffer[..n]);
        pending.extend_from_slice(&buffer[..n]);
        let scan_limit = if n == 0 {
            pending.len()
        } else {
            pending.len().saturating_sub(MAX_PAGE - 1)
        };
        let mut at = 0;
        while let Some(delta) = pending[at..scan_limit].iter().position(|&v| v == b'R') {
            at += delta;
            if pending[at..].starts_with(b"RCRD") {
                signatures += 1;
                if !inspect(&pending[at..], base + at as u64, &mut pages, &mut records)? {
                    rejected += 1;
                }
            }
            at += 1;
        }
        base += scan_limit as u64;
        pending.drain(..scan_limit);
        if n == 0 {
            break;
        }
        println!("read {base} {total}");
    }
    drop((pages, records));
    tx.execute(
        "INSERT INTO carve_summary VALUES (?,?,?,?)",
        params![format!("{:x}", hash.finalize()), base, signatures, rejected],
    )?;
    crate::output::Module::CarvedLog.write_metadata(&tx)?;
    tx.commit()?;
    println!("done {}", signatures - rejected);
    Ok(())
}
