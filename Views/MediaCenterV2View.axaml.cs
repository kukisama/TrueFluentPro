using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    /// <summary>
    /// 媒体中心 v2 —— 创作者工作区视图。
    /// </summary>
    public partial class MediaCenterV2View : UserControl
    {
        private MediaCenterV2ViewModel? _viewModel;
        private bool _initialized;

        public MediaCenterV2View()
        {
            InitializeComponent();
        }

        public void Initialize(
            AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver, IAzureTokenProviderStore azureTokenProviderStore)
        {
            if (_initialized) return;
            _initialized = true;

            _viewModel = new MediaCenterV2ViewModel(aiConfig, genConfig, endpoints, modelRuntimeResolver, azureTokenProviderStore);
            DataContext = _viewModel;

            var workspaceScroll = this.FindControl<ScrollViewer>("WorkspaceScrollViewer");
            if (workspaceScroll != null)
            {
                workspaceScroll.ScrollChanged += WorkspaceScrollViewer_ScrollChanged;
                // 初始 10 条可能不够撑满视口，布局完成后补充加载
                workspaceScroll.LayoutUpdated += WorkspaceScrollViewer_InitialFill;
            }

            AddHandler(
                InputElement.KeyDownEvent,
                PromptTextBox_KeyDown,
                RoutingStrategies.Tunnel);

            AddHandler(
                InputElement.KeyDownEvent,
                ArrowKeys_KeyDown,
                RoutingStrategies.Tunnel);
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints)
        {
            _viewModel?.UpdateConfiguration(aiConfig, genConfig, endpoints);
        }

        public void Cleanup()
        {
            _viewModel?.Dispose();
        }

        private void WorkspaceScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv || _viewModel is not { HasMoreWorkspaces: true }) return;
            if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 200)
            {
                _viewModel.LoadMoreWorkspaces();
            }
        }

        private void WorkspaceScrollViewer_InitialFill(object? sender, EventArgs e)
        {
            if (sender is not ScrollViewer sv || _viewModel is not { HasMoreWorkspaces: true }) return;

            if (sv.Extent.Height <= sv.Viewport.Height && sv.Extent.Height > 0)
            {
                _viewModel.LoadMoreWorkspaces();
            }
            else if (sv.Extent.Height > sv.Viewport.Height)
            {
                sv.LayoutUpdated -= WorkspaceScrollViewer_InitialFill;
            }
        }

        private async void PromptTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (await TryAttachReferenceImageFromClipboardAsync())
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (_viewModel?.SubmitPromptCommand.CanExecute(null) == true)
                {
                    _viewModel.SubmitPromptCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void ArrowKeys_KeyDown(object? sender, KeyEventArgs e)
        {
            // 输入框内不拦截方向键
            if (e.Source is TextBox) return;
            if (_viewModel == null) return;

            switch (e.Key)
            {
                case Key.Left:
                    if (_viewModel.CanSelectPreviousAsset)
                    {
                        _viewModel.SelectPreviousAssetCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                case Key.Right:
                    if (_viewModel.CanSelectNextAsset)
                    {
                        _viewModel.SelectNextAssetCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                case Key.Up:
                    _viewModel.SelectAdjacentWorkspaceTab(-1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    _viewModel.SelectAdjacentWorkspaceTab(1);
                    e.Handled = true;
                    break;
            }
        }

        private async void AttachReferenceImage_Click(object? sender, RoutedEventArgs e)
        {
            if (_viewModel?.WorkspaceSession == null)
            {
                return;
            }

            if (!_viewModel.CanAttachReferenceImages)
            {
                _viewModel.WorkspaceSession.StatusText = _viewModel.IsVideoKind
                    ? "视频模式最多仅支持 1 张参考图"
                    : "当前参考图数量已达上限";
                return;
            }

            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择参考图",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    continue;
                }

                await _viewModel.WorkspaceSession.SetReferenceImageFromFileAsync(localPath);
            }
        }

        private async Task<bool> TryAttachReferenceImageFromClipboardAsync()
        {
            if (_viewModel?.WorkspaceSession == null)
            {
                return false;
            }

            if (!_viewModel.CanAttachReferenceImages)
            {
                _viewModel.WorkspaceSession.StatusText = _viewModel.IsVideoKind
                    ? "视频模式最多仅支持 1 张参考图"
                    : "当前参考图数量已达上限";
                return false;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                return false;
            }

            using (var bitmap = await clipboard.TryGetBitmapAsync())
            {
                if (bitmap != null)
                {
                    var tempPngPath = Path.Combine(Path.GetTempPath(), $"refclip_{Guid.NewGuid():N}.png");
                    try
                    {
                        bitmap.Save(tempPngPath, 100);
                        return await _viewModel.WorkspaceSession.SetReferenceImageFromFileAsync(tempPngPath);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(tempPngPath))
                            {
                                File.Delete(tempPngPath);
                            }
                        }
                        catch
                        {
                            // ignore temp cleanup errors
                        }
                    }
                }
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var candidates = text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().Trim('"'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var attachedAny = false;
                foreach (var candidate in candidates)
                {
                    if (!File.Exists(candidate) || !IsImageFile(candidate))
                    {
                        continue;
                    }

                    attachedAny |= await _viewModel.WorkspaceSession.SetReferenceImageFromFileAsync(candidate);
                }

                if (attachedAny)
                {
                    return true;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                return await TryAttachReferenceImageFromWindowsClipboardBitmapAsync();
            }

            return false;
        }

        private async Task<bool> TryAttachReferenceImageFromWindowsClipboardBitmapAsync()
        {
            if (_viewModel?.WorkspaceSession == null)
            {
                return false;
            }

            IntPtr gdipToken = IntPtr.Zero;
            IntPtr gdipBitmap = IntPtr.Zero;
            string? tempPngPath = null;

            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    return false;
                }

                try
                {
                    var hBitmap = GetClipboardData(CF_BITMAP);
                    if (hBitmap == IntPtr.Zero)
                    {
                        return false;
                    }

                    var startupInput = new GdiplusStartupInput { GdiplusVersion = 1 };
                    if (GdiplusStartup(out gdipToken, ref startupInput, IntPtr.Zero) != 0)
                    {
                        return false;
                    }

                    if (GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out gdipBitmap) != 0)
                    {
                        return false;
                    }

                    tempPngPath = Path.Combine(Path.GetTempPath(), $"refclip_{Guid.NewGuid():N}.png");
                    var pngEncoderClsid = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");
                    if (GdipSaveImageToFile(gdipBitmap, tempPngPath, ref pngEncoderClsid, IntPtr.Zero) != 0)
                    {
                        return false;
                    }
                }
                finally
                {
                    CloseClipboard();
                }

                if (string.IsNullOrWhiteSpace(tempPngPath) || !File.Exists(tempPngPath))
                {
                    return false;
                }

                return await _viewModel.WorkspaceSession.SetReferenceImageFromFileAsync(tempPngPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] 从 Windows 剪贴板读取图片失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (gdipBitmap != IntPtr.Zero)
                {
                    GdipDisposeImage(gdipBitmap);
                }

                if (gdipToken != IntPtr.Zero)
                {
                    GdiplusShutdown(gdipToken);
                }

                if (!string.IsNullOrWhiteSpace(tempPngPath))
                {
                    try
                    {
                        File.Delete(tempPngPath);
                    }
                    catch
                    {
                        // ignore temp cleanup errors
                    }
                }
            }
        }

        private async void ReferenceImageThumbnail_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel?.WorkspaceSession == null)
                return;

            var session = _viewModel.WorkspaceSession;
            if (!session.IsVideoMode)
                return;

            if (sender is not Border border)
                return;

            if (border.DataContext is not string filePath || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            if (!session.TryGetCurrentVideoTargetSize(out var targetWidth, out var targetHeight))
            {
                session.StatusText = "当前视频参数组合无可用尺寸映射";
                return;
            }

            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow == null)
                return;

            var cropWindow = new ReferenceImageCropWindow(filePath, targetWidth, targetHeight);
            var result = await cropWindow.ShowDialog<bool>(parentWindow);
            if (!result)
                return;

            session.NotifyReferenceImageUpdated(filePath);
            session.StatusText = $"已将参考图裁切为 {targetWidth}×{targetHeight}";
            e.Handled = true;
        }

        private void PreviewSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel?.SelectedAsset == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            OpenSelectedAssetPreview();
            e.Handled = true;
        }

        private void PreviewNavButton_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
        }

        private void WorkspaceTabButton_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel == null || sender is not Control control || control.DataContext is not MediaWorkspaceTabViewModel tab)
            {
                return;
            }

            var point = e.GetCurrentPoint(control);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            {
                if (_viewModel.SelectWorkspaceTabCommand.CanExecute(tab))
                {
                    _viewModel.SelectWorkspaceTabCommand.Execute(tab);
                }
            }
        }

        private void WorkspaceTabButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right
                || _viewModel == null
                || sender is not Control control
                || control.DataContext is not MediaWorkspaceTabViewModel tab)
            {
                return;
            }

            if (_viewModel.SelectWorkspaceTabCommand.CanExecute(tab))
            {
                _viewModel.SelectWorkspaceTabCommand.Execute(tab);
            }

            ShowFlyout(control, BuildWorkspaceFlyout(tab));
            e.Handled = true;
        }

        private void RailThumbButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right
                || _viewModel == null
                || sender is not Control control
                || control.DataContext is not MediaCreatorResultAsset asset)
            {
                return;
            }

            if (_viewModel.SelectAssetCommand.CanExecute(asset))
            {
                _viewModel.SelectAssetCommand.Execute(asset);
            }

            var flyout = BuildAssetFlyout(asset);
            if (flyout.Items.Count > 0)
            {
                ShowFlyout(control, flyout);
            }

            e.Handled = true;
        }

        private MenuFlyout BuildWorkspaceFlyout(MediaWorkspaceTabViewModel tab)
        {
            var flyout = new MenuFlyout();

            // 编辑：取当前 workspace 最新的已完成资产，作为新创作的参考图起点
            var latestAsset = _viewModel?.ResultRailItems
                .FirstOrDefault(a => !a.IsPending && File.Exists(a.FilePath));
            if (latestAsset != null)
            {
                var editItem = new MenuItem { Header = latestAsset.IsVideo ? "用尾帧编辑" : "编辑图片" };
                editItem.Click += (_, _) =>
                {
                    if (_viewModel?.EditAssetCommand.CanExecute(latestAsset) == true)
                    {
                        _viewModel.EditAssetCommand.Execute(latestAsset);
                    }
                };
                flyout.Items.Add(editItem);
                flyout.Items.Add(new Separator());
            }

            var removeItem = new MenuItem { Header = "从列表移除" };
            ToolTip.SetTip(removeItem, "只从右侧列表移除，不删除原始图片、视频和目录文件");
            removeItem.Click += async (_, _) =>
            {
                var confirmed = await ConfirmRemoveWorkspaceAsync(tab);
                if (!confirmed)
                {
                    return;
                }

                if (_viewModel?.RemoveWorkspaceCommand.CanExecute(tab) == true)
                {
                    _viewModel.RemoveWorkspaceCommand.Execute(tab);
                }
            };
            flyout.Items.Add(removeItem);
            return flyout;
        }

        private MenuFlyout BuildAssetFlyout(MediaCreatorResultAsset asset)
        {
            var flyout = new MenuFlyout();

            if (!asset.IsPending && File.Exists(asset.FilePath))
            {
                var editItem = new MenuItem { Header = asset.IsVideo ? "用尾帧编辑" : "编辑此图片" };
                editItem.Click += (_, _) =>
                {
                    if (_viewModel?.EditAssetCommand.CanExecute(asset) == true)
                    {
                        _viewModel.EditAssetCommand.Execute(asset);
                    }
                };
                flyout.Items.Add(editItem);

                var openItem = new MenuItem { Header = "打开文件" };
                openItem.Click += (_, _) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = asset.FilePath,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                };
                flyout.Items.Add(openItem);

                if (File.Exists(asset.FilePath))
                {
                    var explorerItem = new MenuItem { Header = "在文件夹中显示" };
                    var filePath = asset.FilePath;
                    explorerItem.Click += (_, _) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"/select,\"{filePath}\"",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    };
                    flyout.Items.Add(explorerItem);
                }
            }

            return flyout;
        }

        private static void ShowFlyout(Control target, MenuFlyout flyout)
        {
            flyout.ShowAt(target, true);
        }

        private async Task<bool> ConfirmRemoveWorkspaceAsync(MediaWorkspaceTabViewModel tab)
        {
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow == null)
            {
                return false;
            }

            var dialog = new Window
            {
                Title = "确认移除会话",
                Width = 420,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false
            };

            var titleBlock = new TextBlock
            {
                Text = "要从右侧列表移除这个会话吗？",
                FontSize = 16,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Margin = new Thickness(16, 16, 16, 8)
            };

            var bodyBlock = new TextBlock
            {
                Text = $"会话：{tab.Title}\n\n这只会把它从列表里移除，下次启动也不会自动恢复；原始图片、视频和会话目录文件都会保留。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 0, 16, 12)
            };

            var removeButton = new Button
            {
                Content = "确认移除",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding = new Thickness(20, 6),
                IsDefault = true
            };

            var cancelButton = new Button
            {
                Content = "取消",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding = new Thickness(20, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };

            var result = false;
            removeButton.Click += (_, _) =>
            {
                result = true;
                dialog.Close();
            };
            cancelButton.Click += (_, _) => dialog.Close();

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(16, 0, 16, 16),
                Spacing = 8
            };
            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(removeButton);

            var stack = new StackPanel();
            stack.Children.Add(titleBlock);
            stack.Children.Add(bodyBlock);
            stack.Children.Add(buttonPanel);
            dialog.Content = stack;

            await dialog.ShowDialog(parentWindow);
            return result;
        }

        private void OpenSelectedAssetPreview()
        {
            if (_viewModel?.SelectedAsset == null)
            {
                return;
            }

            var selected = _viewModel.SelectedAsset;
            if (selected.IsVideo)
            {
                // 视频资产：用系统播放器打开
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = selected.FilePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaCenterV2] 打开系统播放器失败: {ex.Message}");
                }
                return;
            }

            var imagePaths = _viewModel.GetSelectedGroupImagePaths();
            if (imagePaths.Count == 0)
            {
                _viewModel.OpenSelectedAsset();
                return;
            }

            var index = imagePaths
                .Select((path, idx) => new { path, idx })
                .FirstOrDefault(x => string.Equals(x.path, selected.FilePath, StringComparison.OrdinalIgnoreCase))?.idx ?? 0;

            var previewWindow = new ImagePreviewWindow(imagePaths, index);
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow != null)
            {
                previewWindow.Show(parentWindow);
            }
            else
            {
                previewWindow.Show();
            }
        }

        // ──────────── 视频：调用系统播放器 ────────────

        private void PlayVideoButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var filePath = _viewModel?.SelectedAsset?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] 打开系统播放器失败: {ex.Message}");
            }
        }

        private static bool IsImageFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll")]
        private static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsidEncoder, IntPtr encoderParams);

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        private const uint CF_BITMAP = 2;
    }
}
