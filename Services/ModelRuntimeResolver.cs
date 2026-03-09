using System;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public class ModelRuntimeResolver : IModelRuntimeResolver
    {
        public bool TryResolve(
            AzureSpeechConfig config,
            ModelReference? reference,
            ModelCapability capability,
            out ModelRuntimeResolution? runtime,
            out string errorMessage)
        {
            runtime = null;
            errorMessage = "";

            if (config == null)
            {
                errorMessage = "配置为空，无法解析模型引用。";
                return false;
            }

            if (reference == null
                || string.IsNullOrWhiteSpace(reference.EndpointId)
                || string.IsNullOrWhiteSpace(reference.ModelId))
            {
                errorMessage = $"未配置{GetCapabilityName(capability)}模型引用。";
                return false;
            }

            var endpoint = config.Endpoints.FirstOrDefault(e => e.Id == reference.EndpointId);
            if (endpoint == null)
            {
                errorMessage = $"模型引用无效：找不到终结点“{reference.EndpointId}”。";
                return false;
            }

            if (!endpoint.IsEnabled)
            {
                errorMessage = $"模型引用无效：终结点“{GetEndpointName(endpoint)}”已被禁用。";
                return false;
            }

            var model = endpoint.Models.FirstOrDefault(m => m.ModelId == reference.ModelId);
            if (model == null)
            {
                errorMessage = $"模型引用无效：终结点“{GetEndpointName(endpoint)}”下不存在模型“{reference.ModelId}”。";
                return false;
            }

            if (!model.Capabilities.HasFlag(capability))
            {
                errorMessage = $"模型“{GetModelName(model)}”未配置{GetCapabilityName(capability)}能力。";
                return false;
            }

            runtime = new ModelRuntimeResolution
            {
                Endpoint = endpoint,
                Model = model,
                Capability = capability
            };

            return true;
        }

        private static string GetCapabilityName(ModelCapability capability)
            => capability switch
            {
                ModelCapability.Text => "文本",
                ModelCapability.Image => "图片",
                ModelCapability.Video => "视频",
                ModelCapability.SpeechToText => "语音转写",
                ModelCapability.TextToSpeech => "文字转语音",
                _ => capability.ToString()
            };

        private static string GetEndpointName(AiEndpoint endpoint)
            => string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Id : endpoint.Name;

        private static string GetModelName(AiModelEntry model)
            => string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId : model.DisplayName;
    }
}