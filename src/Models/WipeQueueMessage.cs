using System.Text.Json.Serialization;

namespace IntuneWipeApi.Models;

public sealed class WipeQueueMessage
{
    [JsonPropertyName("deviceName")]       public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("entraDeviceId")]    public string EntraDeviceId { get; set; } = string.Empty;
    [JsonPropertyName("intuneDeviceId")]   public string IntuneDeviceId { get; set; } = string.Empty;
    [JsonPropertyName("correlationId")]    public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("clientCertThumbprint")] public string? ClientCertThumbprint { get; set; }
    [JsonPropertyName("requestedAt")]      public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
