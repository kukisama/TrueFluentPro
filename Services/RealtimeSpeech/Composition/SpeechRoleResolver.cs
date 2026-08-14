using System;
using TrueFluentPro.Models;
using TrueFluentPro.Services.RealtimeSpeech.Asr;
using TrueFluentPro.Services.RealtimeSpeech.Translation;

namespace TrueFluentPro.Services.RealtimeSpeech.Composition
{
    /// <summary>
    /// 角色装配器：根据语音资源 + 配置，分别挑选 ASR 识别积木与 MT 翻译积木。
    /// ASR 与 MT 完全解耦——识别厂商由 ConnectorType 决定，翻译厂商由 TranslateVendor 决定，
    /// 二者可任意组合（如讯飞识别 + 百度翻译）。
    /// </summary>
    public static class SpeechRoleResolver
    {
        /// <summary>按资源的 ConnectorType 选择识别连接器。返回 null 表示该资源不是讯飞/百度级联类型。</summary>
        public static IAsrConnector? ResolveAsrConnector(SpeechResource resource)
        {
            return resource.ConnectorType switch
            {
                SpeechConnectorType.XunfeiRtasr => new XunfeiAsrConnector(),
                SpeechConnectorType.BaiduRealtimeAsr => new BaiduAsrConnector(),
                _ => null
            };
        }

        /// <summary>
        /// 按资源的 TranslateVendor 选择翻译 Provider。FollowAsr 时跟随识别同厂商；
        /// Llm 为预留值，暂回退为跟随识别。凭据统一取自资源的 Translate* 字段。
        /// </summary>
        public static ITranslationProvider ResolveTranslationProvider(
            SpeechResource resource,
            Action<string>? status = null,
            Action<string>? log = null)
        {
            var vendor = resource.TranslateVendor;
            if (vendor == SpeechTranslationVendor.FollowAsr || vendor == SpeechTranslationVendor.Llm)
            {
                // 跟随识别同厂商（Llm 预留值第一版也回退到此）。
                vendor = resource.ConnectorType switch
                {
                    SpeechConnectorType.XunfeiRtasr => SpeechTranslationVendor.Xunfei,
                    SpeechConnectorType.BaiduRealtimeAsr => SpeechTranslationVendor.Baidu,
                    _ => SpeechTranslationVendor.None
                };
            }

            return vendor switch
            {
                SpeechTranslationVendor.Baidu => new BaiduTranslationProvider(
                    translateAppId: resource.TranslateAppId,
                    fallbackAppId: resource.AppId,
                    translateApiKey: resource.TranslateApiKey,
                    translateSecret: resource.TranslateApiSecret,
                    status: status,
                    log: log),

                SpeechTranslationVendor.Xunfei => new XunfeiTranslationProvider(
                    translateAppId: resource.TranslateAppId,
                    fallbackAppId: resource.AppId,
                    translateApiKey: resource.TranslateApiKey,
                    fallbackApiKey: resource.ApiKey,
                    translateApiSecret: resource.TranslateApiSecret,
                    status: status,
                    log: log),

                _ => new NullTranslationProvider()
            };
        }
    }
}
