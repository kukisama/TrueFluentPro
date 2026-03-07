using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RecordingMode
    {
        LoopbackOnly = 0,
        LoopbackWithMic = 1,
        MicOnly = 2
    }
}
