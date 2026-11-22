use crate::Result;
use crate::bytes::{u16_at, u32_at, u64_at, utf16_words};
use rusqlite::{Connection, params};
use sha2::{Digest, Sha256};
use std::{
    fs::File,
    io::{self, Read},
    path::Path,
};

const STRIDE: usize = 512;
const PROBE: usize = 1024 * 1024;
const MAX_RECORD: usize = 65536;
type Parsed<T> = std::result::Result<T, &'static str>;

struct Name {
    parent: u64,
    name: String,
    ns: u8,
    attribute: u16,
    offset: usize,
}
struct Record {
    id: u64,
    base: u64,
    flags: u16,
    links: u16,
    names: Vec<Name>,
    expected: Vec<(u64, u16)>,
    incomplete: bool,
}

fn fixup(b: &mut [u8], slot: u64) -> Parsed<(usize, usize)> {
    if &b[..4] != b"FILE" {
        return Err("FILE 서명 없음");
    }
    if u32_at(b, 28) as usize != b.len() {
        return Err("레코드 크기 불일치");
    }
    let usa = u16_at(b, 4) as usize;
    let count = u16_at(b, 6) as usize;
    let attr = u16_at(b, 20) as usize;
    let used = u32_at(b, 24) as usize;
    let end = usa + 2 * count;
    if usa < 42
        || !usa.is_multiple_of(2)
        || count != 1 + b.len() / STRIDE
        || end > STRIDE - 2
        || end > attr
        || !attr.is_multiple_of(8)
        || used > b.len()
        || attr + 4 > used
    {
        return Err("레코드 헤더 범위 오류");
    }
    // NTFS 3.0 headers do not have the slot number stored at offset 44.
    if usa >= 48 && u32_at(b, 44) != slot as u32 {
        return Err("원본 위치와 엔트리 번호 불일치");
    }
    let tag = u16_at(b, usa);
    for sector in 1..count {
        let tail = sector * STRIDE - 2;
        if u16_at(b, tail) != tag {
            return Err("섹터 끝의 업데이트 시퀀스 불일치");
        }
    }
    for sector in 1..count {
        let tail = sector * STRIDE - 2;
        b.copy_within(usa + sector * 2..usa + sector * 2 + 2, tail);
    }
    Ok((attr, used))
}

fn file_name(value: &[u8], attribute: u16, offset: usize) -> Parsed<Name> {
    if value.len() < 66 {
        return Err("파일명 속성 잘림");
    }
    let count = value[64] as usize;
    let ns = value[65];
    if count == 0 || ns > 3 {
        return Err("파일명 길이 또는 네임스페이스 오류");
    }
    let data = value.get(66..66 + 2 * count).ok_or("파일명 데이터 잘림")?;
    let name = char::decode_utf16(utf16_words(data))
        .collect::<std::result::Result<String, _>>()
        .map_err(|_| "파일명 UTF-16 오류")?;
    if name.contains(['\0', '\\']) {
        return Err("파일명에 잘못된 경로 구분자가 있음");
    }
    Ok(Name {
        parent: u64_at(value, 0),
        name,
        ns,
        attribute,
        offset,
    })
}
fn list(value: &[u8], expected: &mut Vec<(u64, u16)>) -> Parsed<()> {
    let mut pos = 0;
    while pos < value.len() {
        let part = &value[pos..];
        if part.len() >= 4 && u32_at(part, 0) == u32::MAX {
            break;
        }
        if part.len() < 26 {
            return Err("속성 목록 헤더 잘림");
        }
        let size = u16_at(part, 4) as usize;
        if size < 26 || !size.is_multiple_of(8) || size > part.len() {
            return Err("속성 목록 길이 오류");
        }
        if u32_at(part, 0) == 0x30 {
            expected.push((u64_at(part, 16), u16_at(part, 24)));
        }
        pos += size;
    }
    Ok(())
}
fn parse(b: &mut [u8], slot: u64) -> Parsed<Record> {
    let (mut offset, used) = fixup(b, slot)?;
    let seq = u16_at(b, 16) as u64;
    let mut r = Record {
        id: slot | (seq << 48),
        base: u64_at(b, 32),
        flags: u16_at(b, 22),
        links: u16_at(b, 18),
        names: vec![],
        expected: vec![],
        incomplete: false,
    };
    loop {
        let a = &b[offset..used];
        if a.len() < 4 {
            return Err("속성 종료 표지 없음");
        }
        let kind = u32_at(a, 0);
        if kind == u32::MAX {
            return Ok(r);
        }
        if a.len() < 16 {
            return Err("속성 헤더 잘림");
        }
        let len = u32_at(a, 4) as usize;
        let storage = a[8];
        if storage > 1
            || len > a.len()
            || !len.is_multiple_of(8)
            || len < if storage == 0 { 24 } else { 64 }
        {
            return Err("속성 길이 또는 저장 형식 오류");
        }
        if kind == 0x30 || kind == 0x20 {
            if storage == 0 {
                let size = u32_at(a, 16) as usize;
                let start = u16_at(a, 20) as usize;
                if start < 24 || start > len || size > len - start {
                    return Err("상주 속성 데이터 범위 오류");
                }
                let value = &a[start..start + size];
                if kind == 0x30 {
                    let instance = u16_at(a, 14);
                    if r.names.iter().any(|n| n.attribute == instance) {
                        return Err("파일명 속성 인스턴스 번호 중복");
                    }
                    r.names.push(file_name(value, instance, offset)?);
                } else {
                    list(value, &mut r.expected)?;
                }
            } else {
                // The allocated clusters cannot be dereferenced from an extracted $MFT.
                r.incomplete = true;
            }
        }
        offset += len;
    }
}

fn geometry(prefix: &[u8]) -> Option<usize> {
    let mut supported = std::collections::BTreeMap::new();
    for offset in (0..prefix.len()).step_by(STRIDE) {
        let b = &prefix[offset..];
        if b.len() < 48 || &b[..4] != b"FILE" {
            continue;
        }
        let size = u32_at(b, 28) as usize;
        if !(STRIDE..=MAX_RECORD).contains(&size)
            || !size.is_power_of_two()
            || offset % size != 0
            || size > b.len()
        {
            continue;
        }
        let slot = (offset / size) as u64;
        if let Ok(r) = parse(&mut b[..size].to_vec(), slot) {
            if r.base != 0 || r.flags & 1 == 0 || r.names.is_empty() {
                continue;
            }
            let system_name = match slot {
                0 => "$MFT",
                1 => "$MFTMirr",
                2 => "$LogFile",
                3 => "$Volume",
                4 => "$AttrDef",
                5 => ".",
                _ => "",
            };
            if !system_name.is_empty()
                && r.names
                    .iter()
                    .any(|n| n.name == system_name && n.parent & 0x0000_ffff_ffff_ffff == 5)
            {
                return Some(size);
            }
            *supported.entry(size).or_insert(0usize) += 1;
        }
    }
    let mut sizes = supported.into_iter().filter(|(_, n)| *n >= 2);
    let size = sizes.next()?.0;
    sizes.next().is_none().then_some(size)
}

fn read_block(source: &mut File, buffer: &mut [u8]) -> io::Result<usize> {
    let mut filled = 0;
    while filled < buffer.len() {
        match source.read(&mut buffer[filled..]) {
            Ok(0) => break,
            Ok(n) => filled += n,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        }
    }
    Ok(filled)
}

pub fn index(mut source: File, out: &Path) -> Result<()> {
    let total = source.metadata()?.len();
    let mut block = vec![0; PROBE];
    let mut filled = read_block(&mut source, &mut block)?;
    let size = geometry(&block[..filled]).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "$MFT의 첫 1 MiB에서 원본 위치와 형식이 일치하는 FILE 레코드를 찾을 수 없습니다.",
        )
    })?;
    let mut hash = Sha256::new();
    let mut db = Connection::open(out)?;
    let tx = db.transaction()?;
    tx.execute_batch("
      CREATE TABLE mft_entries(id BLOB PRIMARY KEY,in_use INTEGER,directory INTEGER,pos INTEGER,names_complete INTEGER,link_count INTEGER) WITHOUT ROWID;
      CREATE TABLE mft_names(file BLOB,parent BLOB,name TEXT,namespace INTEGER,pos INTEGER,attr_offset INTEGER);
      CREATE TABLE mft_summary(hash TEXT,bytes INTEGER,records INTEGER,skipped INTEGER,record_size INTEGER,extensions INTEGER,empty INTEGER);
      CREATE TABLE mft_warnings(pos INTEGER PRIMARY KEY,reason TEXT);
      CREATE TEMP TABLE supplied(base BLOB,segment BLOB,attribute INTEGER,in_use INTEGER,parent BLOB,name TEXT,namespace INTEGER,pos INTEGER,attr_offset INTEGER);
      CREATE INDEX supplied_extensions ON supplied(segment,attribute) WHERE base IS NOT NULL;
      CREATE TEMP TABLE expected(owner BLOB,segment BLOB,attribute INTEGER,PRIMARY KEY(owner,segment,attribute)) WITHOUT ROWID;")?;
    let mut entry = tx.prepare("INSERT INTO mft_entries VALUES(?,?,?,?,?,?)")?;
    let mut stored = tx.prepare("INSERT INTO supplied VALUES(?,?,?,?,?,?,?,?,?)")?;
    let mut wanted = tx.prepare("INSERT OR IGNORE INTO expected VALUES(?,?,?)")?;
    let mut warn = tx.prepare("INSERT INTO mft_warnings VALUES(?,?)")?;
    let (mut pos, mut records, mut extensions, mut empty) = (0u64, 0u64, 0u64, 0u64);
    while filled > 0 {
        hash.update(&block[..filled]);
        for record in block[..filled].chunks_mut(size) {
            if record.len() != size {
                warn.execute(params![pos, "마지막 레코드 잘림"])?;
            } else if record.iter().all(|&b| b == 0) {
                empty += 1;
            } else {
                match parse(record, pos / size as u64) {
                    Err(reason) => {
                        warn.execute(params![pos, reason])?;
                    }
                    Ok(r) => {
                        let id = r.id.to_le_bytes();
                        let live = r.flags & 1 != 0;
                        if r.base == 0 {
                            let mut complete = !r.incomplete;
                            let mut expects_extension = false;
                            for (segment, attribute) in r.expected {
                                if segment == r.id {
                                    complete &=
                                        r.names.iter().any(|name| name.attribute == attribute);
                                } else {
                                    expects_extension = true;
                                    wanted.execute(params![
                                        id,
                                        segment.to_le_bytes(),
                                        attribute
                                    ])?;
                                }
                            }
                            entry.execute(params![
                                id,
                                live,
                                r.flags & 2 != 0,
                                pos,
                                complete
                                    && (expects_extension || usize::from(r.links) <= r.names.len()),
                                r.links
                            ])?;
                            records += 1;
                        } else {
                            extensions += 1;
                        }
                        for name in r.names {
                            stored.execute(params![
                                (r.base != 0).then(|| r.base.to_le_bytes()),
                                id,
                                name.attribute,
                                live,
                                name.parent.to_le_bytes(),
                                name.name,
                                name.ns,
                                pos,
                                name.offset
                            ])?;
                        }
                    }
                }
            }
            pos += record.len() as u64;
        }
        if filled == PROBE {
            println!("read {pos} {total} {records}");
        }
        filled = read_block(&mut source, &mut block)?;
    }
    drop((entry, stored, wanted, warn));
    if pos != total {
        return Err("분석 중 원본 파일 크기가 변경되었습니다.".into());
    }
    println!("index");
    // An extension's base reference alone is not enough to attach stale names.
    // Require the base record's resident attribute list to point back to the exact
    // extension generation and attribute instance, with matching allocation states.
    tx.execute_batch("INSERT INTO mft_names
        SELECT coalesce(s.base,s.segment),s.parent,s.name,s.namespace,s.pos,s.attr_offset FROM supplied s
        WHERE s.base IS NULL OR EXISTS (
          SELECT 1 FROM expected x JOIN mft_entries e ON e.id=x.owner
          WHERE x.owner=s.base AND x.segment=s.segment AND x.attribute=s.attribute AND e.in_use=s.in_use)
        ORDER BY s.segment,s.attribute;
      UPDATE mft_entries SET names_complete=0 WHERE id IN (
        SELECT x.owner FROM expected x JOIN mft_entries e ON e.id=x.owner WHERE NOT EXISTS (
          SELECT 1 FROM supplied s WHERE s.base=x.owner AND s.segment=x.segment AND s.attribute=x.attribute AND s.in_use=e.in_use)
        UNION SELECT s.base FROM supplied s WHERE s.base IS NOT NULL AND s.base!=s.segment AND NOT EXISTS (
          SELECT 1 FROM expected x WHERE x.owner=s.base AND x.segment=s.segment AND x.attribute=s.attribute));
      CREATE INDEX mft_name_lookup ON mft_names(file);
      UPDATE mft_entries SET names_complete=0 WHERE names_complete=1 AND id IN (SELECT owner FROM expected) AND link_count>(
          SELECT count(*) FROM mft_names n WHERE n.file=mft_entries.id);")?;
    let skipped: i64 = tx.query_row("SELECT count(*) FROM mft_warnings", [], |r| r.get(0))?;
    tx.execute(
        "INSERT INTO mft_summary VALUES(?,?,?,?,?,?,?)",
        params![
            format!("{:x}", hash.finalize()),
            pos,
            records,
            skipped,
            size,
            extensions,
            empty
        ],
    )?;
    crate::output::Module::Mft.write_metadata(&tx)?;
    tx.commit()?;
    println!("done {records} {skipped}");
    Ok(())
}
