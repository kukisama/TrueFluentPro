using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TrueFluentPro.Views;

/// <summary>逐路径复用已确认 HTML 的 24×24 线性图标，不以字体图标重新解释原稿。</summary>
public sealed class SongScrollIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<SongScrollIcon,string>(nameof(Kind),"mic");
    public static readonly StyledProperty<IBrush> InkProperty = AvaloniaProperty.Register<SongScrollIcon,IBrush>(nameof(Ink),Brushes.Black);
    public string Kind { get=>GetValue(KindProperty); set=>SetValue(KindProperty,value); }
    public IBrush Ink { get=>GetValue(InkProperty); set=>SetValue(InkProperty,value); }
    static SongScrollIcon() { AffectsRender<SongScrollIcon>(KindProperty,InkProperty); }
    public SongScrollIcon() { IsHitTestVisible=false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width<=0 || Bounds.Height<=0) return;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width/24,Bounds.Height/24)))
        {
            var pen=new Pen(Ink,1.8) { LineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
            void Line(string path)=>context.DrawGeometry(null,pen,Geometry.Parse(path));
            var paper=new SolidColorBrush(Color.Parse("#F3EEE4"));
            switch(Kind)
            {
                case "mic":
                    context.DrawRectangle(null,pen,new Rect(9,2,6,13),3,3);
                    Line("M5 10 V12 A7 7 0 0 0 19 12 V10 M12 19 V22 M8 22 H16 M11 5 H13 M11 8 H13 M11 11 H13"); break;
                case "pen": Line("M4 17 L6 11 L17 2 L22 7 L11 17 L4 19 Z M6 11 L11 17 M15 4 L20 9 M3 22 H21 M4 17 L8 18"); break;
                case "play": context.DrawRectangle(Ink,null,new Rect(2,4,20,16),3,3);context.DrawGeometry(paper,null,Geometry.Parse("M10 8 L16 12 L10 16 Z"));break;
                case "headphones": Line("M3 17 V11 A9 9 0 0 1 21 11 V17");context.DrawRectangle(null,pen,new Rect(2,12,5,9),2,2);context.DrawRectangle(null,pen,new Rect(17,12,5,9),2,2);break;
                case "chart": Line("M3 3 V21 H22 M7 17 V12 M12 17 V6 M17 17 V9 M22 17 V4");break;
                case "settings": context.DrawGeometry(Ink,null,Geometry.Parse("M10 2 H14 L15 5 L18 6 L21 5 L23 9 L21 11 V14 L23 16 L21 20 L18 19 L15 20 L14 23 H10 L9 20 L6 19 L3 20 L1 16 L3 14 V11 L1 9 L3 5 L6 6 L9 5 Z"));context.DrawEllipse(paper,null,new Rect(8,8,8,8));break;
                case "theme": context.DrawEllipse(null,pen,new Rect(3,3,18,18));context.DrawGeometry(Ink,null,Geometry.Parse("M12 3 A9 9 0 0 0 12 21 Z"));break;
                case "screen": context.DrawRectangle(null,pen,new Rect(2,3,20,15),1,1);Line("M8 22 H16 M12 18 V22 M9 9 H15 M12 6 V12");break;
            }
        }
    }
}