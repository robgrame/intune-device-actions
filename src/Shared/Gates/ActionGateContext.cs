namespace IntuneDeviceActions.Gates;

/// <summary>
/// Context passed to all IActionGate implementations during gating checks.
/// Contains device info, caller identity, and capability-specific metadata
/// that gates may need to make authorization decisions.
/// </summary>
public record ActionGateContext
{
    /// <summary>
    /// Device Entra ID (GUID).
    /// </summary>
    public required Guid EntraDeviceId { get; init; }

    /// <summary>
    /// Device Entra directory object ID (the id property from /devices endpoint).
    /// Required for group membership checks.
    /// </summary>
    public required string DeviceObjectId { get; init; }

    /// <summary>
    /// Device NetBIOS name or hostname.
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// The action type being gated (e.g., "wipe", "lock", "retire").
    /// </summary>
    public required string ActionType { get; init; }

    /// <summary>
    /// UPN of the caller who initiated the action (e.g., from cert SAN or API client).
    /// Null if the action was triggered by a scheduled/automated flow.
    /// </summary>
    public string? CallerUpn { get; init; }

    /// <summary>
    /// Correlation ID for tracing (matches ActionRequestMessage.CorrelationId).
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Capability-specific: Entra group ID that the device must belong to
    /// (if device-group membership gating is enabled).
    /// </summary>
    public string? AllowedDeviceGroupId { get; init; }

    /// <summary>
    /// Capability-specific: Entra group ID that the caller must belong to
    /// (if user-group membership gating is enabled).
    /// </summary>
    public string? AllowedUserGroupId { get; init; }

    /// <summary>
    /// Capability-specific: how to evaluate device vs. user group membership
    /// (DeviceOnly, UserOnly, Both, Either). Only relevant if group gates run.
    /// </summary>
    public string? GatingMode { get; init; }
}
