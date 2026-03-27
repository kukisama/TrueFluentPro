using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class TranslationHistoryRepository : ITranslationHistoryRepository
    {
        private readonly ISqliteDbService _db;

        public TranslationHistoryRepository(ISqliteDbService db) => _db = db;

        public void Insert(TranslationRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO translation_history (id, source_text, translated_text, source_language, target_language, created_at, is_deleted)
VALUES (@id, @src, @tgt, @slang, @tlang, @cat, 0);";
            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@src", r.SourceText);
            cmd.Parameters.AddWithValue("@tgt", r.TranslatedText);
            cmd.Parameters.AddWithValue("@slang", Db.Val(r.SourceLanguage));
            cmd.Parameters.AddWithValue("@tlang", Db.Val(r.TargetLanguage));
            cmd.Parameters.AddWithValue("@cat", Db.Ts(r.CreatedAt));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("translation_history", r.Id, "insert");
        }

        public List<TranslationRecord> List(int limit = 50, int offset = 0)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM translation_history WHERE is_deleted = 0
ORDER BY created_at DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var list = new List<TranslationRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TranslationRecord
                {
                    Id = Db.Str(r["id"]),
                    SourceText = Db.Str(r["source_text"]),
                    TranslatedText = Db.Str(r["translated_text"]),
                    SourceLanguage = Db.NStr(r["source_language"]),
                    TargetLanguage = Db.NStr(r["target_language"]),
                    CreatedAt = Db.Dt(r["created_at"]),
                    IsDeleted = Db.Bool(r["is_deleted"]),
                });
            }

            SqliteDebugLogger.LogRead("translation_history", $"limit={limit} offset={offset}", list.Count);
            return list;
        }

        public int Count()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translation_history WHERE is_deleted = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void SoftDelete(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE translation_history SET is_deleted = 1 WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("translation_history", id, "soft-delete");
        }
    }
}
