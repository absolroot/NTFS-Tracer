mod bytes;
mod decode;
mod logcarve;
mod mft;
mod output;
mod transaction_log;
use decode::{Fields, Step};
use rusqlite::{Connection, Statement, params};
use sha2::{Digest, Sha256};
use std::{
    env,
    error::Error,
    fs::{File, OpenOptions},
    io::Read,
    path::Path,
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const BLOCK: usize = 1024 * 1024;

fn flush_damage(
    start: &mut Option<u64>,
    end: u64,
    warnings: &mut Statement,
) -> rusqlite::Result<()> {
    if let Some(pos) = start.take() {
        warnings.execute(params![pos, end - pos, "손상 또는 잘린 데이터"])?;
    }
    Ok(())
}

fn index(source: File, output: &Path) -> Result<()> {
    index_journal(source, output, false)
}

fn carve(source: File, output: &Path) -> Result<()> {
    index_journal(source, output, true)
}

fn index_journal(mut source: File, output: &Path, carving: bool) -> Result<()> {
    let total = source.metadata()?.len();
    let mut db = Connection::open(output)?;
    let tx = db.transaction()?;
    tx.execute_batch(
        "CREATE TABLE records(pos INTEGER PRIMARY KEY, size INTEGER, version INTEGER,
            file BLOB, parent BLOB, usn INTEGER, stamp INTEGER, reason INTEGER,
            source INTEGER, security INTEGER, attr INTEGER, name TEXT,
            minor INTEGER, resynced INTEGER, escaped INTEGER, segment INTEGER);
        CREATE TABLE range_records(pos INTEGER PRIMARY KEY, remaining INTEGER, stride INTEGER);
        CREATE TABLE extents(pos INTEGER, ordinal INTEGER, start INTEGER, length INTEGER,
            PRIMARY KEY(pos,ordinal)) WITHOUT ROWID;
        CREATE TABLE warnings(pos INTEGER, size INTEGER, kind TEXT);
        CREATE TABLE summary(hash TEXT, bytes INTEGER, records INTEGER, skipped INTEGER);",
    )?;
    let mut insert = tx.prepare("INSERT INTO records VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")?;
    let mut range = tx.prepare("INSERT INTO range_records VALUES(?,?,?)")?;
    let mut extent = tx.prepare("INSERT INTO extents VALUES(?,?,?,?)")?;
    let mut warning = tx.prepare("INSERT INTO warnings VALUES(?,?,?)")?;
    let mut hash = Sha256::new();
    let mut buf = vec![0; BLOCK];
    let mut pending = Vec::with_capacity(BLOCK + 65536);
    let mut base = 0u64;
    let mut count = 0u64;
    let mut damage_start = None;
    let mut seeking = false;
    let mut segment = 0u64;
    loop {
        let read = source.read(&mut buf)?;
        hash.update(&buf[..read]);
        pending.extend_from_slice(&buf[..read]);
        let mut i = 0;
        while i < pending.len() {
            let pos = base + i as u64;
            let step = if carving {
                decode::next_carved(&pending[i..], read == 0)
            } else {
                decode::next(&pending[i..], read == 0, seeking)
            };
            if !matches!(step, Step::Damaged(_) | Step::More) {
                flush_damage(&mut damage_start, pos, &mut warning)?;
            }
            i += match step {
                Step::More => break,
                Step::Zero(size) => {
                    if carving {
                        segment += 1;
                    }
                    size
                }
                Step::Unsupported(size, version) => {
                    segment += 1;
                    warning.execute(params![pos, size, format!("지원하지 않는 USN V{version}")])?;
                    size
                }
                Step::Damaged(size) => {
                    if !seeking {
                        segment += 1;
                    }
                    damage_start.get_or_insert(pos);
                    seeking = true;
                    size
                }
                Step::Record(r) => {
                    let (name, stamp, security, attr, escaped) = match &r.fields {
                        Fields::Name(n) => (
                            Some(n.text.as_str()),
                            Some(n.stamp),
                            Some(n.security),
                            Some(n.attr),
                            n.escaped,
                        ),
                        Fields::Range(v) => {
                            range.execute(params![pos, v.remaining, v.stride])?;
                            for (ordinal, (start, length)) in v.entries().enumerate() {
                                extent.execute(params![pos, ordinal, start, length])?;
                            }
                            (None, None, None, None, false)
                        }
                    };
                    insert.execute(params![
                        pos, r.size, r.version, r.file, r.parent, r.usn, stamp, r.reason, r.source,
                        security, attr, name, r.minor, seeking, escaped, segment
                    ])?;
                    seeking = false;
                    count += 1;
                    r.size
                }
            };
        }
        pending.drain(..i);
        base += i as u64;
        if read == 0 {
            break;
        }
        println!("read {base} {total} {count}");
    }
    flush_damage(&mut damage_start, base, &mut warning)?;
    drop((insert, range, extent, warning));
    tx.execute(
        "INSERT INTO summary SELECT ?,?,?,count(*) FROM warnings",
        params![format!("{:x}", hash.finalize()), base, count],
    )?;
    println!("index");
    tx.execute_batch("CREATE INDEX history ON records(file,usn,pos);
        CREATE INDEX dir_names ON records(file,usn,pos) WHERE (attr & 16)!=0;
        CREATE INDEX journal_time ON records(stamp,usn);
        CREATE INDEX journal_usn ON records(usn);
        CREATE TABLE files AS
          WITH counts AS (SELECT file,count(*) AS hits FROM records GROUP BY file)
          SELECT c.file,display.name,display.attr,last.pos,c.hits FROM counts c
          JOIN records last ON last.pos=(SELECT pos FROM records WHERE file=c.file ORDER BY usn DESC,pos DESC LIMIT 1)
          LEFT JOIN records display ON display.pos=CASE WHEN last.name IS NOT NULL THEN last.pos
            ELSE (SELECT pos FROM records WHERE file=c.file AND name IS NOT NULL ORDER BY usn DESC,pos DESC LIMIT 1) END;
        CREATE UNIQUE INDEX file_id ON files(file);
        CREATE INDEX file_order ON files(pos DESC);
        CREATE TABLE aliases AS SELECT DISTINCT file,name FROM records WHERE name IS NOT NULL;
        CREATE INDEX alias_file ON aliases(file);")?;
    let kind = if carving {
        output::Module::CarvedJournal
    } else {
        output::Module::Journal
    };
    kind.write_metadata(&tx)?;
    tx.commit()?;
    let skipped: i64 = db.query_row("SELECT skipped FROM summary", [], |row| row.get(0))?;
    println!("done {count} {skipped}");
    Ok(())
}

fn create(input: &Path, output: &Path, build: fn(File, &Path) -> Result<()>) -> Result<()> {
    let source = File::open(input)?;
    drop(
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(output)?,
    );
    let result = build(source, output);
    if result.is_err() {
        let _ = std::fs::remove_file(output);
    }
    result
}

fn main() {
    let args: Vec<_> = env::args_os().skip(1).collect();
    let result = match args.as_slice() {
        [input, out] => create(Path::new(input), Path::new(out), index),
        [mode, input, out] if mode == "mft" => create(Path::new(input), Path::new(out), mft::index),
        [mode, input, out] if mode == "logfile" => {
            create(Path::new(input), Path::new(out), transaction_log::index)
        }
        [mode, input, out] if mode == "carve" => create(Path::new(input), Path::new(out), carve),
        [mode, input, out] if mode == "logcarve" => {
            create(Path::new(input), Path::new(out), logcarve::index)
        }
        _ => {
            eprintln!("journal-index [mft|logfile|carve|logcarve] INPUT NEW_DB");
            std::process::exit(2);
        }
    };
    if let Err(e) = result {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
