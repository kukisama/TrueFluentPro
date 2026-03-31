using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class SessionMessageRepository : ISessionMessageRepository
    {
        private readonly ISqliteDbService _db;

        public SessionMessageRepository(ISqliteDbService db) => _db = db;

        public void Insert(MessageRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO session_messages (
    id, session_id, sequence_no, role, content_type, text, reasoning_text,
    prompt_tokens, completion_tokens, generate_seconds, download_seconds,
    search_summary, timestamp, is_deleted
) VALUES (
    @id, @session_id, @seq, @role, @content_type, @text, @reasoning_text,
    @prompt_tokens, @completion_tokens, @generate_seconds, @download_seconds,
    @search_summary, @timestamp, 0
);";
            BindMessage(cmd, r);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("session_messages", r.Id, $"insert seq={r.SequenceNo}");
        }

        public void Update(MessageRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE session_messages SET
    role=@role, content_type=@content_type, text=@text, reasoning_text=@reasoning_text,
    prompt_tokens=@prompt_tokens, completion_tokens=@completion_tokens,
    generate_seconds=@generate_seconds, download_seconds=@download_seconds,
    search_summary=@search_summary, timestamp=@timestamp
WHERE id = @id;";
            BindMessage(cmd, r);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("session_messages", r.Id, "update");
        }

        public MessageRecord? GetById(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_messages WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(r) : null;
        }

        public List<MessageRecord> GetLatest(string sessionId, int limit = 40)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM session_messages
WHERE session_id = @sid AND is_deleted = 0
ORDER BY sequence_no DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = ReadAll(cmd);
            list.Reverse(); // 返回正序
            SqliteDebugLogger.LogLazyLoad(sessionId, list.Count > 0 ? list[0].SequenceNo : 0, list.Count);
            return list;
        }

        public List<MessageRecord> GetBefore(string sessionId, int beforeSequence, int limit = 40)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM session_messages
WHERE session_id = @sid AND is_deleted = 0 AND sequence_no < @before
ORDER BY sequence_no DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@before", beforeSequence);
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = ReadAll(cmd);
            list.Reverse();
            SqliteDebugLogger.LogLazyLoad(sessionId, list.Count > 0 ? list[0].SequenceNo : 0, list.Count);
            return list;
        }

        public int GetCount(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM session_messages WHERE session_id = @sid AND is_deleted = 0;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public int GetMaxSequence(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(sequence_no), 0) FROM session_messages WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void SoftDelete(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE session_messages SET is_deleted = 1 WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("session_messages", id, "soft-delete");
        }

        // ── 媒体引用 ──────────────────────────────────

        public void InsertMediaRefs(string messageId, List<MediaRefRecord> refs)
        {
            if (refs.Count == 0) return;
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            foreach (var mr in refs)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO message_media_refs (message_id, media_path, media_kind, sort_order, preview_path)
VALUES (@mid, @path, @kind, @sort, @preview);";
                cmd.Parameters.AddWithValue("@mid", messageId);
                cmd.Parameters.AddWithValue("@path", mr.MediaPath);
                cmd.Parameters.AddWithValue("@kind", mr.MediaKind);
                cmd.Parameters.AddWithValue("@sort", mr.SortOrder);
                cmd.Parameters.AddWithValue("@preview", Db.Val(mr.PreviewPath));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            SqliteDebugLogger.LogWrite("message_media_refs", messageId, $"insert {refs.Count} 条");
        }

        public List<MediaRefRecord> GetMediaRefs(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM message_media_refs WHERE message_id = @mid ORDER BY sort_order;";
            cmd.Parameters.AddWithValue("@mid", messageId);

            var list = new List<MediaRefRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new MediaRefRecord
                {
                    Id = Db.Long(r["id"]),
                    MessageId = Db.Str(r["message_id"]),
                    MediaPath = Db.Str(r["media_path"]),
                    MediaKind = Db.Str(r["media_kind"]),
                    SortOrder = Db.Int(r["sort_order"]),
                    PreviewPath = Db.NStr(r["preview_path"]),
                });
            }
            return list;
        }

        public void DeleteMediaRefs(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM message_media_refs WHERE message_id = @mid;";
            cmd.Parameters.AddWithValue("@mid", messageId);
            cmd.ExecuteNonQuery();
        }

        // ── 网页引用 ──────────────────────────────────

        public void InsertCitations(string messageId, List<CitationRecord> citations)
        {
            if (citations.Count == 0) return;
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            foreach (var c in citations)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO message_citations (message_id, citation_number, title, url, snippet, hostname)
VALUES (@mid, @num, @title, @url, @snippet, @hostname);";
                cmd.Parameters.AddWithValue("@mid", messageId);
                cmd.Parameters.AddWithValue("@num", c.CitationNumber);
                cmd.Parameters.AddWithValue("@title", c.Title);
                cmd.Parameters.AddWithValue("@url", c.Url);
                cmd.Parameters.AddWithValue("@snippet", c.Snippet);
                cmd.Parameters.AddWithValue("@hostname", c.Hostname);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            SqliteDebugLogger.LogWrite("message_citations", messageId, $"insert {citations.Count} 条");
        }

        public List<CitationRecord> GetCitations(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM message_citations WHERE message_id = @mid ORDER BY citation_number;";
            cmd.Parameters.AddWithValue("@mid", messageId);

            var list = new List<CitationRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CitationRecord
                {
                    Id = Db.Long(r["id"]),
                    MessageId = Db.Str(r["message_id"]),
                    CitationNumber = Db.Int(r["citation_number"]),
                    Title = Db.Str(r["title"]),
                    Url = Db.Str(r["url"]),
                    Snippet = Db.Str(r["snippet"]),
                    Hostname = Db.Str(r["hostname"]),
                });
            }
            return list;
        }

        public void DeleteCitations(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM message_citations WHERE message_id = @mid;";
            cmd.Parameters.AddWithValue("@mid", messageId);
            cmd.ExecuteNonQuery();
        }

        // ── 消息附件 ──────────────────────────────────

        public void InsertAttachments(string messageId, List<AttachmentRecord> attachments)
        {
            if (attachments.Count == 0) return;
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();

            foreach (var a in attachments)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO message_attachments (message_id, attachment_type, file_name, file_path, file_size, sort_order)
VALUES (@mid, @type, @name, @path, @size, @sort);";
                cmd.Parameters.AddWithValue("@mid", messageId);
                cmd.Parameters.AddWithValue("@type", a.AttachmentType);
                cmd.Parameters.AddWithValue("@name", a.FileName);
                cmd.Parameters.AddWithValue("@path", a.FilePath);
                cmd.Parameters.AddWithValue("@size", a.FileSize);
                cmd.Parameters.AddWithValue("@sort", a.SortOrder);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            SqliteDebugLogger.LogWrite("message_attachments", messageId, $"insert {attachments.Count} 条");
        }

        public List<AttachmentRecord> GetAttachments(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM message_attachments WHERE message_id = @mid ORDER BY sort_order;";
            cmd.Parameters.AddWithValue("@mid", messageId);

            var list = new List<AttachmentRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AttachmentRecord
                {
                    Id = Db.Long(r["id"]),
                    MessageId = Db.Str(r["message_id"]),
                    AttachmentType = Db.Str(r["attachment_type"]),
                    FileName = Db.Str(r["file_name"]),
                    FilePath = Db.Str(r["file_path"]),
                    FileSize = Db.Long(r["file_size"]),
                    SortOrder = Db.Int(r["sort_order"]),
                });
            }
            return list;
        }

        public void DeleteAttachments(string messageId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM message_attachments WHERE message_id = @mid;";
            cmd.Parameters.AddWithValue("@mid", messageId);
            cmd.ExecuteNonQuery();
        }

        // ── 内部辅助 ──────────────────────────────────

        private static void BindMessage(SqliteCommand cmd, MessageRecord r)
        {
            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@session_id", r.SessionId);
            cmd.Parameters.AddWithValue("@seq", r.SequenceNo);
            cmd.Parameters.AddWithValue("@role", r.Role);
            cmd.Parameters.AddWithValue("@content_type", r.ContentType);
            cmd.Parameters.AddWithValue("@text", r.Text);
            cmd.Parameters.AddWithValue("@reasoning_text", r.ReasoningText);
            cmd.Parameters.AddWithValue("@prompt_tokens", Db.Val(r.PromptTokens));
            cmd.Parameters.AddWithValue("@completion_tokens", Db.Val(r.CompletionTokens));
            cmd.Parameters.AddWithValue("@generate_seconds", Db.Val(r.GenerateSeconds));
            cmd.Parameters.AddWithValue("@download_seconds", Db.Val(r.DownloadSeconds));
            cmd.Parameters.AddWithValue("@search_summary", Db.Val(r.SearchSummary));
            cmd.Parameters.AddWithValue("@timestamp", Db.Ts(r.Timestamp));
        }

        private static List<MessageRecord> ReadAll(SqliteCommand cmd)
        {
            var list = new List<MessageRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRow(r));
            return list;
        }

        private static MessageRecord ReadRow(SqliteDataReader r)
        {
            return new MessageRecord
            {
                Id = Db.Str(r["id"]),
                SessionId = Db.Str(r["session_id"]),
                SequenceNo = Db.Int(r["sequence_no"]),
                Role = Db.Str(r["role"]),
                ContentType = Db.Str(r["content_type"]),
                Text = Db.Str(r["text"]),
                ReasoningText = Db.Str(r["reasoning_text"]),
                PromptTokens = Db.NInt(r["prompt_tokens"]),
                CompletionTokens = Db.NInt(r["completion_tokens"]),
                GenerateSeconds = Db.NDbl(r["generate_seconds"]),
                DownloadSeconds = Db.NDbl(r["download_seconds"]),
                SearchSummary = Db.NStr(r["search_summary"]),
                Timestamp = Db.Dt(r["timestamp"]),
                IsDeleted = Db.Bool(r["is_deleted"]),
            };
        }
    }
}
