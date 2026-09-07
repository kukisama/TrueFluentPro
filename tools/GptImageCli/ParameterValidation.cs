namespace GptImageCli;

internal static class ParameterValidation
{
    public static void Validate(CliOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new CliException("--endpoint 必须是无用户名、密码及片段的绝对 HTTP/HTTPS URL。");
        if (options.Count is < 1 or > 10)
            throw new CliException("--n / --count 必须在 1..10 之间。");
        if (options.TimeoutMinutes > 35791)
            throw new CliException("--timeout-minutes 不能大于 35791。");
        if (options.Quality is not ("low" or "medium" or "high" or "auto"))
            throw new CliException("--quality 只支持 low、medium、high 或 auto。");
        if (options.Background is not (null or "auto" or "opaque" or "transparent"))
            throw new CliException("--background 只支持 auto、opaque 或 transparent。");
        if (options.Moderation is not (null or "auto" or "low"))
            throw new CliException("--moderation 只支持 auto 或 low。");
        if (options.OutputCompression is < 0 or > 100 ||
            (options.OutputCompression.HasValue && options.OutputFormat is not ("jpeg" or "webp")))
            throw new CliException("--output-compression 必须是 0..100 的整数，且仅支持 jpeg/webp。");
        if (options.Background == "transparent" && options.OutputFormat == "jpeg")
            throw new CliException("透明背景仅支持 png/webp，不支持 jpeg。");
        if (options.Mode == ApiMode.Responses && options.User is not null)
            throw new CliException("responses 模式暂不支持 --user；请使用 images 或 edit。");
        if (options.MaskPath is { } mask)
        {
            if (options.Mode != ApiMode.Edit)
                throw new CliException("--mask 仅支持 edit 模式。");
            if (!File.Exists(mask) || !Path.GetExtension(mask).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new CliException("--mask 必须是存在的 PNG 文件。");
            using var stream = File.OpenRead(mask);
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new CliException("--mask 不是有效的 PNG 文件。");
        }
        if (options.Mode != ApiMode.Responses && options.ImageModel.Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase) && options.Size != "auto")
        {
            var parts = options.Size.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) ||
                w <= 0 || h <= 0 || w > 3840 || h > 3840 || w % 16 != 0 || h % 16 != 0 ||
                Math.Max(w, h) > 3L * Math.Min(w, h) || (long)w * h is < 655360 or > 8294400)
                throw new CliException("--size 必须为 auto 或 16 倍数的宽x高；边长≤3840，比例≤3，像素数 655360..8294400。");
        }
    }
}