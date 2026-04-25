using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.ImagePipeline.Steps
{
    /// <summary>
    /// 构建 Responses API 请求体 JSON 和所需的 HTTP 头。
    /// 将结果存入 ctx.RequestJsonBody / ctx.RequestHeaders，供 ExecuteStep 使用。
    /// </summary>
    public sealed class BuildRequestStep : IImagePipelineStep
    {
        public string Name => "BuildRequest";

        public Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            if (ctx.Strategy != ImageApiStrategy.ResponsesApi)
            {
                ctx.ErrorCode = ImageErrorCodes.ApiError;
                ctx.ErrorMessage = "BuildRequestStep 仅支持 ResponsesApi 策略";
                return Task.FromResult(false);
            }

            var req = ctx.Request;
            var genConfig = ctx.GenConfig;
            bool hasReferenceInput = ctx.UploadedFileIds.Count > 0;

            // 构建 input content：文本提示 + 已上传的参考图
            var contentItems = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "input_text",
                    ["text"] = req.Prompt
                }
            };

            foreach (var fileId in ctx.UploadedFileIds)
            {
                contentItems.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_image",
                    ["file_id"] = fileId
                });
            }

            // text model 选择：优先 genConfig.TextModelForResponses，回退到 config.ModelName
            var textModel = !string.IsNullOrWhiteSpace(genConfig.TextModelForResponses)
                ? genConfig.TextModelForResponses
                : ctx.Config.ModelName;

            var bodyObj = new Dictionary<string, object>
            {
                ["model"] = textModel,
                ["input"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = contentItems
                    }
                },
                ["tools"] = new[]
                {
                    AiImageGenService.BuildImageGenerationToolDefinition(genConfig, hasReferenceInput)
                }
            };

            // 多图指令
            if (req.ImageCount > 1)
            {
                bodyObj["instructions"] = $"When generating images, always produce EXACTLY {req.ImageCount} images. Each image should be a distinct variation.";
            }

            // 多轮改图
            if (!string.IsNullOrWhiteSpace(req.PreviousResponseId))
                bodyObj["previous_response_id"] = req.PreviousResponseId;

            ctx.RequestJsonBody = JsonSerializer.Serialize(bodyObj);

            // Azure OpenAI 需通过 header 指定图片模型部署名
            ctx.RequestHeaders["x-ms-oai-image-generation-deployment"] = genConfig.ImageModel;

            return Task.FromResult(true);
        }
    }
}
