using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.ImagePipeline
{
    /// <summary>
    /// 图片生成管线中的一个处理步骤。
    /// 每个步骤职责单一，可独立替换。
    /// </summary>
    public interface IImagePipelineStep
    {
        /// <summary>步骤名（用于日志和诊断）</summary>
        string Name { get; }

        /// <summary>
        /// 执行步骤。返回 true = 继续下一步，false = 中止管线。
        /// </summary>
        Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct);
    }
}
