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
    }
}
