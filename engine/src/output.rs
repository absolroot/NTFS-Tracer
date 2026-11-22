use rusqlite::{Transaction, params};

pub(crate) enum Module {
    Journal,
    CarvedJournal,
    Mft,
    TransactionLog,
    CarvedLog,
}

impl Module {
    pub(crate) fn write_metadata(&self, tx: &Transaction<'_>) -> rusqlite::Result<()> {
        let (kind, version, summary) = match self {
            Self::Journal => ("usn", 3, "summary"),
            Self::CarvedJournal => ("usn_carve", 3, "summary"),
            Self::Mft => ("mft", 1, "mft_summary"),
            Self::TransactionLog => ("ntfs_log", 2, "tx_summary"),
            Self::CarvedLog => ("ntfs_log_carve", 1, "carve_summary"),
        };
        tx.pragma_update(None, "application_id", i32::from_be_bytes(*b"JDSK"))?;
        tx.pragma_update(None, "user_version", version)?;
        tx.execute_batch(
            "CREATE TABLE artifact_meta(
               module TEXT PRIMARY KEY NOT NULL, schema_version INTEGER NOT NULL,
               engine_version TEXT NOT NULL, source_sha256 TEXT NOT NULL, source_size INTEGER NOT NULL
             ) WITHOUT ROWID;",
        )?;
        tx.execute(
            &format!("INSERT INTO artifact_meta SELECT ?1,?2,?3,hash,bytes FROM {summary}"),
            params![kind, version, env!("CARGO_PKG_VERSION")],
        )?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::Module;
    use rusqlite::Connection;

    #[test]
    fn stamp_each_module_in_its_own_evidence_transaction() {
        for (module, name, version, summary) in [
            (Module::Journal, "usn", 3, "summary"),
            (Module::CarvedJournal, "usn_carve", 3, "summary"),
            (Module::Mft, "mft", 1, "mft_summary"),
            (Module::TransactionLog, "ntfs_log", 2, "tx_summary"),
            (Module::CarvedLog, "ntfs_log_carve", 1, "carve_summary"),
        ] {
            let mut db = Connection::open_in_memory().unwrap();
            let tx = db.transaction().unwrap();
            tx.execute_batch(&format!(
                "CREATE TABLE {summary}(hash TEXT,bytes INTEGER);
                 INSERT INTO {summary} VALUES('sample-hash',112);"
            ))
            .unwrap();
            module.write_metadata(&tx).unwrap();
            let (id, schema, tag, hash, bytes): (i64, i32, String, String, i64) = tx
                .query_row(
                    "SELECT application_id, user_version, module, source_sha256, source_size
                     FROM pragma_application_id, pragma_user_version, artifact_meta
                     WHERE schema_version=user_version",
                    [],
                    |row| {
                        Ok((
                            row.get(0)?,
                            row.get(1)?,
                            row.get(2)?,
                            row.get(3)?,
                            row.get(4)?,
                        ))
                    },
                )
                .unwrap();
            assert_eq!(id, 0x4A44534B);
            assert_eq!(
                (schema, tag.as_str(), hash.as_str(), bytes),
                (version, name, "sample-hash", 112)
            );
            tx.rollback().unwrap();
            assert_eq!(
                db.pragma_query_value(None, "application_id", |row| row.get::<_, i64>(0))
                    .unwrap(),
                0
            );
        }
    }
}
