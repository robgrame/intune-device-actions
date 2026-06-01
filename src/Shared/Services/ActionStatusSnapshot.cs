namespace IntuneDeviceActions.Services;

/// <summary>
/// Read-only DTO for the wipe-action status returned by the GET status endpoint.
/// </summary>
public sealed record ActionStatusSnapshot(
    string CorrelationId,
    string DeviceName,
    string EntraDeviceId,
    string IntuneDeviceId,
    string ManagedDeviceId,
    string LastState,
    string PreviousState,
    bool Terminal,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastPolledAt,
    DateTimeOffset LastChangedAt,
    int PollAttempts);
