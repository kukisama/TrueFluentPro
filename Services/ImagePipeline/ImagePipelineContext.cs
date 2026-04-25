using System;
using System.Collections.Generic;
using System.Net.Http;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.ImagePipeline
{
    /// <summary>
    /// API 路由策略枚举。
    /// </summary>
    public enum ImageApiStrategy
    {
        /// <summary>/images/generations (JSON body, 无参考图)</summary>
        ImageGenerations,

        /// <summary>/images/edits (multipart, 单张参考图)</summary>
        ImageEditsMultipart,

        /// <summary>/responses + image_generation tool (全功能推荐路线)</summary>
        ResponsesApi
    }

    /// <summary>
    /// 图片任务请求（管线输入，不可变）。
    /// </summary>
    public sealed class ImageTaskRequest
    {
        public required string Prompt { get; init; }
        public IReadOnlyList<string> ReferenceImagePaths { get; init; } = Array.Empty<string>();
        public string? MaskImagePath { get; init; }
        public string? PreviousResponseId { get; init; }
        public int ImageCount { get; init; } = 1;
        public string Quality { get; init; } = "medium";
        public string Size { get; init; } = "auto";
        public string Format { get; init; } = "png";
        public string Background { get; init; } = "auto";
        public string OutputDirectory { get; init; } = "";
        public string? SessionId { get; init; }
        public string? TaskId { get; init; }
    }

    /// <summary>
    /// 图片管线上下文——在各 Step 之间流转的数据包。
    /// </summary>
    public sealed class ImagePipelineContext
    {
        // ── 输入（不可变）──
        public required ImageTaskRequest Request { get; init; }
        public required ImageModelCapabilities? ModelCaps { get; init; }
        public required AiConfig Config { get; init; }
        public required MediaGenConfig GenConfig { get; init; }

        // ── 中间状态（Step 之间传递）──
        public ImageApiStrategy Strategy { get; set; } = ImageApiStrategy.ResponsesApi;
        public List<string> UploadedFileIds { get; set; } = new();
        public string? MaskFileId { get; set; }
        public HttpResponseMessage? HttpResponse { get; set; }
        public List<byte[]> DecodedImages { get; set; } = new();
        public string? ResponseId { get; set; }

        // ── BuildRequestStep 产出 ──
        public string? RequestJsonBody { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; } = new();

        // ── 输出 ──
        public List<string> ResultFilePaths { get; set; } = new();
        public double GenerateSeconds { get; set; }
        public double DownloadSeconds { get; set; }

        // ── Token / 计费 ──
        public int? ActualInputTokens { get; set; }
        public int? ActualOutputTokens { get; set; }
        public int? ActualCachedTokens { get; set; }

        // ── 诊断 ──
        public List<string> AttemptedUrls { get; set; } = new();
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? HttpStatus { get; set; }
    }
}
