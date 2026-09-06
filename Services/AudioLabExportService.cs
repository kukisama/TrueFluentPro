using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

public interface IAudioLabExportService
{
    Task<AudioLabExportResult> ExportAsync(
        string exportDirectory,
        AudioLabExportRequest request,
        CancellationToken cancellationToken = default);
}

public enum AudioLabExportArtifactKind
{
    Markdown,
    MindMap,
    FileCopy
}

/// <summary>单个导出产物。页面只描述产物，服务不感知页面或提示词阶段。</summary>
public sealed class AudioLabExportArtifact
{
    public AudioLabExportArtifactKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string MarkdownContent { get; init; } = "";
    public MindMapNode? MindMapRoot { get; init; }
    public string SourceFilePath { get; init; } = "";
}

/// <summary>听析中心导出请求。所有页面和提示词套件共用同一产物协议。</summary>
public sealed class AudioLabExportRequest
{
    /// <summary>仅用于生成导出文件名前缀，不决定导出类型。</summary>
    public string SourceAudioPath { get; init; } = "";
    public IReadOnlyList<AudioLabExportArtifact> Artifacts { get; init; } = Array.Empty<AudioLabExportArtifact>();
}

public sealed class AudioLabExportResult
{
    public string ExportDirectory { get; }
    public IReadOnlyList<string> ExportedFiles { get; }

    public AudioLabExportResult(string exportDirectory, IReadOnlyList<string> exportedFiles)
    {
        ExportDirectory = exportDirectory;
        ExportedFiles = exportedFiles;
    }
}

/// <summary>
/// 听析中心通用导出服务。调用方只提供目标目录和当前能力快照，
/// 服务负责生成标准文件名、Markdown、导图图片及所需音频副本。
/// </summary>
public sealed class AudioLabExportService : IAudioLabExportService
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private const int MaximumMindMapNodes = 2000;
    private const int MaximumMindMapDepth = 20;

    public async Task<AudioLabExportResult> ExportAsync(
        string exportDirectory,
        AudioLabExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(exportDirectory))
            throw new ArgumentException("导出目录为空。", nameof(exportDirectory));
        if (string.IsNullOrWhiteSpace(request.SourceAudioPath))
            throw new InvalidOperationException("请先加载音频文件。");
        if (request.Artifacts.Count == 0)
            throw new InvalidOperationException("当前页面没有可导出的产物。");

        Directory.CreateDirectory(exportDirectory);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(request.SourceAudioPath));
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "听析内容";
        baseName = FindAvailableBaseName(exportDirectory, baseName, request.Artifacts);

        var stagingDirectory = Path.Combine(exportDirectory, $".truefluentpro-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var committedFiles = new List<string>();
        try
        {
            var stagedFiles = await ExportCoreAsync(stagingDirectory, baseName, request, cancellationToken);
            foreach (var stagedFile in stagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(exportDirectory, Path.GetFileName(stagedFile));
                File.Move(stagedFile, destination, overwrite: false);
                committedFiles.Add(destination);
            }
            return new AudioLabExportResult(exportDirectory, committedFiles);
        }
        catch
        {
            foreach (var committedFile in committedFiles)
            {
                try { File.Delete(committedFile); }
                catch { /* 尽力回滚，保留原始异常。 */ }
            }
            throw;
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch { /* 临时目录清理由系统后续处理，不覆盖导出结果。 */ }
        }
    }

    private static async Task<List<string>> ExportCoreAsync(
        string exportDirectory,
        string baseName,
        AudioLabExportRequest request,
        CancellationToken cancellationToken)
    {
        var exportedFiles = new List<string>();
        foreach (var artifact in request.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = SanitizeFileName(artifact.Label);
            if (string.IsNullOrWhiteSpace(label)) label = "导出内容";

            switch (artifact.Kind)
            {
                case AudioLabExportArtifactKind.Markdown:
                    await WriteMarkdownAsync(
                        exportDirectory, baseName, label,
                        artifact.MarkdownContent, exportedFiles, cancellationToken);
                    break;
                case AudioLabExportArtifactKind.MindMap:
                    await ExportMindMapAsync(
                        exportDirectory, baseName, label,
                        artifact.MindMapRoot, exportedFiles, cancellationToken);
                    break;
                case AudioLabExportArtifactKind.FileCopy:
                    await CopyFileAsync(
                        artifact.SourceFilePath, exportDirectory, baseName, label,
                        exportedFiles, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"不支持的导出产物类型：{artifact.Kind}");
            }
        }

        return exportedFiles;
    }

    public static string BuildTranscriptMarkdown(
        string title,
        string sourceFileName,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(EscapeHeading(title)).AppendLine(" 录音稿").AppendLine();
        builder.Append("> 原始音频：").AppendLine(sourceFileName).AppendLine();

        foreach (var segment in segments)
        {
            var totalHours = (long)segment.StartTime.TotalHours;
            var time = string.Create(
                CultureInfo.InvariantCulture,
                $"{totalHours:00}:{segment.StartTime.Minutes:00}:{segment.StartTime.Seconds:00}");
            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "未知说话人" : segment.Speaker.Trim();
            builder.Append("## [").Append(time).Append("] ").AppendLine(EscapeHeading(speaker)).AppendLine();
            builder.AppendLine(segment.Text?.Trim() ?? "").AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string BuildMindMapMarkdown(MindMapNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root.Title))
            throw new InvalidOperationException("当前思维导图没有可导出的内容。");
        ValidateMindMap(root);

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(EscapeHeading(root.Title.Trim())).AppendLine();
        foreach (var child in root.Children)
            AppendMindMapNode(builder, child, 0);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static async Task ExportMindMapAsync(
        string directory,
        string baseName,
        string label,
        MindMapNode? root,
        ICollection<string> exportedFiles,
        CancellationToken cancellationToken)
    {
        if (root == null || string.IsNullOrWhiteSpace(root.Title))
            throw new InvalidOperationException("当前思维导图没有可导出的内容。");

        await WriteMarkdownAsync(
            directory,
            baseName,
            label,
            BuildMindMapMarkdown(root),
            exportedFiles,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var imagePath = Path.Combine(directory, $"{baseName}_{SanitizeFileName(label)}.png");
        await Task.Run(() => MindMapPngRenderer.Render(root, imagePath), cancellationToken);
        exportedFiles.Add(imagePath);
    }

    private static async Task WriteMarkdownAsync(
        string directory,
        string baseName,
        string label,
        string content,
        ICollection<string> exportedFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"当前{label}暂无可导出的内容。");

        var path = Path.Combine(directory, $"{baseName}_{SanitizeFileName(label)}.md");
        await File.WriteAllTextAsync(path, content.TrimEnd() + Environment.NewLine, Utf8WithoutBom, cancellationToken);
        exportedFiles.Add(path);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string directory,
        string baseName,
        string label,
        ICollection<string> exportedFiles,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("要导出的附件文件不存在。", sourcePath);

        var extension = Path.GetExtension(sourcePath);
        var destinationPath = Path.Combine(directory, $"{baseName}_{SanitizeFileName(label)}{extension}");
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), pathComparison))
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
        }
        exportedFiles.Add(destinationPath);
    }

    private static string FindAvailableBaseName(
        string directory,
        string requestedBaseName,
        IReadOnlyList<AudioLabExportArtifact> artifacts)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var candidate = requestedBaseName;
        var suffix = 1;
        while (BuildTargetFileNames(candidate, artifacts)
            .Any(name => Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Any(existing => string.Equals(existing, name, comparison))))
        {
            candidate = $"{requestedBaseName} ({suffix++})";
        }
        return candidate;
    }

    private static IEnumerable<string> BuildTargetFileNames(
        string baseName,
        IReadOnlyList<AudioLabExportArtifact> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            var label = SanitizeFileName(artifact.Label);
            if (string.IsNullOrWhiteSpace(label)) label = "导出内容";
            switch (artifact.Kind)
            {
                case AudioLabExportArtifactKind.Markdown:
                    yield return $"{baseName}_{label}.md";
                    break;
                case AudioLabExportArtifactKind.MindMap:
                    yield return $"{baseName}_{label}.md";
                    yield return $"{baseName}_{label}.png";
                    break;
                case AudioLabExportArtifactKind.FileCopy:
                    yield return $"{baseName}_{label}{Path.GetExtension(artifact.SourceFilePath)}";
                    break;
            }
        }
    }

    private static void AppendMindMapNode(StringBuilder builder, MindMapNode node, int depth)
    {
        builder.Append(' ', depth * 2)
            .Append("- ")
            .AppendLine(NormalizeSingleLine(node.Title));
        foreach (var child in node.Children)
            AppendMindMapNode(builder, child, depth + 1);
    }

    private static string EscapeHeading(string value)
        => NormalizeSingleLine(value).Replace("#", "\\#", StringComparison.Ordinal);

    private static string NormalizeSingleLine(string value)
        => (value ?? "").Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = value?.Trim() ?? "";
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return TruncateTextElements(builder.ToString().Trim().TrimEnd('.'), 100);
    }

    private static string TruncateTextElements(string value, int maximumTextElements)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var builder = new StringBuilder(value.Length);
        var count = 0;
        while (count < maximumTextElements && enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());
            count++;
        }
        return builder.ToString();
    }

    private static void ValidateMindMap(MindMapNode root)
    {
        var visited = new HashSet<MindMapNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(MindMapNode Node, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (!visited.Add(node))
                throw new InvalidOperationException("思维导图包含循环引用，无法导出。");
            if (visited.Count > MaximumMindMapNodes)
                throw new InvalidOperationException($"思维导图节点超过 {MaximumMindMapNodes} 个，无法安全导出图片。");
            if (depth > MaximumMindMapDepth)
                throw new InvalidOperationException($"思维导图层级超过 {MaximumMindMapDepth} 层，无法安全导出图片。");
            if (node.Children == null)
                throw new InvalidOperationException("思维导图数据不完整，无法导出。");

            foreach (var child in node.Children)
            {
                if (child == null)
                    throw new InvalidOperationException("思维导图包含空节点，无法导出。");
                pending.Push((child, depth + 1));
            }
        }
    }

    private static class MindMapPngRenderer
    {
        private const float NodePaddingX = 18;
        private const float NodePaddingY = 11;
        private const float HorizontalGap = 56;
        private const float VerticalGap = 12;
        private const float Margin = 24;
        private const float FontSize = 16;
        private const float RootFontSize = 19;
        private const float CornerRadius = 12;
        private const int MaxTextLength = 35;
        private const float MaximumImageDimension = 6000;
        private const float MaximumImagePixels = 16_000_000;

        private static readonly SKColor[] Palette =
        {
            SKColor.Parse("#6366F1"),
            SKColor.Parse("#8B5CF6"),
            SKColor.Parse("#06B6D4"),
            SKColor.Parse("#10B981"),
            SKColor.Parse("#F59E0B")
        };

        private sealed class LayoutNode
        {
            public required MindMapNode Data { get; init; }
            public required int Depth { get; init; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
            public float TreeHeight { get; set; }
            public List<LayoutNode> Children { get; } = new();
        }

        public static void Render(MindMapNode root, string outputPath)
        {
            var layout = Build(root, 0);
            using var typeface = CreateTypeface();
            Measure(layout, typeface);
            Place(layout, Margin, Margin, layout.TreeHeight);

            var contentWidth = TreeWidth(layout) + Margin * 2;
            var contentHeight = layout.TreeHeight + Margin * 2;
            var dimensionScale = MaximumImageDimension / Math.Max(contentWidth, contentHeight);
            var pixelScale = (float)Math.Sqrt(MaximumImagePixels / Math.Max(1, contentWidth * contentHeight));
            var scale = Math.Min(1f, Math.Min(dimensionScale, pixelScale));
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(contentWidth * scale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(contentHeight * scale));

            using var bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            DrawConnections(canvas, layout);
            DrawNodes(canvas, layout, typeface);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("思维导图图片编码失败。");
            using var stream = File.Create(outputPath);
            data.SaveTo(stream);
        }

        private static SKTypeface CreateTypeface()
            => SKTypeface.FromFamilyName("Microsoft YaHei UI")
                ?? SKTypeface.FromFamilyName("Segoe UI")
                ?? SKTypeface.Default;

        private static LayoutNode Build(MindMapNode node, int depth)
        {
            var layout = new LayoutNode { Data = node, Depth = depth };
            foreach (var child in node.Children)
                layout.Children.Add(Build(child, depth + 1));
            return layout;
        }

        private static void Measure(LayoutNode node, SKTypeface typeface)
        {
            var textSize = node.Depth == 0 ? RootFontSize : FontSize;
            using var textPaint = new SKPaint
            {
                Typeface = typeface,
                TextSize = textSize,
                IsAntialias = true,
                FakeBoldText = node.Depth == 0
            };
            var text = Truncate(node.Data.Title);
            node.Width = textPaint.MeasureText(text) + NodePaddingX * 2;
            node.Height = Math.Max(textSize + NodePaddingY * 2, 42);

            foreach (var child in node.Children)
                Measure(child, typeface);

            node.TreeHeight = node.Children.Count == 0
                ? node.Height
                : Math.Max(node.Height, node.Children.Sum(child => child.TreeHeight) + (node.Children.Count - 1) * VerticalGap);
        }

        private static float TreeWidth(LayoutNode node)
            => node.Children.Count == 0
                ? node.Width
                : node.Width + HorizontalGap + node.Children.Max(TreeWidth);

        private static void Place(LayoutNode node, float x, float top, float span)
        {
            node.X = x;
            node.Y = top + (span - node.Height) / 2;
            if (node.Children.Count == 0) return;

            var childrenHeight = node.Children.Sum(child => child.TreeHeight) + (node.Children.Count - 1) * VerticalGap;
            var childTop = top + (span - childrenHeight) / 2;
            foreach (var child in node.Children)
            {
                Place(child, x + node.Width + HorizontalGap, childTop, child.TreeHeight);
                childTop += child.TreeHeight + VerticalGap;
            }
        }

        private static void DrawConnections(SKCanvas canvas, LayoutNode node)
        {
            var color = Palette[Math.Min(node.Depth, Palette.Length - 1)].WithAlpha(115);
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.2f
            };

            foreach (var child in node.Children)
            {
                var startX = node.X + node.Width;
                var startY = node.Y + node.Height / 2;
                var endX = child.X;
                var endY = child.Y + child.Height / 2;
                var middleX = (startX + endX) / 2;
                using var path = new SKPath();
                path.MoveTo(startX, startY);
                path.CubicTo(middleX, startY, middleX, endY, endX, endY);
                canvas.DrawPath(path, paint);
                DrawConnections(canvas, child);
            }
        }

        private static void DrawNodes(SKCanvas canvas, LayoutNode node, SKTypeface typeface)
        {
            var color = Palette[Math.Min(node.Depth, Palette.Length - 1)];
            var rectangle = new SKRect(node.X, node.Y, node.X + node.Width, node.Y + node.Height);
            using var fillPaint = new SKPaint
            {
                Color = color.WithAlpha(28),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            using var borderPaint = new SKPaint
            {
                Color = color.WithAlpha(210),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.8f
            };
            canvas.DrawRoundRect(rectangle, CornerRadius, CornerRadius, fillPaint);
            canvas.DrawRoundRect(rectangle, CornerRadius, CornerRadius, borderPaint);

            var textSize = node.Depth == 0 ? RootFontSize : FontSize;
            using var textPaint = new SKPaint
            {
                Color = color,
                Typeface = typeface,
                TextSize = textSize,
                IsAntialias = true,
                FakeBoldText = node.Depth == 0
            };
            var metrics = textPaint.FontMetrics;
            var baseline = node.Y + (node.Height - metrics.Descent - metrics.Ascent) / 2;
            canvas.DrawText(Truncate(node.Data.Title), node.X + NodePaddingX, baseline, textPaint);

            foreach (var child in node.Children)
                DrawNodes(canvas, child, typeface);
        }

        private static string Truncate(string value)
        {
            var normalized = NormalizeSingleLine(value);
            var truncated = TruncateTextElements(normalized, MaxTextLength);
            return truncated.Length < normalized.Length
                ? TruncateTextElements(normalized, MaxTextLength - 1) + "…"
                : truncated;
        }
    }
}
