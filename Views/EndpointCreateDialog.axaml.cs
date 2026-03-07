using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using TrueFluentPro.Models;

namespace TrueFluentPro.Views;

public partial class EndpointCreateDialog : Window
{
    public EndpointCreateDialog()
    {
        InitializeComponent();
    }

    public EndpointCreateDialog(IEnumerable<EndpointTemplateDefinition> templates)
        : this()
    {
        var items = (templates ?? Enumerable.Empty<EndpointTemplateDefinition>()).ToList();
        TypeListBox.ItemsSource = items;
        TypeListBox.SelectedIndex = items.Count > 0 ? 0 : -1;

        CreateButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => Close(null);
    }

    private void Confirm()
    {
        if (TypeListBox.SelectedItem is not EndpointTemplateDefinition definition)
        {
            return;
        }

        Close(new EndpointCreateDialogResult
        {
            EndpointName = EndpointNameTextBox.Text?.Trim() ?? "",
            EndpointType = definition.Type
        });
    }
}

public sealed class EndpointCreateDialogResult
{
    public string EndpointName { get; init; } = "";
    public EndpointApiType EndpointType { get; init; } = EndpointApiType.OpenAiCompatible;
}