using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Views;

internal static class SongScrollPointerProbe
{
    internal static async Task DragAsync(MainWindow window,SongScrollTranslationView view,Grid header,Border paper,CancellationToken token)
    {
        var field=typeof(SongScrollTranslationView).GetField("_floatDrag",BindingFlags.Instance|BindingFlags.NonPublic)!;
        void Observe(object? sender,PointerEventArgs e)
        {
            var source=e.Source as Control;var captured=e.Pointer.Captured as Control;
            Console.WriteLine($"FLOAT_POINTER event={e.RoutedEvent?.Name};source={source?.Name}/{source?.GetType().Name};captured={captured?.Name}/{captured?.GetType().Name};point={e.GetPosition(view)};leftDown={e.GetCurrentPoint(view).Properties.IsLeftButtonPressed};offset={field.GetValue(view)};paper={Canvas.GetLeft(paper)},{Canvas.GetTop(paper)};handled={e.Handled}");
        }
        EventHandler<PointerPressedEventArgs> down=(s,e)=>Observe(s,e);EventHandler<PointerEventArgs> move=Observe;EventHandler<PointerReleasedEventArgs> up=(s,e)=>Observe(s,e);
        view.AddHandler(InputElement.PointerPressedEvent,down,RoutingStrategies.Bubble,true);
        view.AddHandler(InputElement.PointerMovedEvent,move,RoutingStrategies.Bubble,true);
        view.AddHandler(InputElement.PointerReleasedEvent,up,RoutingStrategies.Bubble,true);
        try
        {
            var start=header.PointToScreen(new Point(30,15));var hit=window.InputHitTest(window.PointToClient(start)) as Visual;
            Console.WriteLine($"FLOAT_DRAG start={start};hit={(hit as Control)?.Name}/{hit?.GetType().Name};header={SongScrollTranslationView.TransformedBounds(header,window)};paper={SongScrollTranslationView.TransformedBounds(paper,window)}");
            if(hit!=header&&hit?.GetVisualAncestors().Contains(header)!=true)throw new InvalidOperationException("拖动起点未命中浮纸标题，拒绝注入。");
            await FloatingSubtitleRegression.DragAsync(start,new PixelPoint(start.X-24,start.Y));token.ThrowIfCancellationRequested();
        }
        finally
        {
            view.RemoveHandler(InputElement.PointerPressedEvent,down);view.RemoveHandler(InputElement.PointerMovedEvent,move);view.RemoveHandler(InputElement.PointerReleasedEvent,up);
        }
    }
}