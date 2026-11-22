use crate::Result;
use ntfslog::{Body, Operation, StandardTimes};
use rusqlite::{Connection, params};
use sha2::{Digest, Sha256};
use std::{fs::File, io::Read, path::Path};

fn times(value: Option<&StandardTimes>) -> [Option<i64>; 4] {
    value.map_or([None; 4], |t| {
        [t.created, t.modified, t.changed, t.accessed].map(|stamp| stamp.and_then(|s| s.ticks()))
    })
}

struct ChainParent {
    lsn: u64,
    transaction: u32,
    root: usize,
}

fn chain_root(
    parents: &mut Vec<ChainParent>,
    id: usize,
    lsn: u64,
    previous: u64,
    transaction: u32,
    redo: Option<Operation>,
) -> usize {
    if transaction == 0 {
        return id;
    }
    // The parser returns distinct LSNs in increasing order, including retained prior-pass records.
    let root = parents
        .binary_search_by_key(&previous, |p| p.lsn)
        .ok()
        .map(|i| &parents[i])
        .filter(|p| p.transaction == transaction)
        .map_or(id, |p| p.root);
    if !matches!(
        redo,
        Some(Operation::ForgetTransaction | Operation::CommitTransaction)
    ) {
        parents.push(ChainParent {
            lsn,
            transaction,
            root,
        });
    }
    root
}

pub fn index(mut source: File, output: &Path) -> Result<()> {
    let mut bytes = Vec::new();
    source.read_to_end(&mut bytes)?;
    let hash = format!("{:x}", Sha256::digest(&bytes));
    let log = ntfslog::parse(&bytes);
    if log.restart_pages.is_empty() && log.records.is_empty() {
        return Err("NTFS 트랜잭션 로그의 헤더 또는 레코드를 찾을 수 없습니다.".into());
    }
    let mut db = Connection::open(output)?;
    let tx = db.transaction()?;
    tx.execute_batch("
        CREATE TABLE tx_records(id INTEGER PRIMARY KEY,lsn TEXT UNIQUE,prev TEXT,undo_next TEXT,
          pos INTEGER,txid INTEGER,redo TEXT,undo TEXT,redo_len INTEGER,undo_len INTEGER,
          redodata BLOB,undodata BLOB,lcns TEXT,mft_slot INTEGER);
        CREATE TABLE tx_provenance(id INTEGER PRIMARY KEY,kind TEXT NOT NULL);
        CREATE TABLE tx_names(id INTEGER,side TEXT,name TEXT,file BLOB,parent BLOB,short_name INTEGER);
        CREATE TABLE tx_times(id INTEGER,side TEXT,created INTEGER,modified INTEGER,changed INTEGER,accessed INTEGER);
        CREATE TABLE tx_time_locations(id INTEGER, field INTEGER, before_bytes BLOB, after_bytes BLOB, PRIMARY KEY(id,field)) WITHOUT ROWID;
        CREATE TABLE tx_warnings(pos INTEGER,reason TEXT);
        CREATE TABLE tx_summary(hash TEXT,bytes INTEGER,records INTEGER,warnings INTEGER);
        CREATE TEMP TABLE named_chains(id INTEGER PRIMARY KEY,root INTEGER NOT NULL);
        CREATE TEMP TABLE excluded_roots(root INTEGER PRIMARY KEY);")?;
    let mut insert = tx.prepare("INSERT INTO tx_records VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)")?;
    let mut name = tx.prepare("INSERT INTO tx_names VALUES(?,?,?,?,?,?)")?;
    let mut provenance = tx.prepare("INSERT INTO tx_provenance VALUES(?,'prior_pass_slack')")?;
    let mut stamp = tx.prepare("INSERT INTO tx_times VALUES(?,?,?,?,?,?)")?;
    let mut location = tx.prepare("INSERT INTO tx_time_locations VALUES(?,?,?,?)")?;
    let mut chain = tx.prepare("INSERT INTO named_chains VALUES(?,?)")?;
    let mut excluded = tx.prepare("INSERT OR IGNORE INTO excluded_roots VALUES(?)")?;
    let mut parents = Vec::new();
    for (index, record) in log.records.iter().enumerate() {
        let id = i64::try_from(index + 1)?;
        if record.prior_pass_slack {
            provenance.execute([id])?;
        }
        let update = record.update();
        let root = chain_root(
            &mut parents,
            index + 1,
            record.lsn,
            record.previous_lsn,
            record.transaction_id,
            update.map(|u| u.redo),
        );
        if record.prior_pass_slack
            || matches!(
                update.map(|u| u.redo),
                Some(
                    Operation::InitializeFileRecordSegment | Operation::DeallocateFileRecordSegment
                )
            )
        {
            excluded.execute([root])?;
        }
        if update.is_some_and(|u| !u.redo_names.is_empty() || !u.undo_names.is_empty()) {
            chain.execute(params![id, root])?;
        }
        let redo = match &record.body {
            Body::Update(u) => u.redo.to_string(),
            Body::Checkpoint(_) => "Checkpoint".into(),
            Body::Other { record_type, .. } => format!("RecordType {record_type}"),
        };
        insert.execute(params![
            id,
            format!("{:016X}", record.lsn),
            format!("{:016X}", record.previous_lsn),
            format!("{:016X}", record.undo_next_lsn),
            record.offset,
            record.transaction_id,
            redo,
            update.map(|u| u.undo.to_string()),
            update.map(|u| u.redo_length),
            update.map(|u| u.undo_length),
            update.map(|u| u.redo_data.as_slice()),
            update.map(|u| u.undo_data.as_slice()),
            update.map(|u| u
                .lcns
                .iter()
                .map(ToString::to_string)
                .collect::<Vec<_>>()
                .join(", ")),
            update.and_then(|u| u.mft_record)
        ])?;
        if let Some(u) = update {
            let before = times(u.undo_times.as_ref());
            let after = times(u.redo_times.as_ref());
            if !record.prior_pass_slack
                && u.redo == Operation::UpdateResidentValue
                && u.undo == Operation::UpdateResidentValue
                && let Some(first) = after.iter().position(Option::is_some)
            {
                for field in first..4 {
                    if before[field].is_none()
                        || after[field].is_none()
                        || before[field] == after[field]
                    {
                        continue;
                    }
                    let start = (field - first) * 8;
                    let range = start..start + 8;
                    if let (Some(old), Some(new)) = (
                        u.undo_time_offsets.get(range.clone()),
                        u.redo_time_offsets.get(range),
                    ) {
                        let pack = |offsets: &[u64]| {
                            offsets
                                .iter()
                                .flat_map(|n| n.to_le_bytes())
                                .collect::<Vec<_>>()
                        };
                        location.execute(params![id, field, pack(old), pack(new)])?;
                    }
                }
            }
            for (side, names, dates) in [
                ("redo", &u.redo_names, u.redo_times.as_ref()),
                ("undo", &u.undo_names, u.undo_times.as_ref()),
            ] {
                for n in names {
                    name.execute(params![
                        id,
                        side,
                        n.name,
                        n.file.map(|f| f.raw().to_le_bytes()),
                        n.parent.raw().to_le_bytes(),
                        matches!(n.namespace, ntfslog::Namespace::Dos)
                    ])?;
                }
                if dates.is_some() {
                    let [c, m, h, a] = if side == "undo" { before } else { after };
                    stamp.execute(params![id, side, c, m, h, a])?;
                }
            }
        }
    }
    drop((
        insert, name, stamp, location, provenance, chain, excluded, parents,
    ));
    for p in &log.problems {
        tx.execute(
            "INSERT INTO tx_warnings VALUES(?,?)",
            params![p.offset, format!("{:?}", p.kind)],
        )?;
    }
    tx.execute_batch("CREATE INDEX tx_name_record ON tx_names(id); CREATE INDEX tx_time_record ON tx_times(id); CREATE INDEX tx_previous ON tx_records(prev);")?;
    tx.execute_batch("
        CREATE INDEX chain_root ON named_chains(root);
        CREATE TABLE tx_moves AS WITH links AS (
          SELECT c.root,n.file,n.id,n.parent,n.name,n.side FROM named_chains c JOIN tx_records r ON r.id=c.id
          JOIN tx_names n ON n.id=c.id WHERE n.file IS NOT NULL AND n.short_name=0 AND
          ((r.redo IN ('DeleteIndexEntryRoot','DeleteIndexEntryAllocation') AND n.side='undo') OR
           (r.redo IN ('AddIndexEntryRoot','AddIndexEntryAllocation') AND n.side='redo'))
        ), pairs AS (
          SELECT root,file FROM links GROUP BY root,file HAVING count(*)=2 AND count(DISTINCT side)=2
        )
        SELECT o.id old_id,n.id new_id,o.file,o.name old_name,n.name new_name,
               o.parent old_parent,n.parent new_parent FROM pairs p
        JOIN links o ON o.root=p.root AND o.file=p.file AND o.side='undo'
        JOIN links n ON n.root=p.root AND n.file=p.file AND n.side='redo'
        WHERE o.id<n.id AND (o.parent<>n.parent OR o.name<>n.name)
        AND NOT EXISTS(SELECT 1 FROM excluded_roots e WHERE e.root=p.root)
        ORDER BY p.root,p.file;
        CREATE INDEX move_old ON tx_moves(old_id); CREATE INDEX move_new ON tx_moves(new_id);")?;
    tx.execute(
        "INSERT INTO tx_summary VALUES(?,?,?,?)",
        params![hash, bytes.len(), log.records.len(), log.problems.len()],
    )?;
    crate::output::Module::TransactionLog.write_metadata(&tx)?;
    tx.commit()?;
    println!("done {} {}", log.records.len(), log.problems.len());
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chain_roots_follow_exact_lsn_across_branches_and_stop_at_transaction_boundaries() {
        use Operation::{CommitTransaction, ForgetTransaction};
        let records = [
            (11, 0, 3, None, 1),
            (15, 11, 3, None, 1),
            (20, 11, 3, None, 1),
            (21, 15, 9, None, 4),
            (23, 15, 3, Some(CommitTransaction), 1),
            (30, 23, 3, None, 6),
            (31, 20, 3, Some(ForgetTransaction), 1),
            (32, 31, 3, None, 8),
            (38, 37, 3, None, 9),
            (40, 41, 3, None, 10),
            (41, 41, 3, None, 11),
            (44, 0, 0, None, 12),
            (45, 44, 0, None, 13),
            (u64::MAX - 1, 11, 3, None, 1),
            (u64::MAX, u64::MAX - 1, 3, None, 1),
        ];
        let mut parents = Vec::new();
        for (index, (lsn, previous, transaction, redo, expected)) in records.into_iter().enumerate()
        {
            assert_eq!(
                chain_root(&mut parents, index + 1, lsn, previous, transaction, redo),
                expected
            );
        }
    }
}
