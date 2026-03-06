using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views.Settings;

public partial class EndpointsSection : UserControl
{
    public EndpointsSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel vm)
        {
            // 监听终结点选择变化，同步 AadLoginPanel
            vm.Settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.SelectedEndpoint))
                    SyncAadLoginPanel(vm.Settings);
            };
            SyncAadLoginPanel(vm.Settings);

            // AAD 登录完成后同步 TenantId 回终结点模型
            EndpointAadLoginPanel.LoginCompleted += () =>
            {
                var ep = vm.Settings.SelectedEndpoint;
                if (ep == null) return;
                var newTenant = EndpointAadLoginPanel.TenantId;
                if (!string.IsNullOrWhiteSpace(newTenant) && newTenant != ep.AzureTenantId)
                {
                    ep.AzureTenantId = newTenant;
                    vm.Settings.NotifyModelChanged();
                }

                _ = vm.Settings.RefreshAiAuthStatusAsync();
            };
        }
    }

    private void SyncAadLoginPanel(SettingsViewModel settings)
    {
        var ep = settings.SelectedEndpoint;
        if (ep == null) return;

        EndpointAadLoginPanel.ProfileKey = $"endpoint_{ep.Id}";
        EndpointAadLoginPanel.TenantId = ep.AzureTenantId;
        EndpointAadLoginPanel.ClientId = ep.AzureClientId;
    }

    private void EndpointField_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Settings.NotifyEndpointChanged();
    }

    private void AddModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Settings.AddModelToSelectedEndpoint(
            "new-model", "", "", "", ModelCapability.Text);
    }

    private void RemoveModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AiModelEntry model &&
            DataContext is MainWindowViewModel vm)
        {
            vm.Settings.RemoveModelFromSelectedEndpoint(model);
        }
    }

    private void ModelField_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Settings.NotifyModelChanged();
    }

    private void ModelCapabilityCombo_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not AiModelEntry model) return;
        combo.SelectedIndex = model.Capabilities switch
        {
            ModelCapability.Image => 1,
            ModelCapability.Video => 2,
            _ => 0,
        };
    }

    private void ModelCapability_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not AiModelEntry model) return;
        if (combo.SelectedItem is not ComboBoxItem selected) return;
        var tag = selected.Tag?.ToString();
        model.Capabilities = tag switch
        {
            "Image" => ModelCapability.Image,
            "Video" => ModelCapability.Video,
            _ => ModelCapability.Text,
        };
        if (DataContext is MainWindowViewModel vm)
            vm.Settings.NotifyModelChanged();
    }

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
        }
    }

    private static void SetExpandIcon(Border header, bool expanded)
    {
        if (header.Child is Grid grid)
        {
            var icon = grid.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "ModelExpandIcon");
            if (icon != null)
                icon.Text = expanded ? "▼" : "▶";
        }
    }
}
