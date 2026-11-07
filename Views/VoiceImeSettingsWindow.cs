using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views;

/// <summary>语音输入法设置页；与主程序共享配置，保留原 VM 命令与自动保存。</summary>
internal sealed class VoiceImeSettingsPage : VoiceImeToolPage
{
    private ConfigurationService? _configService;
    private ConfigurationService Configuration => _configService ??= new ConfigurationService();
    private readonly Func<Task<AzureSpeechConfig>> _load;
    private readonly Func<AzureSpeechConfig, Task> _save;
    private readonly Action<VoiceImeSectionVM> _refresh;
    private readonly bool _unified;
    private readonly VoiceImeRegistrationService _registrationService = new();
    private readonly VoiceImeSectionVM _vm;
    private readonly TextBlock _saveStatusText;
    private readonly Button _retrySaveButton;
    private readonly StackPanel _sectionView;
    private Button[] _settingsSheetButtons = Array.Empty<Button>();
    private Control[] _settingsSheets = Array.Empty<Control>();
    // 仅页面呈现状态：0 识别、1 断句、2 输入法管理；不绑定 VM 或写入配置。
    internal int SelectedSheet { get; private set; }
    private readonly CancellationTokenSource _closedCts = new();
    private bool _closed;
    private bool _started;
    private AzureSpeechConfig? _config;
    private CancellationTokenSource? _saveDebounceCts;
    private Task _pendingSaveTask = Task.CompletedTask;
    private long _editVersion, _savedVersion, _failedVersion;
    private bool _saving;
    private Exception? _saveError;
    private bool HasUnsavedChanges => _editVersion != _savedVersion;

    // 隔离首帧 fixture 注入内存配置与刷新，不提供生产命令行开关。
    internal VoiceImeSettingsPage(Func<Task<AzureSpeechConfig>>? load = null,
        Func<AzureSpeechConfig, Task>? save = null, Action<VoiceImeSectionVM>? refresh = null,
        VoiceImeSectionVM? viewModel = null, bool unified = false)
    {
        _unified = unified;
        _load = load ?? (() => Configuration.LoadConfigAsync());
        _save = save ?? (config => Configuration.SaveConfigAsync(config));
        _refresh = refresh ?? (vm =>
        {
            // 两条原有命令独立启动；设备枚举不再挡住安装状态查询。
            vm.RefreshOptionsCommand.Execute(null);
            vm.RefreshStatusCommand.Execute(null);
        });
        _vm = viewModel ?? new VoiceImeSectionVM(_registrationService);
        _vm.Changed += OnVmChanged;
        DataContext = _vm;

        _saveStatusText = VoiceImeWindowDesign.Text("正在加载共享配置…", 12, VoiceImeWindowDesign.Muted);
        _saveStatusText.VerticalAlignment = VerticalAlignment.Center;

        _retrySaveButton = new Button
        {
            Name = "RetrySaveSettings", Content = "重试保存", MinWidth = 88,
            IsVisible = false, IsEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _retrySaveButton.Click += async (_, _) =>
        {
            if (_closed || _saving || _saveError == null || !HasUnsavedChanges) return;
            QueueSave(immediate: true); // 重试原 _save 与当前草稿，不重载配置或重建 VM。
            await _pendingSaveTask;
        };

        var openMainAppButton = new Button
        {
            Content = _unified ? "资源与登录管理" : "打开主程序管理资源与登录"
        };
        openMainAppButton.Classes.Add("voice-ime-link");
        ToolTip.SetTip(openMainAppButton, "打开主程序管理语音资源、模型、终结点与 AAD 登录。");
        openMainAppButton.Click += (_, _) => LaunchMainApplication();

        Grid footerBar;
        if (_unified)
        {
            // 保存失败时状态占满首行，低频主程序链接独占次行，不挤压窄工作区。
            footerBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnSpacing = 10, RowSpacing = 4
            };
            _retrySaveButton.MinHeight = 0;
            _retrySaveButton.Height = 32;
            openMainAppButton.MinHeight = 0;
            openMainAppButton.Height = 24;
            openMainAppButton.Padding = new Thickness(4, 0);
            openMainAppButton.FontSize = 12;
            openMainAppButton.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_retrySaveButton, 1);
            Grid.SetRow(openMainAppButton, 1);
            Grid.SetColumnSpan(openMainAppButton, 2);
            footerBar.Children.Add(_saveStatusText);
            footerBar.Children.Add(_retrySaveButton);
            footerBar.Children.Add(openMainAppButton);
        }
        else
        {
            var closeButton = new Button
            {
                Content = "完成",
                Width = 88,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            closeButton.Classes.Add("accent");
            closeButton.Click += (_, _) => RequestClose();

            footerBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                ColumnSpacing = 10
            };
            Grid.SetColumn(_saveStatusText, 0);
            Grid.SetColumn(_retrySaveButton, 1);
            Grid.SetColumn(openMainAppButton, 2);
            Grid.SetColumn(closeButton, 3);
            footerBar.Children.Add(_saveStatusText);
            footerBar.Children.Add(_retrySaveButton);
            footerBar.Children.Add(openMainAppButton);
            footerBar.Children.Add(closeButton);
        }

        _sectionView = CreateSettingsContent();
        _sectionView.IsEnabled = false;

        var scroll = new ScrollViewer
        {
            Content = _sectionView,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        scroll.PropertyChanged += (_, change) =>
        {
            // 兼容页保留原限宽；统一页按可用宽度伸展，不让长选项撑宽，外边距只扣一次。
            if (change.Property == ScrollViewer.ViewportProperty)
                _sectionView.Width = Math.Min(_sectionView.MaxWidth, Math.Max(0, scroll.Viewport.Width - _sectionView.Margin.Left - _sectionView.Margin.Right));
        };

        var footer = VoiceImeWindowDesign.Footer(footerBar);
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions(_unified ? "Auto,*,Auto" : "*,Auto")
        };
        if (_unified)
        {
            scroll.Name = "SettingsSheetScroll";
            root.Children.Add(CreateUnifiedSettingsHeader());
            Grid.SetRow(scroll, 1);
            SelectSettingsSheet(0);
        }
        Grid.SetRow(footer, _unified ? 2 : 1);
        root.Children.Add(scroll);
        root.Children.Add(footer);

        Content = root;
    }

    internal override bool CanClose => !_unified ||
        (!HasUnsavedChanges && !_saving && _pendingSaveTask.IsCompleted);

    internal override async Task PrepareToLeaveAsync()
    {
        if (!_unified)
        {
            await _pendingSaveTask;
            return;
        }
        while (true)
        {
            CancelSaveDebounce(); // 只取消尚未开始的任务；已经写盘的保存必须等到结束。
            var pendingSave = _pendingSaveTask;
            await pendingSave;
            // 等待期间仍可编辑；必须追到最新任务，不能只等进入时的快照。
            if (!ReferenceEquals(pendingSave, _pendingSaveTask)) continue;
            if (!HasUnsavedChanges) return;
            if (_saveError != null && _failedVersion == _editVersion)
                throw new InvalidOperationException("设置尚未保存，请点击“重试保存”后再离开。", _saveError);
            QueueSave(immediate: true); // 防抖已取消或前一版本失败，立即保存最新编辑。
        }
    }

    internal void UpdateLanguageSettings(VoiceImeConfig draft)
    {
        if (_closed || _config == null) return;
        // 统一 Shell 必须先完成本页 PrepareToLeaveAsync，确保没有待写设置，再接语言 Saved。
        // 只同步这三个字段；不重读配置、不 ApplyTo、不发 Changed、不写盘，不触碰隐私或凭据。
        var settings = _config.VoiceIme ??= new VoiceImeConfig();
        settings.RecognitionLanguage = draft.RecognitionLanguage;
        settings.EnableTranslation = draft.EnableTranslation;
        settings.TargetLanguage = draft.TargetLanguage;
        _vm.LoadFrom(_config); // 沿用 VM 的刷新门，更新选项不会产生虚假编辑/保存。
    }

    public override void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _closedCts.Cancel();
        _closedCts.Dispose();
        _vm.Changed -= OnVmChanged;
        // 不取消 _saveDebounceCts：已收到的最后一次修改仍按原 200ms 防抖完成保存。
    }

    internal void ShowInitializationError()
    {
        if (!_closed) _saveStatusText.Text = "设置加载未完成，请关闭后重试。";
    }

    private StackPanel CreateSettingsContent()
    {
        if (_unified) return CreateUnifiedSettingsContent();

        var prewarm = new ToggleSwitch
        {
            Name = "EnablePrewarm", OnContent = "", OffContent = "",
            VerticalAlignment = VerticalAlignment.Center
        };
        prewarm.Bind(ToggleSwitch.IsCheckedProperty, Edit(nameof(_vm.EnablePrewarm)));
        prewarm.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(prewarm, "切换到输入法时后台预建语音连接；关闭后，按 F8 时才建立连接。");
        var prewarmRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        prewarmRow.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                VoiceImeWindowDesign.Text("连接预热"),
                VoiceImeWindowDesign.Text("切换到输入法时提前连接，加快首次出字", 12, VoiceImeWindowDesign.Muted)
            }
        });
        Grid.SetColumn(prewarm, 1);
        prewarmRow.Children.Add(prewarm);

        var resource = Choice("SpeechResource", nameof(_vm.SpeechResources), nameof(_vm.SelectedResource),
            value => _vm.SelectedResource = value as VoiceImeSectionVM.ResourceOption);
        resource.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(resource, "引用现有语音资源；跟随模式在后台读取配置时解析当前活动资源。");
        var model = Choice("SpeechModel", nameof(_vm.SpeechModels), nameof(_vm.SelectedModel),
            value => _vm.SelectedModel = value as ModelOption);
        model.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(model, "仅用于输入法快照，不修改共享语音资源及其他功能的模型。");
        var connection = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                CardTitle("识别与连接"), prewarmRow, Rule(),
                SettingRow("语音资源", resource), Rule(),
                SettingRow("模型覆盖", model),
                CommandButton("刷新资源与模型", nameof(_vm.RefreshOptionsCommand))
            }
        };

        var microphone = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        microphone.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        microphone.Children.Add(Choice("Microphone", nameof(_vm.Microphones), nameof(_vm.SelectedMicrophone),
            value => _vm.SelectedMicrophone = value as AudioDeviceInfo));
        var refreshMicrophones = CommandButton("刷新设备", nameof(_vm.RefreshMicrophonesCommand));
        Grid.SetColumn(refreshMicrophones, 1);
        microphone.Children.Add(refreshMicrophones);
        var silence = new NumericUpDown
        {
            Name = "EndSilenceTimeoutSeconds", Minimum = 0.2m, Maximum = 3m, Increment = 0.1m,
            FormatString = "0.0", Width = 116, HorizontalAlignment = HorizontalAlignment.Left
        };
        silence.Bind(NumericUpDown.ValueProperty, Edit(nameof(_vm.EndSilenceTimeoutSeconds)));
        silence.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(silence, "连续静音达到该时长后提交当前句子，默认 0.8 秒；讯飞/百度以连接器实际支持为准。");
        var silenceEditor = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        silenceEditor.Children.Add(silence);
        var silenceNote = VoiceImeWindowDesign.Text("秒 · 停顿后提交当前句", 12, VoiceImeWindowDesign.Muted);
        silenceNote.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(silenceNote, 1);
        silenceEditor.Children.Add(silenceNote);
        Control languageRow;
        if (_unified)
        {
            var openLanguage = new Button
            {
                Name = "OpenLanguageSheet", Content = "语言与翻译",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(openLanguage, "调整识别语言、翻译开关与目标语言；修改后自动保存。");
            openLanguage.Click += (_, _) => RequestNavigation(VoiceImeToolTab.Language);
            languageRow = openLanguage;
        }
        else
        {
            var language = Choice("RecognitionLanguage", nameof(_vm.Languages), nameof(_vm.SelectedLanguage),
                value => _vm.SelectedLanguage = value as LanguageOption);
            language.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
            languageRow = SettingRow("识别语言", language);
        }
        var audio = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                CardTitle("声音与断句"),
                SettingRow("麦克风", microphone), Rule(),
                languageRow, Rule(),
                SettingRow("句尾静音", silenceEditor),
                VoiceImeWindowDesign.Text("仅保存资源、模型和设备引用，不复制凭据；暂时不可用的已保存引用不会因刷新被清空。", 12, VoiceImeWindowDesign.Muted),
                BoundText(nameof(_vm.MicrophoneStatus), 12, VoiceImeWindowDesign.Muted)
            }
        };

        var install = CommandButton("安装 / 修复输入法", nameof(_vm.RegisterCommand));
        install.Classes.Add("accent");
        var maintenanceButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[]
        {
            install, CommandButton("卸载输入法", nameof(_vm.UnregisterCommand)),
            CommandButton("刷新状态", nameof(_vm.RefreshStatusCommand))
        })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            maintenanceButtons.Children.Add(button);
        }
        var progress = new ProgressBar
        {
            Name = "RegistrationProgress", IsIndeterminate = true, Height = 4,
            Foreground = VoiceImeWindowDesign.Blue
        };
        progress.Bind(IsVisibleProperty, Read(nameof(_vm.IsRegistrationBusy)));
        var maintenance = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CardTitle("输入法安装管理"),
                BoundText(nameof(_vm.RegistrationStatus)),
                maintenanceButtons, progress,
                BoundText(nameof(_vm.OperationMessage), 13, VoiceImeWindowDesign.Muted),
                VoiceImeWindowDesign.Text("点击即可安装/修复或卸载；权限不足时会先提示，再由 Windows 请求管理员授权并自动继续，主窗口和当前任务保留。请使用当前用户授权，仅管理当前用户输入法。64 位 Windows 自动处理 x64 与 x86；日常 F8 听写无需管理员。", 12, VoiceImeWindowDesign.Muted),
                VoiceImeWindowDesign.Text("维护时自动暂停 F8、停止后台并切离本输入法；移除系统入口后自动重试清理旧组件，不结束你的其他应用。仍被应用持有的旧文件由 Windows 自动延后回收，无需手工切换或专门重启。主程序、共享配置和日志保留。", 12, VoiceImeWindowDesign.Muted)
            }
        };
        return new StackPanel
        {
            DataContext = _vm,
            Margin = new Thickness(24, 20), Spacing = 16,
            MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                VoiceImeWindowDesign.Card(connection), VoiceImeWindowDesign.Card(audio), VoiceImeWindowDesign.Card(maintenance),
                VoiceImeWindowDesign.Text("浮条“言”按钮可调整语言与翻译，图表按钮可查看本地用量。无需保持主程序窗口打开；与主程序共用配置，不会自动注册或设为默认输入法。", 12, VoiceImeWindowDesign.Muted)
            }
        };
    }

    private StackPanel CreateUnifiedSettingsHeader()
    {
        _settingsSheetButtons = new[]
        {
            VoiceImeWindowDesign.SheetButton("SettingsSheetRecognition", "识别", "\uE720"),
            VoiceImeWindowDesign.SheetButton("SettingsSheetSegmentation", "断句", "\uE70F"),
            VoiceImeWindowDesign.SheetButton("SettingsSheetManagement", "输入法管理", "\uE765")
        };
        var sheets = new Grid
        {
            Name = "SettingsSheetButtons",
            ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10
        };
        for (var i = 0; i < _settingsSheetButtons.Length; i++)
        {
            var sheet = i;
            var button = _settingsSheetButtons[i];
            button.MinWidth = 0;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => SelectSettingsSheet(sheet);
            Grid.SetColumn(button, i);
            sheets.Children.Add(button);
        }
        var title = VoiceImeWindowDesign.Text("设置", 28);
        title.FontWeight = FontWeight.SemiBold;
        return new StackPanel
        {
            Name = "SettingsSheetHeader", Margin = new Thickness(24, 20, 24, 0), Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children = { title, VoiceImeWindowDesign.Text("修改后自动保存", 13, VoiceImeWindowDesign.Muted) }
                },
                sheets
            }
        };
    }

    internal void SelectSettingsSheet(int sheet)
    {
        if (!_unified || _closed || sheet < 0 || sheet >= _settingsSheets.Length) return;
        SelectedSheet = sheet;
        var labels = new[] { "识别", "断句", "输入法管理" };
        for (var i = 0; i < _settingsSheets.Length; i++)
        {
            _settingsSheets[i].IsVisible = i == sheet;
            VoiceImeWindowDesign.SelectSheet(_settingsSheetButtons[i], i == sheet, labels[i]);
        }
        // 仅切换既有控件的显隐；不执行命令、不刷新选项、不产生 Changed 或保存。
    }

    private StackPanel CreateUnifiedSettingsContent()
    {
        var resource = SheetChoice("SpeechResource", nameof(_vm.SpeechResources), nameof(_vm.SelectedResource),
            value => _vm.SelectedResource = value as VoiceImeSectionVM.ResourceOption);
        resource.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(resource, "引用现有语音资源；跟随模式在后台读取配置时解析当前活动资源。");
        var model = SheetChoice("SpeechModel", nameof(_vm.SpeechModels), nameof(_vm.SelectedModel),
            value => _vm.SelectedModel = value as ModelOption);
        model.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(model, "仅用于输入法快照，不修改共享语音资源及其他功能的模型。");
        var refreshOptions = VoiceImeWindowDesign.IconButton("RefreshSpeechOptions", "\uE72C", "刷新资源与模型");
        refreshOptions.Bind(Button.CommandProperty, Read(nameof(_vm.RefreshOptionsCommand)));
        refreshOptions.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        var recognitionCard = VoiceImeWindowDesign.Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SheetCardHeading("语音识别", refreshOptions),
                SheetField("语音资源", resource),
                SheetField("模型覆盖", model),
                VoiceImeWindowDesign.Text("仅保存资源、模型和设备引用，不复制凭据；暂时不可用的已保存引用不会因刷新被清空。", 12, VoiceImeWindowDesign.Muted)
            }
        }, 20);
        recognitionCard.Name = "SettingsRecognitionResourceCard";

        var microphone = SheetChoice("Microphone", nameof(_vm.Microphones), nameof(_vm.SelectedMicrophone),
            value => _vm.SelectedMicrophone = value as AudioDeviceInfo);
        microphone.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        var refreshMicrophones = VoiceImeWindowDesign.IconButton("RefreshMicrophones", "\uE72C", "刷新设备");
        refreshMicrophones.Bind(Button.CommandProperty, Read(nameof(_vm.RefreshMicrophonesCommand)));
        refreshMicrophones.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        var prewarm = new ToggleSwitch
        {
            Name = "EnablePrewarm", OnContent = "", OffContent = "", MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        prewarm.Bind(ToggleSwitch.IsCheckedProperty, Edit(nameof(_vm.EnablePrewarm)));
        prewarm.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(prewarm, "切换到输入法时后台预建语音连接；关闭后，按 F8 时才建立连接。");
        var prewarmRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, MinHeight = 56
        };
        var prewarmLabel = VoiceImeWindowDesign.Text("连接预热");
        prewarmLabel.VerticalAlignment = VerticalAlignment.Center;
        prewarmRow.Children.Add(prewarmLabel);
        Grid.SetColumn(prewarm, 1);
        prewarmRow.Children.Add(prewarm);
        var audioCard = VoiceImeWindowDesign.Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SheetCardHeading("声音与连接", refreshMicrophones),
                SheetField("麦克风", microphone), Rule(),
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        prewarmRow,
                        VoiceImeWindowDesign.Text("切换到输入法时提前连接，加快首次出字", 12, VoiceImeWindowDesign.Muted)
                    }
                },
                BoundText(nameof(_vm.MicrophoneStatus), 12, VoiceImeWindowDesign.Muted)
            }
        }, 20);
        audioCard.Name = "SettingsRecognitionAudioCard";
        // 最小右工作区仍保留双列；卡内标签在上，选项省略显示，不把整页退成纵向。
        var recognition = new Grid
        {
            Name = "SettingsSheetRecognitionContent",
            ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16
        };
        recognition.Children.Add(recognitionCard);
        Grid.SetColumn(audioCard, 1);
        recognition.Children.Add(audioCard);

        var silence = new NumericUpDown
        {
            Name = "EndSilenceTimeoutSeconds", Minimum = 0.2m, Maximum = 3m, Increment = 0.1m,
            FormatString = "0.0", Width = 116, HorizontalAlignment = HorizontalAlignment.Left
        };
        silence.Bind(NumericUpDown.ValueProperty, Edit(nameof(_vm.EndSilenceTimeoutSeconds)));
        silence.Bind(IsEnabledProperty, Read(nameof(_vm.IsWindows)));
        ToolTip.SetTip(silence, "连续静音达到该时长后提交当前句子，默认 0.8 秒；讯飞/百度以连接器实际支持为准。");
        var silenceEditor = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        silenceEditor.Children.Add(silence);
        var silenceNote = VoiceImeWindowDesign.Text("秒 · 停顿后提交当前句", 12, VoiceImeWindowDesign.Muted);
        silenceNote.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(silenceNote, 1);
        silenceEditor.Children.Add(silenceNote);
        var segmentation = VoiceImeWindowDesign.Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                CardTitle("声音与断句"), SheetField("句尾静音", silenceEditor),
                VoiceImeWindowDesign.Text("连续静音达到设定时长后提交当前句子。可设置 0.2–3 秒，每次调整 0.1 秒，默认 0.8 秒；讯飞/百度以连接器实际支持为准。", 12, VoiceImeWindowDesign.Muted)
            }
        }, 20);
        segmentation.Name = "SettingsSheetSegmentationContent";

        var install = CommandButton("安装 / 修复输入法", nameof(_vm.RegisterCommand));
        install.Classes.Add("accent");
        var maintenanceButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[]
        {
            install, CommandButton("卸载输入法", nameof(_vm.UnregisterCommand)),
            CommandButton("刷新状态", nameof(_vm.RefreshStatusCommand))
        })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            maintenanceButtons.Children.Add(button);
        }
        var progress = new ProgressBar
        {
            Name = "RegistrationProgress", IsIndeterminate = true, Height = 4,
            Foreground = VoiceImeWindowDesign.Blue
        };
        progress.Bind(IsVisibleProperty, Read(nameof(_vm.IsRegistrationBusy)));
        var busy = VoiceImeWindowDesign.Text("正在执行输入法维护，请稍候…", 12, VoiceImeWindowDesign.Muted);
        busy.Bind(IsVisibleProperty, Read(nameof(_vm.IsRegistrationBusy)));
        var management = VoiceImeWindowDesign.Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CardTitle("输入法安装管理"), BoundText(nameof(_vm.RegistrationStatus)),
                maintenanceButtons, progress, busy,
                BoundText(nameof(_vm.OperationMessage), 13, VoiceImeWindowDesign.Muted),
                VoiceImeWindowDesign.Text("点击即可安装/修复或卸载；权限不足时会先提示，再由 Windows 请求管理员授权并自动继续，主窗口和当前任务保留。请使用当前用户授权，仅管理当前用户输入法。64 位 Windows 自动处理 x64 与 x86；日常 F8 听写无需管理员。", 12, VoiceImeWindowDesign.Muted),
                new Expander
                {
                    Name = "SettingsMaintenanceDetails", Header = "维护说明与组件清理",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = VoiceImeWindowDesign.Text("维护时自动暂停 F8、停止后台并切离本输入法；移除系统入口后自动重试清理旧组件，不结束你的其他应用。仍被应用持有的旧文件由 Windows 自动延后回收，无需手工切换或专门重启。主程序、共享配置和日志保留。无需保持主程序窗口打开；与主程序共用配置，不会自动注册或设为默认输入法。", 12, VoiceImeWindowDesign.Muted)
                }
            }
        }, 20);
        management.Name = "SettingsSheetManagementContent";

        _settingsSheets = new Control[] { recognition, segmentation, management };
        var sheets = new Grid { Name = "SettingsSheetsHost", VerticalAlignment = VerticalAlignment.Top };
        for (var i = 0; i < _settingsSheets.Length; i++)
        {
            _settingsSheets[i].IsVisible = i == SelectedSheet;
            sheets.Children.Add(_settingsSheets[i]);
        }
        return new StackPanel
        {
            DataContext = _vm, Margin = new Thickness(24, 16, 24, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { sheets }
        };
    }

    private static Grid SheetCardHeading(string title, Button refresh)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var label = CardTitle(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(label);
        Grid.SetColumn(refresh, 1);
        heading.Children.Add(refresh);
        return heading;
    }

    private static StackPanel SheetField(string title, Control editor) => new()
    {
        Spacing = 8, Children = { VoiceImeWindowDesign.Text(title), editor }
    };

    private static ComboBox SheetChoice(string name, string items, string selected, Action<object?> select)
    {
        var combo = Choice(name, items, selected, select);
        combo.MinWidth = 0;
        combo.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        combo.ItemTemplate = new FuncDataTemplate<object>((value, _) =>
        {
            var text = VoiceImeWindowDesign.Text(value?.ToString() ?? "");
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            // 选中项与弹层都受实际下拉框宽度约束；提示仍保留完整选项名称。
            text.Bind(TextBlock.MaxWidthProperty, new Binding("Bounds.Width") { Source = combo, Mode = BindingMode.OneWay });
            ToolTip.SetTip(text, value?.ToString());
            return text;
        });
        return combo;
    }

    private static Binding Read(string property) => new(property) { Mode = BindingMode.OneWay };
    private static Binding Edit(string property) => new(property) { Mode = BindingMode.TwoWay };

    private static ComboBox Choice(string name, string items, string selected, Action<object?> select)
    {
        var combo = new ComboBox { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Bind(ComboBox.ItemsSourceProperty, Read(items));
        // 选项刷新会临时清空选择；不让 TwoWay 的 null 反馈抑制随后 VM 的选中通知。
        // 有效选择通过唯一显式入口提交；VM 自身的刷新门会忽略重建期间的事件。
        combo.Bind(ComboBox.SelectedItemProperty, Read(selected));
        combo.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, combo) && combo.SelectedItem is { } value) select(value);
        };
        return combo;
    }

    private static Button CommandButton(string title, string command)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.Bind(Button.CommandProperty, Read(command));
        return button;
    }

    private static TextBlock BoundText(string property, double size = 14, IBrush? color = null)
    {
        var text = VoiceImeWindowDesign.Text("", size, color);
        text.Bind(TextBlock.TextProperty, Read(property));
        return text;
    }

    private static TextBlock CardTitle(string title)
    {
        var text = VoiceImeWindowDesign.Text(title, 19);
        text.FontWeight = FontWeight.SemiBold;
        return text;
    }

    private static Border Rule() => new() { Height = 1, Background = VoiceImeWindowDesign.Line };

    private static Grid SettingRow(string title, Control editor)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("124,*"), ColumnSpacing = 16 };
        var label = VoiceImeWindowDesign.Text(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    internal override async Task InitializeAsync()
    {
        if (_closed || _started) return;
        _started = true;
        try
        {
            var loaded = await Task.Run(_load).WaitAsync(_closedCts.Token);
            if (_closed) return;
            _config = loaded;
            _vm.LoadFrom(_config);
            _saveStatusText.Text = "已加载当前配置（修改后自动保存）";
            _refresh(_vm);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (!_closed) _saveStatusText.Text = "加载配置失败，请检查共享配置后重新打开设置。";
        }
        finally
        {
            if (!_closed) _sectionView.IsEnabled = _config != null;
        }
    }

    private void OnVmChanged()
    {
        if (_closed || _config == null) return;
        // 新编辑留在唯一 VM 中；不修改 _save 正在使用的配置对象。
        _editVersion++;
        _saveError = null;
        _retrySaveButton.IsVisible = _retrySaveButton.IsEnabled = false;
        _saveStatusText.Text = "有未保存的修改 · 修改后自动保存";
        QueueSave();
    }

    private void CancelSaveDebounce()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
    }

    private void QueueSave(bool immediate = false)
    {
        CancelSaveDebounce();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        var pendingSave = _pendingSaveTask;
        _pendingSaveTask = SaveDebouncedAsync(cts, pendingSave, immediate);
    }

    private async Task SaveDebouncedAsync(CancellationTokenSource cts, Task pendingSave, bool immediate)
    {
        var token = cts.Token;
        try
        {
            if (!immediate) await Task.Delay(200, token);
            await pendingSave;
            token.ThrowIfCancellationRequested();
            // 从这里起不再属于可取消的防抖，后续编辑只排队，不取消在途写盘。
            if (ReferenceEquals(_saveDebounceCts, cts)) _saveDebounceCts = null;
            if (_config != null && HasUnsavedChanges) await SaveCoreAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消当前防抖也不能截断更早的任务链。
            await pendingSave;
        }
        finally
        {
            if (ReferenceEquals(_saveDebounceCts, cts)) _saveDebounceCts = null;
            cts.Dispose();
        }
    }

    private async Task SaveCoreAsync()
    {
        var editVersion = _editVersion;
        _saving = true;
        _saveError = null;
        if (!_closed)
        {
            _retrySaveButton.IsVisible = _retrySaveButton.IsEnabled = false;
            _saveStatusText.Text = "正在保存…";
        }
        try
        {
            // 排到写入时才提交 VM 编辑，沿用配置对象身份及原服务的合并基线。
            _vm.ApplyTo(_config!);
            await _save(_config!); // 兼容旧窗口关闭后，已收到的最后一次编辑仍保存。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _savedVersion = editVersion; // 只确认本次写入版本，不能清掉等待期间的新编辑。
                if (!_closed) _saveStatusText.Text = HasUnsavedChanges
                    ? "有未保存的修改 · 修改后自动保存"
                    : $"已自动保存（{DateTime.Now:HH:mm:ss}）";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _saveError = ex;
                _failedVersion = editVersion;
                if (!_closed) _saveStatusText.Text = "保存失败，当前选择已保留。请检查共享配置写入权限后重试保存。";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _saving = false;
                if (!_closed)
                    _retrySaveButton.IsVisible = _retrySaveButton.IsEnabled = HasUnsavedChanges && _saveError != null;
            });
        }
    }

    private static void LaunchMainApplication()
    {
        try
        {
            string? mainAppPath = null;
            if (OperatingSystem.IsWindows())
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = user.OpenSubKey(@"Software\TrueFluentPro\VoiceImeUI") ?? user.OpenSubKey(@"Software\TrueFluentPro\VoiceIme");
                var candidate = key?.GetValue("MainAppExe") as string ?? key?.GetValue("HostExe") as string;
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    mainAppPath = candidate;
            }
            mainAppPath ??= VoiceImeUiProtocol.SourceExecutable(Path.Combine(AppContext.BaseDirectory, "TrueFluentPro.exe"));
            if (!File.Exists(mainAppPath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = mainAppPath,
                WorkingDirectory = Path.GetDirectoryName(mainAppPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch { }
    }
}

/// <summary>保留齿轮入口、原 fixture 构造与当前设置窗口弱引用。</summary>
public sealed class VoiceImeSettingsWindow : Window
{
    private readonly VoiceImeSettingsPage _page;
    private readonly CancellationTokenSource _closedCts = new();
    private static WeakReference<VoiceImeSettingsWindow>? _current;
    internal static Window? Current => _current != null && _current.TryGetTarget(out var window) && window.IsVisible ? window : null;
    private bool _closed;

    public VoiceImeSettingsWindow() : this(null, null, null) { }

    internal VoiceImeSettingsWindow(Func<Task<AzureSpeechConfig>>? load,
        Func<AzureSpeechConfig, Task>? save, Action<VoiceImeSectionVM>? refresh, VoiceImeSectionVM? viewModel = null)
    {
        _page = new VoiceImeSettingsPage(load, save, refresh, viewModel);
        var ver = VoiceImeRegistrationService.GetVoiceImeVersionString();
        Title = $"译见pro输入法设置 v{ver}";
        Width = 820;
        Height = 820;
        MinWidth = 640;
        MinHeight = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        VoiceImeWindowDesign.Apply(this);

        var paleBlue = new SolidColorBrush(Color.Parse("#B8CEE9"));
        var version = VoiceImeWindowDesign.Text("", 12, paleBlue);
        version.Bind(TextBlock.TextProperty, new Binding(nameof(VoiceImeSectionVM.ImeVersionText)) { Mode = BindingMode.OneWay });
        var headerAside = new StackPanel
        {
            MaxWidth = 220,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#25476F")),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), Child = version
                },
                VoiceImeWindowDesign.Text("F8 开始或停止听写 · 修改后自动保存", 12, paleBlue)
            }
        };
        Content = VoiceImeToolPage.Wrap(this, _page, "输入法设置",
            "配置识别与连接、声音与断句，以及输入法的安装维护。", headerAside);
        _page.CloseRequested += OnPageCloseRequested;

        var autoCloseSelfTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--self-test") >= 0;
        Opened += async (_, _) =>
        {
            _current = new WeakReference<VoiceImeSettingsWindow>(this);
            try
            {
                // Opened 不代表已经渲染；配置解析、迁移、设备/状态均让出真实第一帧。
                await VoiceImeToolPage.RenderedAsync(this, _closedCts.Token);
                await _page.InitializeAsync();
                if (autoCloseSelfTest && !_closed)
                {
                    await Task.Delay(300, _closedCts.Token);
                    Close();
                }
            }
            catch (OperationCanceledException) when (_closed) { }
            catch { if (!_closed) _page.ShowInitializationError(); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _closedCts.Cancel();
            _closedCts.Dispose();
            _page.CloseRequested -= OnPageCloseRequested;
            _page.Dispose();
            if (_current != null && _current.TryGetTarget(out var current) && ReferenceEquals(current, this)) _current = null;
        };
    }

    private void OnPageCloseRequested() => Close();
}
