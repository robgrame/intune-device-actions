using System.Text.Json.Serialization;

namespace IntuneDeviceActions.Models;

public sealed class ActionRequest
{
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("entraDeviceId")]
    public string? EntraDeviceId { get; set; }

    [JsonPropertyName("intuneDeviceId")]
    public string? IntuneDeviceId { get; set; }
}

public sealed class ActionResponse
{
    public string Status { get; set; } = "accepted";
    public string? Message { get; set; }
    public string? CorrelationId { get; set; }
}
