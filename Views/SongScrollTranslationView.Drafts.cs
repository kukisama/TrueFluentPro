using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class SongScrollTranslationView
{
    private TextBox DraftText(string value, double height = 90) => new() { Text = value, AcceptsReturn = true, Height = height, FontSize = 18, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Field(string label, Control control)
    { var field = new StackPanel { Spacing = 7 }; field.Children.Add(new TextBlock { Text = label, FontFamily = "STKaiti, KaiTi", FontSize = 18 }); field.Children.Add(control); return field; }
    private void OpenAdvancedDraft()
    {
        if (_draftConfigProvider == null || _saveDraft == null) return;
        var config = _draftConfigProvider();
        var draft = new SongScrollInsightDraft(config.AiConfig ?? new AiConfig());
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock { Text = "洞察高级配置", FontFamily = "STKaiti, KaiTi", FontSize = 30 });
        var prompts = new StackPanel { Spacing = 14 };
        var system = DraftText(draft.SystemPrompt); var template = DraftText(draft.UserTemplate);
        system.Name = "ScrollDraftSystem"; template.Name = "ScrollDraftTemplate";
        prompts.Children.Add(Field("系统提示词",system)); prompts.Children.Add(Field("用户内容模板",template));
        prompts.Children.Add(new TextBlock { Text = "保留 {history} 与 {question}；保存后用于下一次生成。", FontSize = 14 });
        var presets = new StackPanel { Spacing = 10 };
        var selected = new ComboBox { ItemsSource = draft.Presets, DisplayMemberBinding = new Avalonia.Data.Binding("Name"), FontSize = 18 };
        var name = new TextBox { FontSize = 18 }; var prompt = DraftText("");
        InsightPresetButton? current = null;
        void SaveSelected() { if (current != null) { current.Name = name.Text ?? ""; current.Prompt = prompt.Text ?? ""; } }
        selected.SelectionChanged += (_, _) => { SaveSelected(); current = selected.SelectedItem as InsightPresetButton; name.Text = current?.Name ?? ""; prompt.Text = current?.Prompt ?? ""; };
        presets.Children.Add(Field("选择预设", selected)); presets.Children.Add(Field("预设名称",name)); presets.Children.Add(Field("预设提示词",prompt));
        var presetActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        presetActions.Children.Add(PaperButton("添加预设",(_,_) => { SaveSelected(); var added = new InsightPresetButton { Name = "新预设", Prompt = "" }; draft.Presets.Add(added); selected.SelectedItem = added; }));
        presetActions.Children.Add(PaperButton("删除所选",(_,_) => { if (current != null) { var removed = current; current = null; draft.Presets.Remove(removed); selected.SelectedItem = draft.Presets.FirstOrDefault(); } }));
        presets.Children.Add(presetActions); selected.SelectedItem = draft.Presets.FirstOrDefault();
        var modelPanel = new StackPanel { Spacing = 14 };
        var models = config.GetAvailableModels(ModelCapability.Text).Select(pair => new ModelOption { Reference = new ModelReference { EndpointId = pair.Endpoint.Id, ModelId = pair.Model.ModelId }, EndpointName = pair.Endpoint.Name, ModelDisplayName = string.IsNullOrEmpty(pair.Model.DisplayName) ? pair.Model.ModelId : pair.Model.DisplayName, EndpointType = pair.Endpoint.EndpointType }).ToList();
        var model = new ComboBox { ItemsSource = models, FontSize = 18, SelectedItem = models.FirstOrDefault(m => m.Reference.EndpointId == draft.QuickModel?.EndpointId && m.Reference.ModelId == draft.QuickModel?.ModelId) };
        var modelTouched = false;
        model.SelectionChanged += (_, _) => modelTouched = true;
        modelPanel.Children.Add(Field("Quick 模型引用（最高优先级）",model));
        modelPanel.Children.Add(new TextBlock { Text = "实际生成优先级：Quick → Insight → Summary → Review。未选择 Quick 时沿用其余现有引用；终结点与模型清单仍在系统设置中管理。", TextWrapping = TextWrapping.Wrap, FontSize = 16 });
        modelPanel.Children.Add(PaperButton("清除 Quick，沿用后备引用", (_,_) => model.SelectedItem = null));
        modelPanel.Children.Add(PaperButton("进入已有资源模型设置", (_,_) => { CloseDialog(); NavigationRequested?.Invoke("settings"); }));
        var tabs = new TabControl { Items = { new TabItem { Header = "提示词", Content = prompts }, new TabItem { Header = "预设管理", Content = presets }, new TabItem { Header = "模型配置", Content = modelPanel } }, FontSize = 20 };
        body.Children.Add(tabs);
        var status = new TextBlock { Text = "修改仅在保存后应用；取消不提交。", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = PaperButton("取消",(_,_) => CloseDialog()); cancel.Name = "ScrollDraftCancel"; actions.Children.Add(cancel);
        var save = PaperButton("保存", async (sender,_) =>
        {
            SaveSelected(); draft.SystemPrompt = system.Text ?? ""; draft.UserTemplate = template.Text ?? "";
            if (modelTouched) draft.QuickModel = (model.SelectedItem as ModelOption)?.Reference;
            if (sender is Button button) button.IsEnabled = false;
            try { await _saveDraft(draft); CloseDialog(); }
            catch (Exception ex) { status.Text = $"保存未完成：{ex.Message}"; if (sender is Button b) b.IsEnabled = true; }
        });
        save.Name = "ScrollDraftSave";
        actions.Children.Add(save); body.Children.Add(actions); ShowDialog(body,system);
    }
    private void OpenAutoDraft()
    {
        if (_source is not MainWindowViewModel main) return;
        var ai = main.AiInsight;
        ai.LoadSongScrollAutoPreferences();
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock { Text = "自动分析", FontFamily = "STKaiti, KaiTi", FontSize = 30 });
        var available = new CheckBox { Content = "启用自动洞察", IsChecked = main.ConfigVM.Config.AiConfig?.SongScrollAutoAvailable != false, FontSize = 18 };
        body.Children.Add(available);
        var mode = new ComboBox { ItemsSource = new[] { "定时循环", "新数据触发（现有历史条数增长）" }, SelectedIndex = ai.AutoInsightModeIndex, FontSize = 18 };
        var interval = new NumericUpDown { Minimum = 10, Maximum = 3600, Value = ai.AutoInsightIntervalSeconds, FontSize = 18 };
        var prompt = DraftText(ai.AutoInsightPrompt);
        var buffer = new CheckBox { Content = "缓冲输出，完成后一次呈现", IsChecked = main.ConfigVM.Config.AiConfig?.AutoInsightBufferOutput == true, FontSize = 18 };
        body.Children.Add(Field("触发方式", mode)); body.Children.Add(Field("定时循环间隔（秒）", interval)); body.Children.Add(Field("自动提示词",prompt)); body.Children.Add(buffer);
        body.Children.Add(new TextBlock { Text = "停止自动只停止后续调度；当前生成请使用「停止生成」。新数据触发沿用业务历史条数增长规则，不是 revision 或最短间隔合并。", FontSize = 16, TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Text = ai.AutoInsightToggleText, FontSize = 16, TextWrapping = TextWrapping.Wrap }; body.Children.Add(status);
        async System.Threading.Tasks.Task Save()
        {
            if (string.IsNullOrWhiteSpace(prompt.Text)) throw new InvalidOperationException("请填写自动提示词。");
            var wasRunning = ai.IsAutoInsightEnabled;
            if (wasRunning) ai.ToggleAutoInsightCommand.Execute(null);
            ai.AutoInsightModeIndex = mode.SelectedIndex; ai.AutoInsightIntervalSeconds = (int)(interval.Value ?? 30); ai.AutoInsightPrompt = prompt.Text;
            var draft = new SongScrollInsightDraft(main.ConfigVM.Config.AiConfig ?? new AiConfig()) { BufferOutput = buffer.IsChecked == true,
                AutoAvailable = available.IsChecked == true, AutoModeIndex = mode.SelectedIndex, AutoIntervalSeconds = (int)(interval.Value ?? 30), AutoPrompt = prompt.Text };
            await main.Settings.SaveSongScrollInsightAsync(draft);
            if (wasRunning && available.IsChecked == true) ai.ToggleAutoInsightCommand.Execute(null);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var toggle = PaperButton(ai.AutoInsightToggleText, async (_,_) => { try { if (!ai.IsAutoInsightEnabled) { if (available.IsChecked != true) throw new InvalidOperationException("请先启用自动洞察。"); await Save(); } ai.ToggleAutoInsightCommand.Execute(null); status.Text = ai.AutoInsightToggleText; } catch (Exception ex) { status.Text = ex.Message; } });
        actions.Children.Add(toggle); actions.Children.Add(PaperButton("取消编辑",(_,_) => CloseDialog()));
        actions.Children.Add(PaperButton("保存配置",async (_,_) => { try { await Save(); CloseDialog(); } catch (Exception ex) { status.Text = ex.Message; } }));
        body.Children.Add(actions); ShowDialog(body,prompt);
    }
}