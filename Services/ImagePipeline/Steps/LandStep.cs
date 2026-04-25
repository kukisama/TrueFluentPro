using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.ImagePipeline.Steps
{
    /// <summary>
    /// 文件落地：byte[] → .tmp 写入 → rename 最终文件。
    /// 所有厂商/策略共用，不需要定制。
    /// </summary>
    public sealed class LandStep : IImagePipelineStep
    {
        public string Name => "Land";

        public async Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            if (ctx.DecodedImages.Count == 0)
                return true;

            var outputDir = ctx.Request.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDir))
                return true;

            Directory.CreateDirectory(outputDir);

            var seq = 1;
            foreach (var data in ctx.DecodedImages)
            {
                var randomId = Guid.NewGuid().ToString("N")[..8];
                var ext = ctx.Request.Format;
                var fileName = $"img_{seq:D3}_{randomId}.{ext}";
                var filePath = Path.Combine(outputDir, fileName);
                var tmpPath = filePath + ".tmp";

                try
                {
                    await File.WriteAllBytesAsync(tmpPath, data, ct);
                    File.Move(tmpPath, filePath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }

                ctx.ResultFilePaths.Add(filePath);
                seq++;
            }

            return true;
        }
    }
}
