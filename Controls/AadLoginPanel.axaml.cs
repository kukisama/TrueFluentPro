using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

namespace TrueFluentPro.Controls
{
    public partial class AadLoginPanel : UserControl
    {
        public static readonly StyledProperty<string> ProfileKeyProperty =
            AvaloniaProperty.Register<AadLoginPanel, string>(nameof(ProfileKey), "ai");

        public static readonly StyledProperty<bool> HideTenantClientFieldsProperty =
            AvaloniaProperty.Register<AadLoginPanel, bool>(nameof(HideTenantClientFields), false);

        public string ProfileKey
        {
            get => GetValue(ProfileKeyProperty);
            set => SetValue(ProfileKeyProperty, value);
        }

        /// <summary>为 true 时隐藏面板内置的 Tenant/Client ID 字段（由外部控件提供）</summary>
        public bool HideTenantClientFields
        {
            get => GetValue(HideTenantClientFieldsProperty);
            set => SetValue(HideTenantClientFieldsProperty, value);
        }

        public string? TenantId
        {
            get => TenantIdTextBox.Text?.Trim();
            set => TenantIdTextBox.Text = value;
        }

        public string? ClientId
        {
            get => ClientIdTextBox.Text?.Trim();
            set => ClientIdTextBox.Text = value;
        }

        /// <summary>登录完成后触发（可能更新了 TenantId）</summary>
        public event Action? LoginCompleted;

        private string? _lastDeviceCodeUrl;
        private CancellationTokenSource? _statusRefreshCts;

        public AadLoginPanel()
        {
            InitializeComponent();
            LoginButton.Click += LoginButton_Click;
            LogoutButton.Click += LogoutButton_Click;
            CopyCodeButton.Click += CopyCodeButton_Click;
            OpenLinkButton.Click += OpenLinkButton_Click;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == HideTenantClientFieldsProperty)
            {
                var hide = (bool)(change.NewValue ?? false);
                TenantIdPanel.IsVisible = !hide;
                ClientIdPanel.IsVisible = !hide;
            }

            if (change.Property == ProfileKeyProperty)
            {
                _ = RefreshLoginStatusAsync();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _ = RefreshLoginStatusAsync();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _statusRefreshCts?.Cancel();
            _statusRefreshCts?.Dispose();
            _statusRefreshCts = null;
            base.OnDetachedFromVisualTree(e);
        }

        public void SetStatus(string text, string colorHex)
        {
            StatusTextBlock.Text = text;
            StatusTextBlock.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(colorHex));
        }

        private async void LoginButton_Click(object? sender, RoutedEventArgs e)
        {
            LoginButton.IsEnabled = false;
            SetStatus("正在启动登录…", "#6a737d");
            DeviceCodePanel.IsVisible = false;
            _lastDeviceCodeUrl = null;

            var providerStore = App.Services.GetRequiredService<IAzureTokenProviderStore>();
            var provider = providerStore.GetProvider(ProfileKey);
            var tenantId = TenantIdTextBox.Text?.Trim();
            var clientId = ClientIdTextBox.Text?.Trim();

            Action<string> onDeviceCode = message =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    DeviceCodePanel.IsVisible = true;
                    DeviceCodeMessage.Text = message;

                    var urlMatch = Regex.Match(message, @"https?://\S+");
                    if (urlMatch.Success)
                        _lastDeviceCodeUrl = urlMatch.Value;
                });
            };

            try
            {
                var success = await provider.LoginAutoAsync(tenantId, clientId, onDeviceCode);
                if (!success)
                {
                    SetStatus("✗ 登录失败", "#cb2431");
                    return;
                }

                SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}", "#22863a");
                DeviceCodePanel.IsVisible = false;

                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}（正在获取租户列表…）", "#22863a");
                    IReadOnlyList<AzureTenantInfo> tenants;
                    try
                    {
                        tenants = await provider.GetAvailableTenantsAsync();
                    }
                    catch
                    {
                        tenants = Array.Empty<AzureTenantInfo>();
                    }

                    if (tenants.Count == 1)
                    {
                        TenantIdTextBox.Text = tenants[0].TenantId;
                        SetStatus($"正在切换到租户: {tenants[0].TenantId}…", "#6a737d");
                        var switched = await provider.LoginAutoAsync(tenants[0].TenantId, clientId, onDeviceCode);
                        if (switched)
                        {
                            SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}（租户: {tenants[0].TenantId}）", "#22863a");
                            DeviceCodePanel.IsVisible = false;
                        }
                        else
                        {
                            SetStatus("✗ 切换租户失败（请检查权限/管理员同意/条件访问策略）", "#cb2431");
                        }
                    }
                    else if (tenants.Count > 1)
                    {
                        var parentWindow = TopLevel.GetTopLevel(this) as Window;
                        var picked = parentWindow != null
                            ? await TenantSelectionView.ShowAsync(parentWindow, tenants)
                            : null;

                        if (picked != null && !string.IsNullOrWhiteSpace(picked.TenantId))
                        {
                            TenantIdTextBox.Text = picked.TenantId;
                            SetStatus($"正在切换到租户: {picked.DisplayName ?? picked.TenantId}…", "#6a737d");
                            var switched = await provider.LoginAutoAsync(picked.TenantId, clientId, onDeviceCode, forceInteractive: true);
                            if (switched)
                            {
                                SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}（租户: {picked.TenantId}）", "#22863a");
                                DeviceCodePanel.IsVisible = false;
                            }
                            else
                            {
                                SetStatus("✗ 切换租户失败（请检查权限/管理员同意/条件访问策略）", "#cb2431");
                            }
                        }
                        else
                        {
                            SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}（未选择租户）", "#22863a");
                        }
                    }
                    else
                    {
                        SetStatus($"✓ 已登录: {provider.Username ?? "已认证"}（未能获取租户列表，可手动填写租户 ID）", "#22863a");
                    }
                }

                LoginCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                SetStatus($"✗ 错误: {ex.Message}", "#cb2431");
            }
            finally
            {
                LoginButton.IsEnabled = true;
            }
        }

        private void LogoutButton_Click(object? sender, RoutedEventArgs e)
        {
            var providerStore = App.Services.GetRequiredService<IAzureTokenProviderStore>();
            var provider = providerStore.GetProvider(ProfileKey);
            provider.Logout();
            SetStatus("已注销（认证记录与缓存已清除）", "#6a737d");
            DeviceCodePanel.IsVisible = false;
        }

        private async void CopyCodeButton_Click(object? sender, RoutedEventArgs e)
        {
            var text = DeviceCodeMessage.Text ?? "";
            var codeMatch = Regex.Match(text, @"\b[A-Z0-9]{8,}\b");
            if (codeMatch.Success)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(codeMatch.Value);
            }
        }

        private void OpenLinkButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastDeviceCodeUrl))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _lastDeviceCodeUrl,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private async Task RefreshLoginStatusAsync()
        {
            _statusRefreshCts?.Cancel();
            _statusRefreshCts?.Dispose();
            _statusRefreshCts = new CancellationTokenSource();
            var token = _statusRefreshCts.Token;

            try
            {
                var providerStore = App.Services.GetRequiredService<IAzureTokenProviderStore>();
                var provider = providerStore.GetProvider(ProfileKey);
                var tenantId = TenantIdTextBox.Text?.Trim();
                var clientId = ClientIdTextBox.Text?.Trim();

                SetStatus("正在检查登录状态…", "#6a737d");

                var authenticated = await providerStore.GetAuthenticatedProviderAsync(
                    ProfileKey,
                    tenantId,
                    clientId,
                    token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (authenticated != null)
                {
                    SetStatus($"✓ 已登录: {authenticated.Username ?? provider.Username ?? "已认证"}", "#22863a");
                }
                else
                {
                    SetStatus("未登录", "#6a737d");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                SetStatus("未登录", "#6a737d");
            }
        }
    }
}
