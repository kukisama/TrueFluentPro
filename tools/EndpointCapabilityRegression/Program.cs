using System.Reflection;
using System.Text.Json;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels.Settings;

var restore = typeof(EndpointsSectionVM).GetMethod(
    "RestoreUnclassifiedAzureModels", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("未找到 AAD 分类恢复入口");

static AiModelEntry Model(string id, ModelCapability capability = ModelCapability.None)
    => new() { ModelId = id, Capabilities = capability };

var apim = new AiEndpoint
{
    EndpointType = EndpointApiType.ApiManagementGateway,
    Models = [Model("gpt-6-astra", ModelCapability.Text), Model("gpt-image-2", ModelCapability.Image), Model("conflict", ModelCapability.Text)]
};
var aad = new AiEndpoint
{
    EndpointType = EndpointApiType.AzureOpenAi,
    AuthMode = AzureAuthMode.AAD,
    Models = [Model("gpt-6-astra"), Model("gpt-image-2"), Model("sora-2"), Model("unknown"), Model("conflict")]
};
var existing = new AiEndpoint
{
    EndpointType = EndpointApiType.AzureOpenAi,
    AuthMode = AzureAuthMode.AAD,
    Models = [Model("gpt-6-astra", ModelCapability.Text), Model("conflict", ModelCapability.Image), Model("unknown")]
};
var endpoints = new List<AiEndpoint> { apim, aad, existing };
var changed = (bool)(restore.Invoke(null, [endpoints]) ?? false);
if (!changed || aad.Models[0].Capabilities != ModelCapability.Text
    || aad.Models[1].Capabilities != ModelCapability.Image
    || aad.Models[2].Capabilities != ModelCapability.Video
    || aad.Models[3].Capabilities != ModelCapability.None
    || aad.Models[4].Capabilities != ModelCapability.None
    || existing.Models[0].Capabilities != ModelCapability.Text
    || existing.Models[1].Capabilities != ModelCapability.Image
    || existing.Models[2].Capabilities != ModelCapability.None
    || apim.Models[0].Capabilities != ModelCapability.Text)
    throw new Exception("AAD 分类恢复错误或改写了原有分类");

if ((bool)(restore.Invoke(null, [endpoints]) ?? true))
    throw new Exception("重复加载不应再次恢复分类");

var roundTrip = JsonSerializer.Deserialize<List<AiEndpoint>>(JsonSerializer.Serialize(endpoints))!;
if (roundTrip[1].Models[0].Capabilities != ModelCapability.Text
    || roundTrip[1].Models[1].Capabilities != ModelCapability.Image
    || roundTrip[1].Models[2].Capabilities != ModelCapability.Video)
    throw new Exception("序列化往返丢失模型分类");

Console.WriteLine("AAD 分类恢复、保留已有值、幂等与持久化往返通过");