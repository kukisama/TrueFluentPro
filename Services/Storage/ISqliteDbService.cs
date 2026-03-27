using System;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 数据库生命周期管理：连接创建、Schema 初始化与版本迁移。
    /// </summary>
    public interface ISqliteDbService : IDisposable
    {
        string DatabasePath { get; }
        int SchemaVersion { get; }
        SqliteConnection CreateConnection();
        void EnsureCreated();

        /// <summary>读取 _meta 表中的值，不存在返回 null。</summary>
        string? GetMeta(string key);

        /// <summary>写入或更新 _meta 表中的值。</summary>
        void SetMeta(string key, string value);
    }
}
