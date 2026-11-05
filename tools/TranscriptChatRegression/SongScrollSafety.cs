using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

internal static class SongScrollSafety
{
    internal static async Task VerifyAsync(CancellationToken token,Action<string> log)
    {
        // 与既有字幕专项相同：跳过主 VM 构造时的配置读取/语音/DI初始化，调用实际窄入口。
        var main=(MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        var config=(ConfigViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ConfigViewModel));
        var service=new HeldTranslation();
        Set(main,"<ConfigVM>k__BackingField",config);Set(main,"_config",new AzureSpeechConfig());
        Set(main,"_translationService",service);Set(main,"_realtimeTranslationServiceFactory",new HeldFactory(service));
        Set(main,"_currentOriginal","原有正文");
        foreach(var field in typeof(MainWindowViewModel).GetFields(BindingFlags.Instance|BindingFlags.NonPublic)
            .Where(f=>f.Name.StartsWith("_floating",StringComparison.Ordinal)&&f.FieldType.IsGenericType&&
                (f.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||f.FieldType.GetGenericTypeDefinition()==typeof(Queue<>))))
            field.SetValue(main,Activator.CreateInstance(field.FieldType));
        var starting=main.ToggleSongScrollTranslationAsync();
        Check(main.IsSongScrollConnecting&&service.Starts==1&&!config.IsConfigurationEnabled,"启动忙态没有及时锁定/开始一次");
        await main.ToggleSongScrollTranslationAsync();
        Check(!starting.IsCompleted&&service.Stops==0&&main.SongScrollToggleText=="正在取消","取消伪造完成，或在启动尚未返回时猜测停止契约");
        typeof(MainWindowViewModel).GetMethod("StartTranslation",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,null);
        Check(service.Starts==1,"旧入口在新页连接中重入");
        service.Started.SetResult(true);await starting.WaitAsync(token);
        Check(service.Stops==1&&!main.IsTranslating&&!main.IsSongScrollConnecting&&config.IsConfigurationEnabled,"迟到启动成功变成运行态/未真实停止");
        typeof(MainWindowViewModel).GetMethod("OnRealtimeTranslationReceived",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,new object[]{service,new TranslationItem{OriginalText="取消后的迟到帧"}});
        await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
        Check(main.CurrentOriginal=="原有正文","取消后迟到帧仍写入正文");
        log("PASS production SongScroll start gate: Starts=1; cancel waits actual startup; late success -> Stops=1, IsTranslating=false; old Start entry cannot reenter; no real audio");

        var auth=new HeldTokens(); var messages=new List<string>();
        var ai=new AiInsightViewModel(new AiInsightService(),auth,new HeldModel(),()=>new AzureSpeechConfig{AiConfig=new AiConfig()},()=>new ObservableCollection<TranslationItem>(),messages.Add,()=>Task.CompletedTask);
        try
        {
            ai.InsightMarkdown="保留结果";ai.InsightUserInput="测试问题";
            var stateChanges=0;ai.SongScrollSendCommand.CanExecuteChanged+=(_,_)=>stateChanges++;
            ai.SongScrollSendCommand.Execute(null);
            Check(ai.IsInsightLoading&&auth.Calls==1&&!ai.SongScrollSendCommand.CanExecute(null),"鉴权前缺少单飞忙态");
            ai.SongScrollSendCommand.Execute(null);Check(auth.Calls==1,"直接重复Execute发出二次鉴权");
            ai.ClearSongScrollInsight();
            Check(auth.RequestToken.IsCancellationRequested,"停止没有向鉴权服务传递真实取消令牌");
            Check(ai.InsightMarkdown==""&&ai.InsightUserInput==""&&ai.IsInsightLoading,"清除丢失语义/提前允许二次请求");
            auth.Completed.SetResult(null);
            await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
            Check(!ai.IsInsightLoading&&ai.InsightMarkdown==""&&ai.InsightUserInput==""&&stateChanges>0,"迟到鉴权回填结果/忙态命令不刷新");
            log("PASS production SongScroll insight gate: authCalls=1, busy before auth, clear -> late auth ignored, Markdown/input stay empty; no request constructed or network call");
        }
        finally{ai.Dispose();}
    }
    private static void Set(object value,string name,object target)=>typeof(MainWindowViewModel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(value,target);
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}

    private sealed class HeldTranslation:IRealtimeTranslationService
    {
        public RealtimeConnectorFamily ConnectorFamily=>RealtimeConnectorFamily.Custom;
        public int Starts,Stops;
        public TaskCompletionSource<bool> Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived{add{}remove{}}
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived{add{}remove{}}
        public event EventHandler<string>? OnStatusChanged{add{}remove{}}
        public event EventHandler<string>? OnReconnectTriggered{add{}remove{}}
        public event EventHandler<double>? OnAudioLevelUpdated{add{}remove{}}
        public event EventHandler<string>? OnDiagnosticsUpdated{add{}remove{}}
        public Task<bool> StartTranslationAsync(){Starts++;return Started.Task;}
        public Task StopTranslationAsync(){Stops++;return Task.CompletedTask;}
        public Task UpdateConfigAsync(AzureSpeechConfig config)=>throw new InvalidOperationException("专项不改服务配置");
        public bool TryApplyLiveAudioRoutingFromCurrentConfig(int fadeMilliseconds=30)=>false;
    }
    private sealed class HeldFactory(HeldTranslation service):IRealtimeTranslationServiceFactory
    {
        public bool TryResolveConnectorFamily(AzureSpeechConfig config,out RealtimeConnectorFamily family,out string error){family=RealtimeConnectorFamily.Custom;error="";return true;}
        public bool TryCreate(AzureSpeechConfig config,Action<string>? audit,out IRealtimeTranslationService? result,out string error){result=service;error="";return true;}
        public Task<RealtimeProbeResult> ProbeThirdPartyRealtimeAsync(AzureSpeechConfig config,SpeechResource resource,CancellationToken token)=>throw new InvalidOperationException("禁止探活");
        public Task<RealtimeProbeResult> ProbeThirdPartyTranslationAsync(AzureSpeechConfig config,SpeechResource resource,CancellationToken token)=>throw new InvalidOperationException("禁止探活");
    }
    private sealed class HeldTokens:IAzureTokenProviderStore
    {
        public int Calls;
        public CancellationToken RequestToken;
        public TaskCompletionSource<AzureTokenProvider?> Completed{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AzureTokenProvider GetProvider(string? profileKey=null)=>throw new InvalidOperationException("禁止读取身份");
        public Task<AzureTokenProvider?> GetAuthenticatedProviderAsync(string? profileKey,string? tenantId,string? clientId,CancellationToken cancellationToken=default){Calls++;RequestToken=cancellationToken;return Completed.Task;}
    }
    private sealed class HeldModel:IModelRuntimeResolver
    {
        public bool TryResolve(AzureSpeechConfig config,ModelReference? reference,ModelCapability capability,out ModelRuntimeResolution? runtime,out string error)
        {
            runtime=new ModelRuntimeResolution{Endpoint=new AiEndpoint{Id="isolated",Name="隔离鉴权闸门",EndpointType=EndpointApiType.AzureOpenAi,AuthMode=AzureAuthMode.AAD,BaseUrl="https://invalid.invalid"},Model=new AiModelEntry{ModelId="isolated"}};error="";return true;
        }
    }
}