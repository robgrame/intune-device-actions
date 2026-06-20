using IntuneDeviceActions.Gates;
using IntuneDeviceActions.Capabilities.Wipe.Schedule;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Capabilities.Wipe.Gates;

/// <summary>
/// Wipe-specific gate that enforces wave-based temporal gating:
/// - Devices enrolled in future waves are deferred
/// - Devices not enrolled in any active wave are denied
/// - Otherwise pass
/// </summary>
public sealed class WipeScheduleGate : IActionGate
{
    public string Name => "WipeScheduleGate";

    private readonly WipeScheduleStore? _store;
    private readonly GateErrorPolicy _errorPolicy;
    private readonly ILogger<WipeScheduleGate> _log;

    public WipeScheduleGate(WipeScheduleStore? store, IConfiguration cfg, ILogger<WipeScheduleGate> log)
    {
        _store = store;
        _errorPolicy = cfg.ReadGateErrorPolicy();
        _log = log;
    }

    public async Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct)
    {
        // If no schedule store is registered, allow (schedule gating is optional).
        if (_store is null)
        {
            return ActionGateResult.Pass();
        }

        // Only apply gating to the wipe action (other capabilities may have their own gates).
        if (context.ActionType != "wipe")
        {
            return ActionGateResult.Pass();
        }

        try
        {
            // Check if device is enrolled in any wave.
            var (defer, scheduledAtUtc) = await _store.ShouldDeferWipeAsync(context.EntraDeviceId, ct);

            if (defer && scheduledAtUtc is { } when_)
            {
                // Wave exists but is scheduled for the future → defer.
                _log.LogInformation(
                    "Wipe action deferred by schedule gate: device {DeviceName} ({DeviceId}) is enrolled in a wave that fires at {When}.",
                    context.DeviceName, context.EntraDeviceId, when_);
                return ActionGateResult.Deferred(when_);
            }

            if (!defer && scheduledAtUtc is null)
            {
                // ShouldDeferWipeAsync returns (false, null) when device is NOT in any wave.
                // Deny with reason "not-enrolled-in-wave".
                _log.LogWarning(
                    "Wipe action denied by schedule gate: device {DeviceName} ({DeviceId}) is not enrolled in any wave.",
                    context.DeviceName, context.EntraDeviceId);
                return ActionGateResult.Denied("denied:not-enrolled-in-wave");
            }

            // defer=false and scheduledAtUtc is set → wave is active (just fired or in progress).
            return ActionGateResult.Pass();
        }
        catch (Exception ex)
        {
            // Schedule lookup failure on a *destructive* action: apply the configured policy.
            // Default (FailClosed) refuses the wipe on uncertainty; operators can opt into
            // FailOpen (Actions:GateErrorPolicy=fail-open) when the client gate + ledger are
            // trusted as defense-in-depth and availability is preferred over caution.
            if (_errorPolicy == GateErrorPolicy.FailOpen)
            {
                _log.LogWarning(ex, "Wipe schedule gate lookup failed for device {Device}; failing open (policy=FailOpen).",
                    context.DeviceName);
                return ActionGateResult.Pass();
            }

            _log.LogError(ex, "Wipe schedule gate lookup failed for device {Device}; failing closed (policy=FailClosed).",
                context.DeviceName);
            return ActionGateResult.Denied("denied:schedule-lookup-failed");
        }
    }
}
