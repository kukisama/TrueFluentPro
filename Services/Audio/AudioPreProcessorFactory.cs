using System;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Audio
{
    public static class AudioPreProcessorFactory
    {
        public static IAudioPreProcessor Create(AzureSpeechConfig config, Action<string>? log = null)
        {
            return config.AudioPreProcessorPlugin switch
            {
                AudioPreProcessorPluginType.WebRtcApm => CreateWebRtc(config, log),
                _ => new NullAudioPreProcessor()
            };
        }

        private static IAudioPreProcessor CreateWebRtc(AzureSpeechConfig config, Action<string>? log)
        {
            var plugin = new WebRtcApmPreProcessor(config, log);
            if (!plugin.IsAvailable)
            {
                log?.Invoke($"[音频插件] WebRTC APM 当前不可用：{plugin.UnavailableReason} 已回退为关闭模式。");
                plugin.Dispose();
                return new NullAudioPreProcessor();
            }

            return plugin;
        }
    }
}
