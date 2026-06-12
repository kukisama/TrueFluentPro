using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;
using TrueFluentPro.ViewModels.EndpointTesting;
using TrueFluentPro.Views;
using TrueFluentPro.Views.EndpointTesting;
using FaIcon = Projektanker.Icons.Avalonia.Icon;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class EndpointsSection : UserControl
{
    private EndpointsSectionVM? _boundVm;

    public EndpointsSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is EndpointsSectionVM vm)
        {
            WireHandlers(vm);
            SyncAadLoginPanel(vm);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        UnwireHandlers();
        base.OnUnloaded(e);
    }

    private void SyncAadLoginPanel(EndpointsSectionVM settings)
    {
        var ep = settings.SelectedEndpoint;
        ResetTransientUiState();
        if (ep == null) return;

        EndpointAadLoginPanel.ProfileKey = $"endpoint_{ep.Id}";
        EndpointAadLoginPanel.TenantId = ep.AzureTenantId;
        EndpointAadLoginPanel.ClientId = ep.AzureClientId;
    }

    private void OpenUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开浏览器失败时静默忽略，避免影响设置界面
            }
        }
    }

    private async void CreateEndpoint_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointsSectionVM vm)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
            return;

        var dialog = new EndpointCreateDialog(vm.EndpointTypeOptions);
        var result = await dialog.ShowDialog<EndpointCreateDialogResult?>(owner);
        if (result == null)
            return;

        vm.CreateEndpoint(result.EndpointName, result.EndpointType);
    }

    private async void CopyApiKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointsSectionVM { SelectedEndpoint: { } endpoint } vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(endpoint.ApiKey ?? string.Empty);
        vm.NotifyStatus("已复制 API 密钥");
    }

    private async void ShowEndpointInfo_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointsSectionVM { SelectedEndpoint: { } endpoint } vm)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
            return;

        EndpointInfoDialog dialog;
        try
        {
            var inspectionModel = vm.GetSelectedEndpointInspectionModel();
            dialog = inspectionModel == null
                ? new EndpointInfoDialog($"{endpoint.Name} · 详细信息", vm.GetSelectedEndpointInspectionDetails())
                : new EndpointInfoDialog($"{endpoint.Name} · 详细信息", inspectionModel);
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Write("EndpointsSection.ShowEndpointInfo_Click", ex, isTerminating: false);
            dialog = new EndpointInfoDialog(
                $"{endpoint.Name} · 详细信息（降级显示）",
                $"终结点详细信息\n\n结构化详情初始化失败，已自动降级为纯文本展示。\n\n{vm.GetSelectedEndpointInspectionDetails()}");
        }

        dialog.TestAllRequestedAsync = async () =>
        {
            var resultViewModel = new EndpointBatchTestDialogViewModel(endpoint.Name);
            var resultDialog = new EndpointBatchTestDialog(
                resultViewModel,
                (progress, cancellationToken) => vm.RunSelectedEndpointTestAsync(progress, cancellationToken));
            resultDialog.Show(owner);
            await Task.CompletedTask;
        };
        await dialog.ShowDialog(owner);
    }

    private void AddModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointsSectionVM vm) return;
        var model = vm.AddBlankModelToSelectedEndpoint();
        if (model != null)
        {
            ExpandAndFocusModel(model, true);
        }
    }

    private void AddDiscoveredModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string modelId } || DataContext is not EndpointsSectionVM vm)
            return;

        var model = vm.AddDiscoveredModelToSelectedEndpoint(modelId);
        if (model != null)
        {
            ExpandAndFocusModel(model, true);
        }
    }

    private void RemoveModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AiModelEntry model &&
            DataContext is EndpointsSectionVM vm)
        {
            vm.RemoveModelFromSelectedEndpoint(model);
        }
    }

    private void CapTogglePanel_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel panel) return;
        if (panel.Tag is not AiModelEntry model) return;
        RefreshCapToggleState(panel, model);
        UpdateHeaderCapIcon(panel, model);
    }

    private void CapToggle_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        var panel = toggle.Parent as StackPanel;
        if (panel?.Tag is not AiModelEntry model) return;

        // 单选：取消其他 toggle
        foreach (var other in panel.Children.OfType<ToggleButton>())
        {
            if (other != toggle) other.IsChecked = false;
        }

        var tag = toggle.Tag?.ToString();
        var capability = ParseCapability(tag);

        if (DataContext is EndpointsSectionVM { SelectedEndpoint: { } endpoint }
            && capability != ModelCapability.None
            && !EndpointCapabilityPolicyResolver.IsCapabilityAllowed(endpoint.ProfileId, endpoint.EndpointType, capability))
        {
            toggle.IsChecked = false;
            capability = ModelCapability.None;
        }

        model.Capabilities = capability;
        UpdateHeaderCapIcon(panel, model);

        if (DataContext is EndpointsSectionVM vm)
            vm.NotifyModelChanged();
    }

    private void CapToggle_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        var panel = toggle.Parent as StackPanel;
        if (panel?.Tag is not AiModelEntry model) return;

        // 如果全部取消选中，设为 None
        var anyChecked = panel.Children.OfType<ToggleButton>().Any(t => t.IsChecked == true);
        if (!anyChecked)
        {
            model.Capabilities = ModelCapability.None;
            UpdateHeaderCapIcon(panel, model);
            if (DataContext is EndpointsSectionVM vm)
                vm.NotifyModelChanged();
        }
    }

    private void RefreshCapToggleState(StackPanel panel, AiModelEntry model)
    {
        if (DataContext is not EndpointsSectionVM { SelectedEndpoint: { } endpoint })
            return;

        var allowed = EndpointCapabilityPolicyResolver.GetAllowedCapabilities(endpoint.ProfileId, endpoint.EndpointType)
            .ToHashSet();

        foreach (var toggle in panel.Children.OfType<ToggleButton>())
        {
            var tag = toggle.Tag?.ToString() ?? "";
            var cap = ParseCapability(tag);
            toggle.IsEnabled = cap == ModelCapability.None || allowed.Contains(cap);
            toggle.IsChecked = model.Capabilities == cap && cap != ModelCapability.None;
        }

        if (model.Capabilities != ModelCapability.None && !allowed.Contains(model.Capabilities))
        {
            model.Capabilities = ModelCapability.None;
            if (DataContext is EndpointsSectionVM vm)
                vm.NotifyModelChanged();
        }
    }

    private void UpdateHeaderCapIcon(StackPanel capPanel, AiModelEntry model)
    {
        // CapTogglePanel → Grid → ModelDetailPanel → StackPanel → Border(ModelItemRoot)
        var modelRoot = capPanel.Parent?.Parent?.Parent?.Parent as Border;
        if (modelRoot == null) return;
        var headerBorder = modelRoot.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "ModelHeaderBorder");
        var icon = headerBorder?.GetVisualDescendants().OfType<FaIcon>()
            .FirstOrDefault(i => i.Name == "ModelCapIcon");
        if (icon == null) return;

        var (value, color) = model.Capabilities switch
        {
            ModelCapability.Text => ("fa-solid fa-font", "#8AADA0"),
            ModelCapability.Image => ("fa-solid fa-image", "#B09AAC"),
            ModelCapability.Video => ("fa-solid fa-video", "#8FA4B8"),
            ModelCapability.SpeechToText => ("fa-solid fa-microphone", "#B5A88A"),
            ModelCapability.TextToSpeech => ("fa-solid fa-volume-high", "#9A92AD"),
            _ => ("fa-solid fa-caret-right", "")
        };

        icon.Value = value;
        icon.Foreground = string.IsNullOrEmpty(color)
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    private static ModelCapability ParseCapability(string? tag) => tag switch
    {
        "Text" => ModelCapability.Text,
        "Image" => ModelCapability.Image,
        "Video" => ModelCapability.Video,
        "SpeechToText" => ModelCapability.SpeechToText,
        "TextToSpeech" => ModelCapability.TextToSpeech,
        _ => ModelCapability.None,
    };

    // ═══ 模型手风琴（同一时间仅展开一个） ═══

    private StackPanel? _expandedModelPanel;
    private Border? _expandedModelHeader;

    private void ModelHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border header) return;

        var parent = header.Parent as StackPanel;
        var detailPanel = parent?.Children.OfType<StackPanel>()
            .FirstOrDefault(p => p.Name == "ModelDetailPanel");

        if (detailPanel == null) return;

        if (detailPanel.IsVisible)
        {
            detailPanel.IsVisible = false;
            SetExpandIcon(header, false);
            _expandedModelPanel = null;
            _expandedModelHeader = null;
        }
        else
        {
            ExpandModelPanel(detailPanel, header, true);
        }
    }

    public void ResetTransientUiState()
    {
    }

    private void EndpointsListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        if (DataContext is not EndpointsSectionVM vm)
            return;

        var visual = e.Source as Control;
        var listBoxItem = visual?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (listBoxItem?.DataContext is not AiEndpoint endpoint)
            return;

        vm.SelectedEndpoint = endpoint;

        var flyout = new MenuFlyout();

        var editItem = new MenuItem { Header = "编辑" };
        foreach (var option in vm.EndpointTypeOptions)
        {
            var typeItem = new MenuItem
            {
                Header = option.DisplayName,
                IsEnabled = option.Type != endpoint.EndpointType
            };
            typeItem.Click += (_, _) => vm.ChangeEndpointType(endpoint, option.Type);
            editItem.Items.Add(typeItem);
        }

        flyout.Items.Add(editItem);
        flyout.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "删除" };
        deleteItem.Click += (_, _) => vm.RemoveEndpoint(endpoint);
        flyout.Items.Add(deleteItem);

        flyout.ShowAt(EndpointsListBox, true);
        e.Handled = true;
    }

    private void ExpandAndFocusModel(AiModelEntry model, bool focusModelId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var root = this.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Name == "ModelItemRoot" && ReferenceEquals(border.Tag, model));
            if (root == null)
                return;

            var detailPanel = root.GetVisualDescendants()
                .OfType<StackPanel>()
                .FirstOrDefault(panel => panel.Name == "ModelDetailPanel");
            var header = root.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Name == "ModelHeaderBorder");

            if (detailPanel == null || header == null)
                return;

            ExpandModelPanel(detailPanel, header, focusModelId);
        }, DispatcherPriority.Background);
    }

    private void ExpandModelPanel(StackPanel detailPanel, Border header, bool focusModelId)
    {
        if (_expandedModelPanel != null && _expandedModelPanel != detailPanel)
        {
            _expandedModelPanel.IsVisible = false;
            if (_expandedModelHeader != null)
                SetExpandIcon(_expandedModelHeader, false);
        }

        detailPanel.IsVisible = true;
        SetExpandIcon(header, true);
        _expandedModelPanel = detailPanel;
        _expandedModelHeader = header;

        if (focusModelId)
        {
            var modelIdTextBox = detailPanel.Children
                .OfType<Grid>()
                .SelectMany(grid => grid.Children.OfType<TextBox>())
                .FirstOrDefault(textBox => textBox.Name == "ModelIdTextBox");
            if (modelIdTextBox != null)
            {
                modelIdTextBox.Focus();
                modelIdTextBox.SelectAll();
            }
        }
    }

    private static void SetExpandIcon(Border header, bool expanded)
    {
        if (header.Child is not Grid grid) return;
        var icon = grid.Children.OfType<FaIcon>()
            .FirstOrDefault(i => i.Name == "ModelCapIcon");
        if (icon == null) return;

        // 如果当前显示的是能力图标（有颜色），保持不变
        if (header.Tag is AiModelEntry model && model.Capabilities != ModelCapability.None)
            return;

        icon.Value = expanded ? "fa-solid fa-caret-down" : "fa-solid fa-caret-right";
    }

    private void WireHandlers(EndpointsSectionVM vm)
    {
        if (ReferenceEquals(_boundVm, vm))
            return;

        UnwireHandlers();
        _boundVm = vm;

        vm.PropertyChanged += Settings_PropertyChanged;
        EndpointAadLoginPanel.LoginCompleted += EndpointAadLoginPanel_LoginCompleted;
    }

    private void UnwireHandlers()
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= Settings_PropertyChanged;
        }

        EndpointAadLoginPanel.LoginCompleted -= EndpointAadLoginPanel_LoginCompleted;
        _boundVm = null;
    }

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm != null && args.PropertyName is nameof(EndpointsSectionVM.SelectedEndpoint)
            or nameof(EndpointsSectionVM.SelectedEndpointAuthMode)
            or nameof(EndpointsSectionVM.SelectedEndpointTypeSummary))
        {
            SyncAadLoginPanel(_boundVm);
            RefreshVisibleCapabilityCombos();
        }
    }

    private void RefreshVisibleCapabilityCombos()
    {
        foreach (var panel in this.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "CapTogglePanel"))
        {
            if (panel.Tag is AiModelEntry model)
            {
                RefreshCapToggleState(panel, model);
                UpdateHeaderCapIcon(panel, model);
            }
        }
    }

    private void EndpointAadLoginPanel_LoginCompleted()
    {
        var vm = _boundVm;
        if (vm == null)
            return;

        var ep = vm.SelectedEndpoint;
        if (ep == null)
            return;

        var newTenant = EndpointAadLoginPanel.TenantId;
        if (!string.IsNullOrWhiteSpace(newTenant) && newTenant != ep.AzureTenantId)
        {
            ep.AzureTenantId = newTenant;
            vm.NotifyEndpointChanged();
        }
    }
}
