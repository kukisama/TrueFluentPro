using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels.Presentation;
using TrueFluentPro.Views;

internal static class SongScrollRegression
{
    internal static async Task VerifyViewportFitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "song-scroll-fit", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        var window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "appearance.json")))
        { Width = 1256, Height = 831, Position = new PixelPoint(40, 40), WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show();
            await Pump(window, token);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            var view = Find<Panel>(window, "SongScrollHost").Children.OfType<SongScrollTranslationView>().Single();
            view.BindSession(new SongScrollFakeSession());
            foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal })
            {
                window.WindowState = state;
                await Pump(window, token);
                var stage = Bounds(Find<Canvas>(view, "ScrollStage"), window);
                var footer = Bounds(Find<Border>(view, "ScrollSessionControls"), window);
                var seal = Bounds(Find<Button>(view, "ScrollStart"), window);
                Check(Math.Abs(stage.Bottom - window.ClientSize.Height) < 1, "舞台下方仍留有整条窗体底色");
                Check(Math.Abs(stage.Left) < 1 && Math.Abs(stage.Right - window.ClientSize.Width) < 1, "舞台左右仍留有整条窗体底色");
                Check(Math.Abs(seal.Width / seal.Height - 136d / 124) < .001, "高度适配拉伸了朱印");
                Check(footer.Bottom <= window.ClientSize.Height && seal.Bottom <= footer.Bottom, "页底主操作超出窗口");
                var bookmarks = Find<Button>(view, "ScrollLiveNav").GetVisualAncestors().OfType<StackPanel>().First();
                Check(Bounds(Find<Border>(view, "ScrollSidebarFooter"), window).Top >= Bounds(bookmarks, window).Bottom - 1, "左侧设置区遮住了最后一枚书签");
                view.Presentation!.SettingsExpanded = true;
                await Pump(window, token);
                Check(Math.Abs(Bounds(Find<Border>(view, "ScrollSessionControls"), window).Top - footer.Top) < 1, "展开设置移动了常驻主控");
                view.Presentation.SettingsExpanded = false;
                await Pump(window, token);
                Console.WriteLine($"PASS viewport-fit state={state};client={window.ClientSize};stage={stage};footer={footer};sealRatio={seal.Width / seal.Height:F6}");
            }
            Console.WriteLine("SONG_SCROLL_FIT_EXIT=0");
        }
        finally { window.Close(); }
    }

    internal static async Task VerifyAsync()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40)); var token=deadline.Token;
        var root=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","song-scroll-validation",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(root);
        var lines=new List<string>(); void Log(string value){lines.Add(value);Console.WriteLine(value);}
        var app=Application.Current!; var oldTheme=app.RequestedThemeVariant; MainWindow? window=null; var pass=false;
        try
        {
            Log("ARTIFACTS="+root); Log("ASSEMBLY="+typeof(MainWindow).Assembly.Location);
            Log("SCOPE=isolated presentation source/config; native HWND + SendInput + BitBlt; no MainVM constructor, real user configuration, audio capture, cloud or IME registration");
            app.RequestedThemeVariant=ThemeVariant.Dark;
            window=new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root,"appearance.json"))) { Width=1256,Height=831,WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(40,40) };
            window.Show(); await Pump(window,token);
            Check(window.DataContext==null,"隔离DataContext失效");
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll); await Pump(window,token);
            var view=Find<Panel>(window,"SongScrollHost").Children.OfType<SongScrollTranslationView>().Single();
            var fake=new SongScrollFakeSession(); view.BindSession(fake); await Pump(window,token);
            File.WriteAllText(Path.Combine(root,"fixture.json"),JsonSerializer.Serialize(new { history=fake.History.Select(item=>new { original=item.OriginalText,translated=item.TranslatedText,time=item.Timestamp.ToString("HH:mm:ss") }), markdown=fake.AiInsight.InsightMarkdown },new JsonSerializerOptions{WriteIndented=true}));
            var title=Find<Border>(view,"ScrollTitleDrag");
            await WindowChromeRegression.ScrollForegroundAsync(window,title,token); await Pump(window,token);
            Check(!Find<Border>(window,"ShellTitleBar").IsVisible&&!Find<Grid>(window,"LegacyWorkspace").IsVisible,"双标题/旧宿主仍显示");
            Check(ReferenceEquals(view.Presentation!.Session,fake),"会话不是同一source");
            Check(app.RequestedThemeVariant==ThemeVariant.Dark&&window.ActualThemeVariant==ThemeVariant.Light,"污染应用明暗/局部主题");
            foreach(var screen in window.Screens.All) Log($"SCREEN={screen.Bounds};work={screen.WorkingArea};scaling={screen.Scaling}");
            Log("DPI=actual monitor only; higher DPI not measured unless Screens.All reports it");
            WindowChromeRegression.ScrollStyle(window,Log);
            var captureClient=window.ClientSize;
            var stageHeight=captureClient.Height*2560/captureClient.Width;
            var extraHeight=stageHeight-1440;
            var scale=Math.Min(captureClient.Width/2560,captureClient.Height/stageHeight);
            var stage=Find<Canvas>(view,"ScrollStage"); var stageRect=Bounds(stage,window);
            Check(Math.Abs(stageRect.Width-2560*scale)<1&&Math.Abs(stageRect.Height-stageHeight*scale)<1&&Math.Abs(stageRect.Top)<1&&Math.Abs(stageRect.Bottom-captureClient.Height)<1,"Viewbox未保持等比并填满客户区高度");
            var right=Bounds(Find<Grid>(view,"ScrollRightPage"),window); var footer=Bounds(Find<Border>(view,"ScrollSessionControls"),window);
            Check(Math.Abs(right.Left-stageRect.Left-1425*scale)<1&&Math.Abs(right.Top-270*scale)<1&&Math.Abs(right.Width-1038*scale)<1&&Math.Abs(right.Height-(1074+extraHeight)*scale)<1,"右页未按窗口高度延展");
            Check(Math.Abs(footer.Height-132*scale)<1,"底栏未保持132设计像素");
            Log($"GEOMETRY client={window.ClientSize};stage={stageRect};scale={scale};right={right};footer={footer}");
            var insight=Find<ScrollViewer>(view,"ScrollInsightContent"); var history=Find<ScrollViewer>(view,"ScrollHistoryContent");
            Check(insight.Extent.Height>insight.Viewport.Height,"洞察长文不能滚动");
            var md=Find<SongScrollMarkdownBlock>(view,"ScrollMarkdown");
            Check(md.Markdown==fake.AiInsight.InsightMarkdown&&md.GetVisualDescendants().OfType<SelectableTextBlock>().Count()>30,"不是真实长Markdown");
            var font=Find<TextBlock>(view,"ScrollHeading");
            var typeface=new Typeface(font.FontFamily,font.FontStyle,font.FontWeight,font.FontStretch).GlyphTypeface;
            Log($"FONT requested={font.FontFamily};actual={typeface.FamilyName};titleSize={font.FontSize}");
            WindowChromeRegression.ScrollCapture(window,Path.Combine(root,"native-insight-default.png"));
            WindowChromeRegression.ScrollCaptureTop(window,Path.Combine(root,"native-dwm-top.png"),Log);
            insight.Offset=new Vector(0,350); await Pump(window,token); var insightSaved=insight.Offset;
            await Click(window,view,"ScrollHistoryTab",token); Check(view.Presentation.IsHistory&&!Find<Grid>(view,"ScrollInsightPanel").IsEffectivelyVisible,"互斥切换失败");
            Check(view.Presentation.VisibleHistory.Count==15,"近期不是15条"); await Click(window,view,"ScrollHistoryAll",token); Check(view.Presentation.VisibleHistory.Count==30,"全部历史不完整");
            var historyText=history.GetVisualDescendants().OfType<SelectableTextBlock>().First(t=>t.Text==fake.History[0].OriginalText);
            Check(historyText.Bounds.Width>600&&historyText.TextLayout.Width<=historyText.Bounds.Width+1,"历史正文收窄或逐字换行");
            var expand=history.GetVisualDescendants().OfType<Button>().First(b=>b.Name=="ScrollHistoryExpand");
            await WindowChromeRegression.ScrollClickAsync(window,expand,token);await Pump(window,token);Check(view.Presentation.VisibleHistory[0].IsExpanded,"历史记录未真正展开");
            await Click(window,view,"ScrollInsightTab",token);await Click(window,view,"ScrollHistoryTab",token);Check(view.Presentation.VisibleHistory[0].IsExpanded,"切页丢失历史展开状态");
            await WindowChromeRegression.ScrollClickAsync(window,expand,token);await Pump(window,token);
            Log($"HISTORY_TEXT widthDesign={historyText.Bounds.Width};textLayout={historyText.TextLayout.Width};expanded state retained");
            history.Offset=new Vector(0,400); await Pump(window,token); var historySaved=history.Offset;
            WindowChromeRegression.ScrollCapture(window,Path.Combine(root,"native-history.png"));
            await Click(window,view,"ScrollInsightTab",token); Check(Math.Abs(insight.Offset.Y-insightSaved.Y)<1,"洞察位置丢失");
            await Click(window,view,"ScrollHistoryTab",token); Check(Math.Abs(history.Offset.Y-historySaved.Y)<1,"历史位置丢失");
            await Click(window,view,"ScrollInsightTab",token); insight.Offset=default; await Pump(window,token);
            await Click(window,view,"ScrollSettingsToggle",token);
            var expandedFooter=Bounds(Find<Border>(view,"ScrollSessionControls"),window);
            Check(Math.Abs(footer.Top-expandedFooter.Top)<1&&Find<Grid>(view,"ScrollSettingsForm").IsEffectivelyVisible&&insight.Viewport.Height>100,"展开设置挤掉底栏/长文");
            Log($"SETTINGS footerBefore={footer};footerAfter={expandedFooter};insightViewport={insight.Viewport}");
            WindowChromeRegression.ScrollCapture(window,Path.Combine(root,"native-settings-expanded.png"));
            var fontCombo=Find<ComboBox>(view,"ScrollFont"); fontCombo.SelectedItem=20; await Pump(window,token);
            Check(view.Presentation.OriginalSize==42&&view.Presentation.TranslatedSize==38&&view.Presentation.InsightSize==30&&fake.ConfigVM.Config.DefaultFontSize==38,"字号映射/旧字号污染");
            fontCombo.SelectedItem=16; await Pump(window,token);
            await Click(window,view,"ScrollRefresh",token); Check(fake.AudioDevices.RefreshAudioDevicesCommand.ExecutionCount==1,"设备绑定不是执行一次");
            await Click(window,view,"ScrollSettingsToggle",token);
            await Click(window,view,"ScrollStart",token); Check(fake.ToggleTranslationCommand.ExecutionCount==1,"主印绑定不是执行一次");
            fake.AudioDevices.AudioLevelHistory[0]=.75;await Pump(window,token);
            var levels=Find<ItemsControl>(view,"ScrollAggregateLevel");Check(ReferenceEquals(levels.ItemsSource,fake.AudioDevices.AudioLevelHistory)&&levels.GetVisualDescendants().OfType<Border>().Any(b=>Math.Abs(b.Height-19.5)<.1),"朱印汇总电平未绑定真实source");fake.AudioDevices.AudioLevelHistory[0]=0;await Pump(window,token);
            var start=Find<Button>(view,"ScrollStart"); WindowChromeRegression.ScrollHover(window,start); await Task.Delay(220,token); await Pump(window,token);
            Check(start.IsPointerOver,"朱印未真实hover"); var seal=Bounds(start,window);
            Check(Math.Abs(seal.Width-136*scale)<1&&Math.Abs(seal.Height-124*scale)<1,"朱印变形");
            Check(!seal.Intersects(Bounds(Find<Button>(view,"ScrollSubtitle"),window)),"朱印遮字幕按钮");
            Log($"SEAL hover={seal};ratio={seal.Width/seal.Height};hit=own HWND"); WindowChromeRegression.ScrollCapture(window,Path.Combine(root,"native-seal-hover.png"));
            await Click(window,view,"ScrollFloatOpen",token); var floatPaper=Find<Border>(view,"ScrollFloatingPaper");
            Check(floatPaper.IsEffectivelyVisible&&Bounds(floatPaper,window).Bottom<footer.Top,"浮纸遮主控");
            var floatMarkdown=Find<SongScrollMarkdownBlock>(view,"ScrollFloatMarkdown");Check(floatMarkdown.Markdown==md.Markdown,"浮纸不是共享结果");
            await Click(window,view,"ScrollFloatPin",token);Check(floatPaper.ZIndex==90,"浮纸置顶状态未应用");
            var plus=floatPaper.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="A+");await WindowChromeRegression.ScrollClickAsync(window,plus,token);await Pump(window,token);Check(floatMarkdown.ReadingFontSize==20&&floatMarkdown.MajorHeadingOffset==6,"浮纸独立字号/标题映射错误");
            var floatBefore=Bounds(floatPaper,window);await SongScrollPointerProbe.DragAsync(window,view,Find<Grid>(view,"ScrollFloatDrag"),floatPaper,token);await Pump(window,token);Check(Bounds(floatPaper,window).Left<floatBefore.Left-8,"浮纸真实拖动未移动");
            WindowChromeRegression.ScrollCapture(window,Path.Combine(root,"native-floating-paper.png"));await Click(window,view,"ScrollFloatClose",token);
            var subtitleMenu=(MenuFlyout)Find<Button>(view,"ScrollSubtitle").ContextFlyout!;Check(ReferenceEquals(subtitleMenu.Items.OfType<MenuItem>().First().Command,fake.ShowFloatingMicSubtitleCommand),"字幕分源菜单未继承同一会话");
            Find<TextBox>(view,"ScrollQuestion").Text="定向问题草稿";await Pump(window,token);
            await Click(window,view,"ScrollHistoryTab",token);await Click(window,view,"ScrollInsightTab",token);
            Check(Find<TextBox>(view,"ScrollQuestion").Text=="定向问题草稿"&&fake.AiInsight.InsightUserInput=="定向问题草稿","提问草稿丢失/未双向绑定");
            await Click(window,view,"ScrollSend",token);Check(fake.AiInsight.SendInsightCommand.ExecutionCount==1,"洞察发送没有执行一次");
            fake.AiInsight.IsInsightLoading=true;await Pump(window,token);await Click(window,view,"ScrollInsightStop",token);Check(fake.AiInsight.StopInsightCommand.ExecutionCount==1,"停止生成命令未绑定");
            fake.AiInsight.IsInsightLoading=false;await Pump(window,token);await Click(window,view,"ScrollInsightClear",token);Check(fake.AiInsight.ClearInsightCommand.ExecutionCount==1,"清除生成命令未绑定");
            var isolatedConfig=new AzureSpeechConfig { AiConfig=new AiConfig() };var saves=0;view.BindConfigurationDrafts(()=>isolatedConfig,draft=>{draft.ApplyTo(isolatedConfig.AiConfig!);saves++;File.WriteAllText(Path.Combine(root,"draft-config.json"),JsonSerializer.Serialize(isolatedConfig.AiConfig));return Task.CompletedTask;});
            await Click(window,view,"ScrollAdvanced",token);Find<TextBox>(view,"ScrollDraftSystem").Text="未保存的草稿";await Click(window,view,"ScrollDraftCancel",token);
            Check(saves==0&&isolatedConfig.AiConfig!.InsightSystemPrompt!="未保存的草稿","取消触发保存/污染业务配置");
            await Click(window,view,"ScrollAdvanced",token);Find<TextBox>(view,"ScrollDraftSystem").Text="已保存的草稿";await Click(window,view,"ScrollDraftSave",token);
            Check(saves==1&&isolatedConfig.AiConfig!.InsightSystemPrompt=="已保存的草稿","保存没有提交同一草稿");
            Log("PASS native question/send/stop/clear bindings; advanced UI cancel=zero saves, save=one isolated commit; no user configuration");
            await SongScrollSafety.VerifyAsync(token,Log);
            var before=fake.History.ToArray(); var stamp=new DateTime(2026,11,5,10,26,0);
            fake.Publish(new LiveTranslationFrame("同一句未完","partial",stamp,VadGateController.ActiveSource.Mic,false,"merge")); await Pump(window,token);
            var active=view.Presentation.Conversation.Single(c=>c.SegmentKey=="merge"); fake.Publish(fake.LatestRealtimeFrame! with {OriginalText="同一句终稿",IsFinal=true}); await Pump(window,token);
            fake.History.Insert(0,new TranslationItem{OriginalText="同一句终稿",TranslatedText="partial",Timestamp=stamp,Source=VadGateController.ActiveSource.Mic,SubtitleSegmentKey="merge"}); await Pump(window,token);
            Check(view.Presentation.Conversation.Count(c=>c.SegmentKey=="merge")==1&&ReferenceEquals(view.Presentation.Conversation.Last(),active),"partialfinal不是同一句唯一合并");
            Check(fake.History.Skip(1).SequenceEqual(before),"投影修改历史顺序");
            fake.Publish(new LiveTranslationFrame("未知来源","neutral",stamp.AddSeconds(1),VadGateController.ActiveSource.None,false,"unknown")); await Pump(window,token);
            Check(view.Presentation.Conversation.Last().SourceLabel=="音频输入"&&view.Presentation.Conversation.Count(c=>c.SourceLabel=="麦克风")>1,"同麦克风假分人/未知源不实");
            var draftConfig=new AiConfig(); var draft=new SongScrollInsightDraft(draftConfig); draft.Presets[0].Name="草稿"; draft.SystemPrompt="草稿系统";
            Check(draftConfig.PresetButtons[0].Name!="草稿"&&draftConfig.InsightSystemPrompt!="草稿系统","草稿不是深拷贝"); draft.ApplyTo(draftConfig); Check(draftConfig.PresetButtons[0].Name=="草稿","保存未应用草稿"); draft.Presets[0].Name="后续编辑"; Check(draftConfig.PresetButtons[0].Name=="草稿","保存后对象仍共享");
            Log("PASS native layout, same-source commands, mutually exclusive long reading panels, retained positions/question, folded settings fixed footer, source labels, partial-final identity, draft deep-copy");
            foreach(var appearance in new[]{ShellAppearancePreference.Classic,ShellAppearancePreference.SongStudy,ShellAppearancePreference.InkStudy,ShellAppearancePreference.SongScroll})
            { window.ApplyShellAppearance(appearance);await Pump(window,token);Check(window.CurrentShellAppearance==appearance,"主题值未保留");Check(Find<Border>(window,"ShellTitleBar").IsVisible==(appearance!=ShellAppearancePreference.SongScroll),"标题路由错误"); }
            view.BindSession(fake);await Pump(window,token);Check(Find<Panel>(window,"SongScrollHost").Children.OfType<SongScrollTranslationView>().Single()==view,"新页缓存被重建");
            await Click(window,view,"ScrollTheme",token);var menu=(MenuFlyout)Find<Button>(view,"ScrollTheme").Flyout!;
            Check(menu.IsOpen,"新页没有实际主题菜单");var option=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.Contains("宋人书斋 · 长卷")==true);
            await WindowChromeRegression.ScrollPopupClickAsync(window,option,token);await Pump(window,token);menu.Hide();
            Check(new ShellAppearancePreferencesService(Path.Combine(root,"appearance.json")).Load()==ShellAppearancePreference.SongScroll,"长卷主题菜单未保存到隔离appearance.json");view.BindSession(fake);await Pump(window,token);
            Log("PASS own theme menu -> SongScroll save/reload (isolated appearance only); real aggregate level .75 -> 19.5 stage px, no per-person wave");
            await WindowChromeRegression.ScrollForegroundAsync(window,title,token);
            await WindowChromeRegression.ScrollDoubleClickAsync(window,title,token);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Maximized,token);
            await WindowChromeRegression.ScrollDoubleClickAsync(window,title,token);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Normal,token);
            await WindowChromeRegression.ScrollResizeAsync(window,token,Log);
            await Click(window,view,"ScrollMaximize",token);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Maximized,token);Check(AutomationProperties.GetName(Find<Button>(view,"ScrollMaximize"))=="还原","还原按钮没同步");
            await Click(window,view,"ScrollMaximize",token);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Normal,token);
            await Click(window,view,"ScrollMinimize",token);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Minimized,token);WindowChromeRegression.ScrollRestore(window);await WindowChromeRegression.ScrollStateAsync(window,WindowState.Normal,token);
            await WindowChromeRegression.ScrollForegroundAsync(window,title,token);
            var oldPosition=window.Position;var p=title.PointToScreen(new Point(1000,30));await FloatingSubtitleRegression.DragAsync(p,new PixelPoint(p.X+24,p.Y+16));await Pump(window,token);Check(window.Position.X>oldPosition.X+8&&window.Position.Y>oldPosition.Y+5,"真实拖动失败");
            var closed=0;window.Closed+=(_,_)=>closed++;await Click(window,view,"ScrollClose",token);Check(closed==1&&!window.IsVisible,"原生关闭失败");
            Check(fake.DisposeCount==0&&fake.StopCount==0,"呈现释放停止业务");
            Log("PASS four appearance routes/cache; native minimize/maximize/restore/drag/close; BorderOnly thickframe retained");pass=true;
            File.WriteAllText(Path.Combine(root,"geometry.json"),JsonSerializer.Serialize(new{clientWidth=captureClient.Width,clientHeight=captureClient.Height,scale,right=right.ToString(),footer=footer.ToString(),seal=seal.ToString()},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){Log("FAIL "+ex);throw;}
        finally{window?.Close();app.RequestedThemeVariant=oldTheme;Log("SONG_SCROLL_EXIT="+(pass?0:1));File.WriteAllLines(Path.Combine(root,"result.txt"),lines);}
    }
    private static Rect Bounds(Visual visual,Visual ancestor)=>SongScrollTranslationView.TransformedBounds(visual,ancestor);
    private static T Find<T>(Control root,string name) where T:Control=>root.FindControl<T>(name)??root.GetVisualDescendants().OfType<T>().First(c=>c.Name==name);
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Pump(MainWindow window,CancellationToken token)
    {await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);if(!window.IsVisible)return;window.UpdateLayout();var composition=ElementComposition.GetElementVisual(window)??throw new InvalidOperationException("已显示窗口尚无合成器");var batch=composition.Compositor.RequestCompositionBatchCommitAsync();await Dispatcher.UIThread.AwaitWithPriority(batch.Rendered.WaitAsync(token),DispatcherPriority.Background);}
    private static async Task Click(MainWindow window,Control view,string name,CancellationToken token){await WindowChromeRegression.ScrollClickAsync(window,Find<Control>(view,name),token);await Pump(window,token);}
}

public sealed class SongScrollFakeConfig
{
    public InkStudyFakeLegacyConfig Config { get; }=new();
    public string[] SpeechResourceNames {get;}={"东南亚 · 隔离配置"}; public int ActiveSpeechResourceIndex {get;set;}
    public string ActiveSpeechResourceStatus=>"已配置 · 东南亚"; public int SourceLanguageIndex{get;set;} public int TargetLanguageIndex{get;set;}
    public bool IsConfigurationEnabled=>true;
}
public sealed class SongScrollFakeSession:InkStudyFakeState,ILiveTranslationPresentationSource,IDisposable
{
    public ObservableCollection<TranslationItem> History{get;}=new();
    public SongScrollFakeConfig ConfigVM{get;}=new(); public InkStudyFakeAudioDevices AudioDevices{get;}=new(); public SongScrollFakeInsight AiInsight{get;}=new();
    public LiveTranslationFrame? LatestRealtimeFrame{get;private set;} public string CurrentOriginal=>LatestRealtimeFrame?.OriginalText??"";public string CurrentTranslated=>LatestRealtimeFrame?.TranslatedText??"";
    public InkStudyFakeCommand ToggleTranslationCommand{get;}=new();public InkStudyFakeCommand ClearHistoryCommand{get;}=new();public InkStudyFakeCommand OpenHistoryFolderCommand{get;}=new();public InkStudyFakeCommand ShowFloatingSubtitlesCommand{get;}=new();public InkStudyFakeCommand ShowFloatingMicSubtitleCommand{get;}=new();public InkStudyFakeCommand ShowFloatingLoopbackSubtitleCommand{get;}=new();public InkStudyFakeCommand RestoreFloatingSubtitleInteractionCommand{get;}=new();
    public string TranslationToggleButtonText=>"开始翻译";public string StatusMessage=>"待开始";public string AudioPipelineStatusText=>"";public bool IsAnySubtitleOpen=>false;public bool IsTranslating=>false;public int DisposeCount{get;private set;}public int StopCount=>0;
    public SongScrollFakeSession()
    {
        AudioDevices.IsRecognitionLoopbackMix=false;AudioDevices.IsRecognitionMicOnly=true;AudioDevices.IsRecordingLoopbackMix=false;AudioDevices.IsRecordingMicOnly=true;
        // 测试读取原型中结构化纯文字 fixture，绝不在生产执行模拟 JS。
        var js=File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"design","宋人书斋HTML原型_20261105","app.js")).Replace("\r\n","\n");
        var historyBlock=js[(js.IndexOf("const historyPairs = [",StringComparison.Ordinal)+"const historyPairs = ".Length)..js.IndexOf(";\n  const initialHistory",StringComparison.Ordinal)];
        using var pairs=JsonDocument.Parse(historyBlock);
        var timestamp=new DateTime(2026,11,5,10,24,59);
        foreach(var pair in pairs.RootElement.EnumerateArray())History.Add(new TranslationItem{OriginalText=pair[0].GetString()!,TranslatedText=pair[1].GetString()!,Timestamp=timestamp.AddSeconds(-History.Count),Source=VadGateController.ActiveSource.Mic,SubtitleSegmentKey="history-"+History.Count});
        // 左页原稿两段；历史仍有完整30条，呈现通过不同时间/句身份识别真正的句子。
        History.Clear();
        History.Add(new TranslationItem{OriginalText="这真是一个很棒的想法。",TranslatedText="That’s a wonderful idea.",Timestamp=new DateTime(2026,11,5,10,24,29),Source=VadGateController.ActiveSource.Mic,SubtitleSegmentKey="second"});
        History.Add(new TranslationItem{OriginalText="让交流跨越语言的边界。",TranslatedText="Let every conversation flow naturally.",Timestamp=new DateTime(2026,11,5,10,24,17),Source=VadGateController.ActiveSource.Mic,SubtitleSegmentKey="first"});
        foreach(var pair in pairs.RootElement.EnumerateArray().Skip(2))History.Add(new TranslationItem{OriginalText=pair[0].GetString()!,TranslatedText=pair[1].GetString()!,Timestamp=timestamp.AddMinutes(-History.Count),Source=VadGateController.ActiveSource.Mic,SubtitleSegmentKey="history-"+History.Count});
        var reportBlock=js[(js.IndexOf("const reportSections = [",StringComparison.Ordinal)+"const reportSections = ".Length)..js.IndexOf(";\n  const focusLines",StringComparison.Ordinal)];
        using var sections=JsonDocument.Parse(reportBlock);var report=new System.Text.StringBuilder("## 会议洞察报告\n\n生成时间 10:24:59 · 本地会议示例\n\n");var index=1;
        foreach(var section in sections.RootElement.EnumerateArray()){report.AppendLine($"### {index++} · {section[0].GetString()}\n");foreach(var line in section[1].EnumerateArray())report.AppendLine("- "+line.GetString());report.AppendLine();}
        AiInsight.InsightMarkdown=report.ToString();AiInsight.InsightUserInput="";
    }
    public void Publish(LiveTranslationFrame frame){LatestRealtimeFrame=frame;Notify(nameof(LatestRealtimeFrame));}
    public void Dispose()=>DisposeCount++;
}

public sealed class SongScrollFakeInsight:InkStudyFakeState
{
    public string InsightMarkdown{get;set;}="";
    public bool IsInsightEmpty=>string.IsNullOrEmpty(InsightMarkdown);
    public string InsightUserInput{get;set;}="";
    private bool _loading;
    public bool IsInsightLoading{get=>_loading;set{_loading=value;Notify();}}
    public bool IsAiConfigured=>true;public bool IsAutoInsightEnabled=>false;
    public string AutoInsightToggleText=>"自动分析未启动";
    public InsightPresetButton[] InsightPresetButtons{get;}={new(){Name="会议摘要",Prompt="总结会议"}};
    public InkStudyFakeCommand SendInsightCommand{get;}=new();public InkStudyFakeCommand StopInsightCommand{get;}=new();public InkStudyFakeCommand ClearInsightCommand{get;}=new();public InkStudyFakeCommand SendPresetInsightCommand{get;}=new();
}