using System.Text.Json.Serialization;

namespace IntuneWipeApi.Models;

public sealed class WipeRequest
{
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("entraDeviceId")]
    public string? EntraDeviceId { get; set; }

    [JsonPropertyName("intuneDeviceId")]
    public string? IntuneDeviceId { get; set; }
}

public sealed class WipeResponse
{
    public string Status { get; set; } = "accepted";
    public string? Message { get; set; }
    public string? CorrelationId { get; set; }
}
