using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

internal enum EngineArtifact
{
    Journal, CarvedJournal, Mft, TransactionLog, CarvedLog
}

internal static class EngineIndexFormat
{
    internal static void Validate(SqliteConnection db, EngineArtifact expected)
    {
        var (kind, version, legacy, summary) = expected switch
        {
            EngineArtifact.Journal => ("usn", 3, 2, "summary"),
            EngineArtifact.CarvedJournal => ("usn_carve", 3, 2, "summary"),
            EngineArtifact.Mft => ("mft", 1, 1, "mft_summary"),
            EngineArtifact.TransactionLog => ("ntfs_log", 1, 1, "tx_summary"),
            EngineArtifact.CarvedLog => ("ntfs_log_carve", 1, 1, "carve_summary"),
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA application_id";
        long appId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        cmd.CommandText = "PRAGMA user_version";
        long schema = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='artifact_meta')";
        bool manifest = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        // Previous versions recorded only a schema version; their module-specific
        // summary and the original hash are checked by the opener.
        if (appId == 0 && !manifest && schema == legacy)
            return;
        if (appId != 0x4A44534B || !manifest || schema != version)
            throw new InvalidDataException(L.T("색인 식별 정보 또는 형식 버전이 현재 모듈과 일치하지 않습니다.", "The index identifier or format version does not match this module."));

        cmd.CommandText = "SELECT module,schema_version,source_sha256,source_size FROM artifact_meta";
        string hash;
        long bytes;
        using (var metadata = cmd.ExecuteReader())
        {
            if (!metadata.Read() || metadata.GetString(0) != kind || metadata.GetInt64(1) != schema)
                throw new InvalidDataException(L.T("선택한 색인이 다른 분석 모듈용이거나 버전 정보가 손상됐습니다.", "The index belongs to another module or has invalid version information."));
            hash = metadata.GetString(2);
            bytes = metadata.GetInt64(3);
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || bytes < 0 || metadata.Read())
                throw new InvalidDataException(L.T("색인의 원본 식별 정보를 확인할 수 없습니다.", "The source identifier is unavailable in the index."));
        }
        cmd.CommandText = $"SELECT hash,bytes FROM {summary}";
        using var row = cmd.ExecuteReader();
        if (!row.Read() || !StringComparer.OrdinalIgnoreCase.Equals(row.GetString(0), hash) || row.GetInt64(1) != bytes || row.Read())
            throw new InvalidDataException(L.T("색인의 원본 해시 또는 크기가 모듈의 분석 결과와 일치하지 않습니다.", "The indexed source hash or size does not match the module output."));
    }
}
