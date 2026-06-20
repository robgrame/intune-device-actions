using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Gates;

/// <summary>
/// Generic gate that checks device membership in an allowed Entra group.
/// Capability-agnostic: any action can use this if it provides
/// AllowedDeviceGroupId in the context.
/// </summary>
public sealed class DeviceGroupMembershipGate : IActionGate
{
    public string Name => "DeviceGroupMembershipGate";

    private readonly IGraphGroupMembershipService _graph;
    private readonly ILogger<DeviceGroupMembershipGate> _log;

    public DeviceGroupMembershipGate(IGraphGroupMembershipService graph, ILogger<DeviceGroupMembershipGate> log)
    {
        _graph = graph;
        _log = log;
    }

    public async Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct)
    {
        // If no allowed group is configured, allow (group gating is optional).
        if (string.IsNullOrEmpty(context.AllowedDeviceGroupId))
        {
            return ActionGateResult.Pass();
        }

        // If gating mode excludes device check, skip.
        if (context.GatingMode is "UserOnly")
        {
            return ActionGateResult.Pass();
        }

        try
        {
            var deviceInGroup = await _graph.IsDeviceInGroupAsync(context.DeviceObjectId, context.AllowedDeviceGroupId, ct);

            if (!deviceInGroup)
            {
                _log.LogWarning(
                    "Device group membership gate denied: device {DeviceName} ({DeviceId}) not in allowed group {GroupId}.",
                    context.DeviceName, context.EntraDeviceId, context.AllowedDeviceGroupId);
                return ActionGateResult.Denied("denied:device-not-in-allowed-group");
            }

            return ActionGateResult.Pass();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _log.LogWarning(ex, "Transient error on device group check for {Device}; will retry", context.DeviceName);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device group membership gate check failed for {Device}", context.DeviceName);
            return ActionGateResult.Denied("denied:group-check-failed");
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException or OperationCanceledException;
    }
}
