using System.Text.Json.Serialization;

namespace IntuneDeviceActions.Capabilities.Rename.Models;

/// <summary>
/// Message published by the rename capability to the dedicated
/// <c>ad-object-cleanup</c> Service Bus queue and consumed by the on-prem
/// <b>hybrid worker</b> (see <c>workers/AdObjectCleanup</c>). The worker deletes
/// every on-premises Active Directory <c>computer</c> object whose name matches
/// <see cref="TargetName"/> — the name the device is about to be renamed to —
/// so a stale AD object does not block the hybrid rename on the next
/// Entra Connect / MDM sync.
/// <para>
/// The core is intentionally unaware of this type: it is a capability-owned
/// contract between the rename runner and its worker (never placed in Shared).
/// </para>
/// </summary>
public sealed class AdCleanupMessage
{
    /// <summary>Service Bus <c>ApplicationProperties["messageType"]</c> discriminator the worker asserts.</summary>
    public const string MessageType = "ad-object-cleanup";

    /// <summary>Contract schema version — bump on breaking changes so the worker can reject unknown shapes.</summary>
    public const string CurrentSchemaVersion = "1";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>End-to-end correlation id (mirrors the rename action correlation).</summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// The target device name. The worker deletes AD computer objects whose
    /// <c>name</c> / <c>sAMAccountName</c> (minus the trailing <c>$</c>) equals
    /// this value. Required — the worker no-ops on a blank value.
    /// </summary>
    [JsonPropertyName("targetName")]
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Current (pre-rename) device name — for audit/traceability only.</summary>
    [JsonPropertyName("sourceDeviceName")]
    public string? SourceDeviceName { get; set; }

    /// <summary>Entra <c>deviceId</c> GUID of the device being renamed — audit only.</summary>
    [JsonPropertyName("entraDeviceId")]
    public string? EntraDeviceId { get; set; }

    /// <summary>Intune <c>managedDevice</c> id of the device being renamed — audit only.</summary>
    [JsonPropertyName("intuneDeviceId")]
    public string? IntuneDeviceId { get; set; }

    /// <summary>Hardware serial number — audit only.</summary>
    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    /// <summary>UTC timestamp the request was enqueued.</summary>
    [JsonPropertyName("requestedUtc")]
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
}
