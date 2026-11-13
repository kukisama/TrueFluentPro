using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public partial class AudioLabViewModel
    {
        private int _audioTitleRefreshVersion;
        private bool _libraryDisposed;

        private void OnRecordingTitleChanged(string audioItemId)
            => Dispatcher.UIThread.Post(() =>
            {
                if (!_libraryDisposed) _ = RefreshAudioDisplayNamesAsync();
            });

        private async Task RefreshAudioDisplayNamesAsync()
        {
            var version = Interlocked.Increment(ref _audioTitleRefreshVersion);
            try
            {
                var files = await Dispatcher.UIThread.InvokeAsync(() => _audioFiles.ToArray());
                var titles = await Task.Run(() => files.Select(file =>
                    (File: file, Title: _pipeline.GetAudioDisplayName(file.FullPath))).ToArray());
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_libraryDisposed || version != _audioTitleRefreshVersion) return;
                    foreach (var (file, title) in titles)
                    {
                        file.AudioTitle = title;
                        if (string.Equals(file.FullPath, CurrentFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            CurrentFileName = file.AudioDisplayName;
                            BreadcrumbText = $"听析中心 / {CurrentFileName}";
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // 显示名称失败不影响加载和转录；仍可通过原文件名访问。
                System.Diagnostics.Debug.WriteLine($"刷新录音标题失败：{ex.Message}");
            }
        }

        private BatchSubtitleSplitOptions GetTranscriptSplitOptions()
        {
            var config = _configProvider();
            return new BatchSubtitleSplitOptions
            {
                EnableSentenceSplit = config.EnableBatchSubtitleSentenceSplit,
                SplitOnComma = config.BatchSubtitleSplitOnComma,
                MaxChars = config.BatchSubtitleMaxChars,
                MaxDurationSeconds = config.BatchSubtitleMaxDurationSeconds,
                PauseSplitMs = config.BatchSubtitlePauseSplitMs
            };
        }

        private static List<TranscriptSegment> LoadStoredTranscript(string json, BatchSubtitleSplitOptions options)
        {
            if (!TranscriptionDataHelper.IsRawFormat(json))
                return TranscriptionDataHelper.DeserializeLegacySegments(json);
            var raw = TranscriptionDataHelper.ParseRawData(json);
            return raw == null ? new() : TranscriptionDataHelper.ComputeSegments(raw, options);
        }

        /// <summary>只读导出全库已有转录及总结；不加载音频、不提交任务、不要求源文件存在。</summary>
        public async Task<AudioLabExportResult?> ExportAllTranscriptsAsync(
            string exportDirectory, CancellationToken cancellationToken = default)
        {
            if (IsExporting || _libraryDisposed) return null;
            IsExporting = true;
            HasExportFeedback = false;
            StatusMessage = "正在读取全部已有转录...";
            var exported = new List<string>();
            var count = 0;
            var skipped = 0;
            var failed = 0;
            try
            {
                var options = GetTranscriptSplitOptions();
                var records = await Task.Run(() => _pipeline.ListStoredTranscripts(), cancellationToken);
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<TranscriptSegment> segments;
                    try
                    {
                        segments = await Task.Run(() => LoadStoredTranscript(record.TranscriptJson!, options)
                            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).ToList(), cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { skipped++; continue; }
                    if (segments.Count == 0) { skipped++; continue; }

                    var fileName = string.IsNullOrWhiteSpace(record.Audio.FileName)
                        ? Path.GetFileName(record.Audio.FilePath) : record.Audio.FileName;
                    var title = AudioRecordingTitle.Build(fileName, record.Audio.CreatedAt, record.SummaryMarkdown);
                    var transcript = AudioLabExportService.BuildTranscriptMarkdown(title, fileName, segments);
                    if (record.IsStale)
                        transcript += "\n> 注意：这是已保存的历史内容，转录或总结已标记为过期。\n";
                    var artifacts = new List<AudioLabExportArtifact>
                    {
                        new() { Kind = AudioLabExportArtifactKind.Markdown, Label = "录音稿", MarkdownContent = transcript }
                    };
                    if (!string.IsNullOrWhiteSpace(record.SummaryMarkdown))
                        artifacts.Add(new() { Kind = AudioLabExportArtifactKind.Markdown, Label = "总结",
                            MarkdownContent = record.SummaryMarkdown + (record.IsStale ? "\n\n> 注意：这是已标记为过期的历史内容。\n" : "") });
                    try
                    {
                        var result = await _exportService.ExportAsync(exportDirectory, new AudioLabExportRequest
                        {
                            SourceAudioPath = string.IsNullOrWhiteSpace(record.Audio.FilePath) ? fileName : record.Audio.FilePath,
                            FileNamePrefix = title,
                            Artifacts = artifacts
                        }, cancellationToken);
                        exported.AddRange(result.ExportedFiles);
                        count++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { failed++; }
                    StatusMessage = $"正在导出：已完成 {count} 条，跳过 {skipped} 条，失败 {failed} 条...";
                }

                StatusMessage = records.Count == 0 ? "没有已保存的转录可导出。"
                    : $"导出完成：{count} 条转录，{exported.Count} 个文件；跳过 {skipped} 条无效记录，失败 {failed} 条。目录：{exportDirectory}";
                if (exported.Count > 0)
                {
                    try { _folderLauncher.Open(exportDirectory); }
                    catch { StatusMessage += "（无法自动打开目录，请手动查看。）"; }
                }
                return new AudioLabExportResult(exportDirectory, exported);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"导出已取消，已保留 {count} 条导出的记录。";
                return null;
            }
            catch (Exception ex)
            {
                StatusMessage = $"批量导出失败：{ex.Message}；已保留 {count} 条导出的记录。";
                return null;
            }
            finally
            {
                IsExporting = false;
                HasExportFeedback = true;
            }
        }
    }
}