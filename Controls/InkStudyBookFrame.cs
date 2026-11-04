using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TrueFluentPro.Controls;

/// <summary>仅绘制十五片书册材质，不承载内容、不参与命中测试。</summary>
public partial class InkStudyBookFrame : UserControl
{
    public InkStudyBookFrame()
    {
        AvaloniaXamlLoader.Load(this);
    }
}