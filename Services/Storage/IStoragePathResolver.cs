namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 统一路径转换服务：绝对路径 ↔ 工作目录相对路径，以及新资源时间分桶目录生成。
    /// </summary>
    public interface IStoragePathResolver
    {
        string WorkspaceRoot { get; }
        string ToRelativePath(string absolutePath);
        string ToAbsolutePath(string relativePath);
        string GetNewResourcePath(string mediaType, string extension);

        /// <summary>
        /// 返回按月分桶的资源目录绝对路径，如 {WorkspaceRoot}/library/{mediaType}/{yyyy}/{MM}/，不存在时自动创建。
        /// </summary>
        string GetNewResourceDirectory(string mediaType);
    }
}
