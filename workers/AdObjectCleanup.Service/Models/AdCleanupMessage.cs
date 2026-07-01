using System.Text.Json.Serialization;

namespace IntuneDeviceActions.Workers.AdObjectCleanup.Models;

/// <summary>
/// Wire contract published by the Intune rename capability to the
/// <c>ad-object-cleanup</c> Service Bus queue. Mirrors
/// <c>src/Capabilities.Rename/Models/AdCleanupMessage.cs</c> — the capability
/// owns the contract; this worker is a consumer and keeps its own DTO so it can
/// be deployed on-prem without referencing the Azure capability assembly.
/// </summary>
public sealed class AdCleanupMessage
{
    public const string MessageType = "ad-object-cleanup";

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>The device name whose AD computer object(s) must be deleted.</summary>
    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    [JsonPropertyName("sourceDeviceName")]
    public string? SourceDeviceName { get; set; }

    [JsonPropertyName("entraDeviceId")]
    public string? EntraDeviceId { get; set; }

    [JsonPropertyName("intuneDeviceId")]
    public string? IntuneDeviceId { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("requestedUtc")]
    public DateTimeOffset? RequestedUtc { get; set; }
}
