namespace IntuneDeviceActions.Actions;

/// <summary>
/// Capability-agnostic snapshot returned by <see cref="IActionStatusProbe"/>.
/// Carries the action state plus surrounding device-health context that helps
/// answer "why didn't the action complete?" — common causes are device offline
/// (high <see cref="DeviceLastSync"/> drift), out-of-compliance, or removed
/// from Intune.
/// </summary>
public sealed record ActionProbeSnapshot(
    string State,
    DateTimeOffset? ActionStartedAt,
    DateTimeOffset? ActionLastUpdated,
    DateTimeOffset? DeviceLastSync,
    string? ComplianceState,
    string? OsVersion,
    string? OperatingSystem);
