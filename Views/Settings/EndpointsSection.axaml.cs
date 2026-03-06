using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
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
        if (DataContext is EndpointsSectionVM vm)
            vm.NotifyModelChanged();
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
            ExpandModelPanel(detailPanel, header, true);
        }
    }

    public void ResetTransientUiState()
    {
        if (EpShowKeyCheckBox != null)
        {
            EpShowKeyCheckBox.IsChecked = false;
        }
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
        if (header.Child is Grid grid)
        {
            var icon = grid.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "ModelExpandIcon");
            if (icon != null)
                icon.Text = expanded ? "▼" : "▶";
        }
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
        if (args.PropertyName == nameof(EndpointsSectionVM.SelectedEndpoint) && _boundVm != null)
        {
            SyncAadLoginPanel(_boundVm);
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
