using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings
{
    public class SubscriptionSectionVM : SettingsSectionBase
    {
        private readonly AzureSubscriptionValidator _subscriptionValidator;

        private ObservableCollection<AzureSubscription> _subscriptions = new();
        private AzureSubscription? _selectedSubscription;
        private string _subscriptionEditorName = "";
        private string _subscriptionEditorKey = "";
        private string _subscriptionEditorEndpoint = "";
        private string _subscriptionEditorRegionHint = "";
        private string _subscriptionMessage = "";
        private string _testAllResult = "";
        private bool _showTestAllResult;

        public SubscriptionSectionVM(AzureSubscriptionValidator subscriptionValidator)
        {
            _subscriptionValidator = subscriptionValidator;

            AddSubscriptionCommand = new RelayCommand(async _ => await AddSubscriptionAsync());
            UpdateSubscriptionCommand = new RelayCommand(async _ => await UpdateSubscriptionAsync(), _ => SelectedSubscription != null);
            DeleteSubscriptionCommand = new RelayCommand(_ => DeleteSubscription(), _ => SelectedSubscription != null);
            TestSubscriptionCommand = new RelayCommand(async _ => await TestSubscriptionAsync());
            TestAllSubscriptionsCommand = new RelayCommand(async _ => await TestAllSubscriptionsAsync(), _ => Subscriptions.Count > 0);
        }

        public ObservableCollection<AzureSubscription> Subscriptions { get => _subscriptions; set => SetProperty(ref _subscriptions, value); }

        public AzureSubscription? SelectedSubscription
        {
            get => _selectedSubscription;
            set
            {
                if (SetProperty(ref _selectedSubscription, value))
                {
                    ((RelayCommand)UpdateSubscriptionCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DeleteSubscriptionCommand).RaiseCanExecuteChanged();
                    if (value != null) LoadSubscriptionToEditor(value);
                }
            }
        }

        public string SubscriptionEditorName { get => _subscriptionEditorName; set => SetProperty(ref _subscriptionEditorName, value); }
        public string SubscriptionEditorKey { get => _subscriptionEditorKey; set => SetProperty(ref _subscriptionEditorKey, value); }
        public string SubscriptionEditorEndpoint { get => _subscriptionEditorEndpoint;
            set => Set(ref _subscriptionEditorEndpoint, value, dirty: false, then: UpdateEndpointRegionHint); }

        public string SubscriptionEditorRegionHint { get => _subscriptionEditorRegionHint; set => SetProperty(ref _subscriptionEditorRegionHint, value); }
        public string SubscriptionMessage { get => _subscriptionMessage; set => SetProperty(ref _subscriptionMessage, value); }
        public string TestAllResult { get => _testAllResult; set => SetProperty(ref _testAllResult, value); }
        public bool ShowTestAllResult { get => _showTestAllResult; set => SetProperty(ref _showTestAllResult, value); }

        public ICommand AddSubscriptionCommand { get; }
        public ICommand UpdateSubscriptionCommand { get; }
        public ICommand DeleteSubscriptionCommand { get; }
        public ICommand TestSubscriptionCommand { get; }
        public ICommand TestAllSubscriptionsCommand { get; }

        /// <summary>内部访问配置，由宿主在 Initialize 时注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        public override void LoadFrom(AzureSpeechConfig config)
        {
            Config = config;
            _subscriptions = new ObservableCollection<AzureSubscription>(config.Subscriptions);
            OnPropertyChanged(nameof(Subscriptions));
            SelectedSubscription = _subscriptions.FirstOrDefault();
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.Subscriptions = Subscriptions.ToList();
        }

        private void SyncSubscriptionsToConfig()
        {
            Config.Subscriptions = Subscriptions.ToList();
        }

        private void LoadSubscriptionToEditor(AzureSubscription sub)
        {
            SubscriptionEditorName = sub.Name;
            SubscriptionEditorKey = sub.SubscriptionKey;
            SubscriptionEditorEndpoint = sub.GetEffectiveEndpoint();
        }

        private void UpdateEndpointRegionHint()
        {
            var ep = SubscriptionEditorEndpoint?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(ep))
            {
                SubscriptionEditorRegionHint = "";
                return;
            }
            var region = AzureSubscription.ParseRegionFromEndpoint(ep);
            if (!string.IsNullOrWhiteSpace(region))
            {
                var type = ep.Contains(".azure.cn", StringComparison.OrdinalIgnoreCase) ? "中国区" : "国际版";
                SubscriptionEditorRegionHint = $"✓ 已识别区域: {region} ({type})";
            }
            else
            {
                SubscriptionEditorRegionHint = "✗ 无法识别区域，请检查终结点格式";
            }
        }

        private async Task AddSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionEditorName) ||
                string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请填写订阅名称和密钥";
                return;
            }

            var key = SubscriptionEditorKey.Trim();
            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域，请检查格式。";
                return;
            }

            SubscriptionMessage = "验证中...";
            var (isValid, message) = await ValidateSubscriptionAsync(key, region, endpoint);
            if (!isValid)
            {
                SubscriptionMessage = $"✗ {message}";
                return;
            }

            var newSub = new AzureSubscription
            {
                Name = SubscriptionEditorName.Trim(),
                SubscriptionKey = key,
                ServiceRegion = region,
                Endpoint = endpoint
            };
            Subscriptions.Add(newSub);
            SelectedSubscription = newSub;
            SyncSubscriptionsToConfig();
            OnChanged();
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();
            SubscriptionMessage = "✓ 订阅添加成功！";
        }

        private async Task UpdateSubscriptionAsync()
        {
            if (SelectedSubscription == null)
            {
                SubscriptionMessage = "请先选择要更新的订阅";
                return;
            }
            if (string.IsNullOrWhiteSpace(SubscriptionEditorName) ||
                string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请填写订阅名称和密钥";
                return;
            }

            var key = SubscriptionEditorKey.Trim();
            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域";
                return;
            }

            SubscriptionMessage = "验证中...";
            var (isValid, message) = await ValidateSubscriptionAsync(key, region, endpoint);
            if (!isValid)
            {
                SubscriptionMessage = $"✗ {message}";
                return;
            }

            SelectedSubscription.Name = SubscriptionEditorName.Trim();
            SelectedSubscription.SubscriptionKey = key;
            SelectedSubscription.ServiceRegion = region;
            SelectedSubscription.Endpoint = endpoint;

            var idx = Subscriptions.IndexOf(SelectedSubscription);
            if (idx >= 0)
            {
                var temp = SelectedSubscription;
                Subscriptions[idx] = temp;
            }

            SyncSubscriptionsToConfig();
            OnChanged();
            SubscriptionMessage = "✓ 订阅更新成功！";
        }

        private void DeleteSubscription()
        {
            if (SelectedSubscription == null)
            {
                SubscriptionMessage = "请先选择要删除的订阅";
                return;
            }
            Subscriptions.Remove(SelectedSubscription);
            SelectedSubscription = Subscriptions.FirstOrDefault();
            SyncSubscriptionsToConfig();
            OnChanged();
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();
            SubscriptionMessage = "✓ 订阅删除成功！";
        }

        private async Task TestSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请输入订阅密钥";
                return;
            }

            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域";
                return;
            }

            SubscriptionMessage = "测试中...";
            var (isValid, message) = await ValidateSubscriptionAsync(SubscriptionEditorKey.Trim(), region, endpoint);
            SubscriptionMessage = isValid ? $"✓ {message}" : $"✗ {message}";
        }

        private async Task TestAllSubscriptionsAsync()
        {
            if (Subscriptions.Count == 0)
            {
                SubscriptionMessage = "订阅列表为空";
                return;
            }

            ShowTestAllResult = true;
            TestAllResult = "正在测试所有订阅...";

            var results = new List<(string Name, string Region, bool IsValid, long Ms, string Msg)>();
            foreach (var sub in Subscriptions)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (isValid, message) = await _subscriptionValidator.ValidateAsync(sub, CancellationToken.None);
                sw.Stop();
                results.Add((sub.Name, sub.ServiceRegion, isValid, sw.ElapsedMilliseconds, message));
            }

            results.Sort((a, b) =>
            {
                if (a.IsValid != b.IsValid) return a.IsValid ? -1 : 1;
                return a.Ms.CompareTo(b.Ms);
            });

            var text = "测试结果（按速度排序）：\n";
            foreach (var (name, region, isValid, ms, msg) in results)
            {
                text += $"{(isValid ? "✓" : "✗")} {name} ({region}) — {ms}ms";
                if (!isValid) text += $" [{msg}]";
                text += "\n";
            }

            if (results.Any(r => r.IsValid))
            {
                var fastest = results.First(r => r.IsValid);
                text += $"\n🏆 最快: {fastest.Name} ({fastest.Region}) — {fastest.Ms}ms";
            }

            TestAllResult = text.TrimEnd();
        }

        private async Task<(bool, string)> ValidateSubscriptionAsync(string key, string region, string endpoint)
        {
            var sub = new AzureSubscription
            {
                Name = "(test)",
                SubscriptionKey = key,
                ServiceRegion = region,
                Endpoint = endpoint
            };
            return await _subscriptionValidator.ValidateAsync(sub, CancellationToken.None);
        }
    }
}
