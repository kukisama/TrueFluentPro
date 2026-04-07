using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AudioPreProcessorPluginType
    {
        None = 0,
        WebRtcApm = 1
    }
}
