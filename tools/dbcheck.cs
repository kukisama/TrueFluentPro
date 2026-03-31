using System;
using Microsoft.Data.Sqlite;

var conn = new SqliteConnection(@"Data Source=C:\Users\a9y\AppData\Roaming\TrueFluentPro\truefluentpro.db;Mode=ReadOnly");
conn.Open();
var cmd = conn.CreateCommand();

cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_type='media-center-v2' AND is_deleted=0";
Console.WriteLine($"Total non-deleted: {cmd.ExecuteScalar()}");

cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_type='media-center-v2' AND is_deleted=0 AND (task_count > 0 OR asset_count > 0)";
Console.WriteLine($"Filtered (task>0 OR asset>0): {cmd.ExecuteScalar()}");

cmd.CommandText = "SELECT id, name, task_count, asset_count, message_count, created_at, directory_path FROM sessions WHERE session_type='media-center-v2' AND is_deleted=0 AND (task_count > 0 OR asset_count > 0) ORDER BY created_at DESC";
using var r = cmd.ExecuteReader();
int i = 0;
while(r.Read())
{
    i++;
    var dir = r.GetString(6);
    var dirName = System.IO.Path.GetFileName(dir);
    Console.WriteLine($"{i,3}. id={r.GetString(0)} name={r.GetString(1),-30} task={r.GetInt64(2)} asset={r.GetInt64(3)} msg={r.GetInt64(4)} created={r.GetString(5)} dir={dirName}");
}
r.Close();
conn.Close();
