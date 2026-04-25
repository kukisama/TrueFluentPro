using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.ImagePipeline.Steps
{
    /// <summary>
    /// 参考图上传：使用 FileIdCache 避免重复上传同一图片。
    /// 仅在 Responses API 策略下执行。
    /// </summary>
    public sealed class UploadStep : IImagePipelineStep
    {
        private readonly IAiImageGenService _imageService;
        private readonly FileIdCache _fileIdCache;

        public UploadStep(IAiImageGenService imageService, FileIdCache fileIdCache)
        {
            _imageService = imageService;
            _fileIdCache = fileIdCache;
        }

        public string Name => "Upload";

        public async Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            if (ctx.Strategy != ImageApiStrategy.ResponsesApi)
                return true;

            var endpointBase = ctx.Config.ApiEndpoint ?? "";

            // 上传参考图
            foreach (var imagePath in ctx.Request.ReferenceImagePaths)
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    continue;

                var cached = _fileIdCache.TryGet(endpointBase, imagePath);
                if (cached != null)
                {
                    ctx.UploadedFileIds.Add(cached);
                    continue;
                }

                var fileId = await _imageService.UploadImageFileAsync(ctx.Config, imagePath, ct);
                _fileIdCache.Set(endpointBase, imagePath, fileId);
                ctx.UploadedFileIds.Add(fileId);
            }

            // 上传 Mask（如有）
            if (!string.IsNullOrWhiteSpace(ctx.Request.MaskImagePath) && File.Exists(ctx.Request.MaskImagePath))
            {
                var maskCached = _fileIdCache.TryGet(endpointBase, ctx.Request.MaskImagePath);
                if (maskCached != null)
                {
                    ctx.MaskFileId = maskCached;
                }
                else
                {
                    var maskFileId = await _imageService.UploadImageFileAsync(ctx.Config, ctx.Request.MaskImagePath, ct);
                    _fileIdCache.Set(endpointBase, ctx.Request.MaskImagePath, maskFileId);
                    ctx.MaskFileId = maskFileId;
                }
            }

            return true;
        }
    }
}
