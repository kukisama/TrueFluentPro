using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.ImagePipeline.Steps
{
    /// <summary>
    /// 路由选择：根据请求特征自动决定 API 策略。
    /// 决策逻辑（§16 统一 Responses API 路线）：
    ///   - 所有场景统一走 Responses API
    ///   - 保留 /images/generations 和 /images/edits 代码但暂不接入
    /// </summary>
    public sealed class RouteStep : IImagePipelineStep
    {
        public string Name => "Route";

        public Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            // 统一走 Responses API（§16 决策）
            // 未来可根据条件切换到 ImageGenerations 或 ImageEditsMultipart
            ctx.Strategy = ImageApiStrategy.ResponsesApi;

            return Task.FromResult(true);
        }
    }
}
