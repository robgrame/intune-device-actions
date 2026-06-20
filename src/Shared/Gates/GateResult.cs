namespace IntuneDeviceActions.Gates;

/// <summary>
/// Represents the result of a cross-cutting action gate (schedule, quota, time-of-day, etc.).
/// </summary>
public record ActionGateResult
{
    /// <summary>
    /// Gate passed; action may proceed.
    /// </summary>
    public static ActionGateResult Pass() => new() { Status = ActionGateStatus.Pass };

    /// <summary>
    /// Gate deferred the action; will become available later (e.g., wave not yet fired).
    /// The action should be silently discarded (not queued, no status row).
    /// </summary>
    public static ActionGateResult Deferred(DateTimeOffset? availableAtUtc = null) => new()
    {
        Status = ActionGateStatus.Deferred,
        AvailableAtUtc = availableAtUtc,
    };

    /// <summary>
    /// Gate denied the action; a terminal denial that should result in
    /// a status row with the given denial reason.
    /// </summary>
    public static ActionGateResult Denied(string reason) => new()
    {
        Status = ActionGateStatus.Denied,
        DenialReason = reason,
    };

    /// <summary>
    /// Status of the gate check.
    /// </summary>
    public ActionGateStatus Status { get; init; }

    /// <summary>
    /// If Status == Deferred, the UTC time the action will become available.
    /// </summary>
    public DateTimeOffset? AvailableAtUtc { get; init; }

    /// <summary>
    /// If Status == Denied, the denial reason (e.g., "denied:not-enrolled-in-wave").
    /// Used for status row and audit trail.
    /// </summary>
    public string? DenialReason { get; init; }
}

public enum ActionGateStatus
{
    Pass,
    Deferred,
    Denied,
}

