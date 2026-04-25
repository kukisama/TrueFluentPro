using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.ImagePipeline
{
    /// <summary>
    /// 图片生成管线运行器。按序执行各 Step，任一 Step 返回 false 则中止。
    /// </summary>
    public sealed class ImagePipelineRunner
    {
        private readonly List<IImagePipelineStep> _steps = new();

        public ImagePipelineRunner AddStep(IImagePipelineStep step)
        {
            _steps.Add(step);
            return this;
        }

        /// <summary>
        /// 按序执行全部 Step。如果某个 Step 返回 false 或抛异常，管线中止。
        /// </summary>
        public async Task<ImagePipelineContext> RunAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            foreach (var step in _steps)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var shouldContinue = await step.ExecuteAsync(ctx, ct);
                    if (!shouldContinue) break;
                }
                catch (OperationCanceledException)
                {
                    ctx.ErrorCode = Models.ImageErrorCodes.UserCancel;
                    ctx.ErrorMessage = "用户取消";
                    throw;
                }
                catch (Exception ex)
                {
                    ctx.ErrorCode ??= Models.ImageErrorCodes.ApiError;
                    ctx.ErrorMessage ??= ex.Message;
                    throw;
                }
            }

            return ctx;
        }
    }
}
