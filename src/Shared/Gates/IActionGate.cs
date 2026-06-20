namespace IntuneDeviceActions.Gates;

/// <summary>
/// Cross-cutting action gate (schedule, quota, time-of-day, etc.).
/// Each gate runs sequentially; the first Deferred/Denied stops the chain.
/// Multiple gates can be registered and run in order of registration.
/// </summary>
public interface IActionGate
{
    /// <summary>
    /// Check if a device is allowed to execute a given action at this moment.
    /// </summary>
    /// <param name="context">Gate context with device info, caller identity, and capability-specific metadata</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Gate result: Pass, Deferred, or Denied</returns>
    Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct);

    /// <summary>
    /// Human-readable name for logging/debugging (e.g., "ScheduleGate", "QuotaGate").
    /// </summary>
    string Name { get; }
}

