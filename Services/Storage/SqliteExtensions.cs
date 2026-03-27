using System;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 读写辅助方法，简化 DBNull 处理与类型转换。
    /// </summary>
    internal static class Db
    {
        // ── 读取辅助（从 DataReader["column"] 返回的 object 安全转换） ──

        public static string Str(object val) => val is DBNull ? "" : (string)val;
        public static string? NStr(object val) => val is DBNull ? null : (string)val;
        public static int Int(object val) => val is DBNull ? 0 : Convert.ToInt32(val);
        public static int? NInt(object val) => val is DBNull ? null : Convert.ToInt32(val);
        public static long Long(object val) => val is DBNull ? 0 : Convert.ToInt64(val);
        public static long? NLong(object val) => val is DBNull ? null : Convert.ToInt64(val);
        public static double Dbl(object val) => val is DBNull ? 0 : Convert.ToDouble(val);
        public static double? NDbl(object val) => val is DBNull ? null : Convert.ToDouble(val);
        public static bool Bool(object val) => val is not DBNull && Convert.ToInt32(val) != 0;
        public static DateTime Dt(object val) => val is DBNull ? DateTime.MinValue : DateTime.Parse((string)val);
        public static DateTime? NDt(object val) => val is DBNull ? null : DateTime.Parse((string)val);

        // ── 写入辅助（将 C# 值转为 SQLite 参数值） ──

        public static object Val(string? v) => (object?)v ?? DBNull.Value;
        public static object Val(int? v) => v.HasValue ? v.Value : DBNull.Value;
        public static object Val(long? v) => v.HasValue ? v.Value : DBNull.Value;
        public static object Val(double? v) => v.HasValue ? v.Value : DBNull.Value;
        public static object Val(DateTime? v) => v.HasValue ? v.Value.ToString("o") : DBNull.Value;
        public static object Ts(DateTime v) => v.ToString("o");
        public static object BoolInt(bool v) => v ? 1 : 0;
    }
}
