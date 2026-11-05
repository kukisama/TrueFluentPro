using Avalonia.Controls;

namespace TrueFluentPro.Views;

/// <summary>仅呈现既有控制面板 VM，不创建服务或第二份生命周期订阅。</summary>
public partial class SongScrollAudioLabViewControlPanel : UserControl
{
    public SongScrollAudioLabViewControlPanel() => InitializeComponent();
}