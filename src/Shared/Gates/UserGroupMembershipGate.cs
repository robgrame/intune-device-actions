using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Gates;

/// <summary>
/// Generic gate that checks user membership in an allowed Entra group.
/// Capability-agnostic: any action can use this if it provides
/// AllowedUserGroupId and CallerUpn in the context.
/// </summary>
public sealed class UserGroupMembershipGate : IActionGate
{
    public string Name => "UserGroupMembershipGate";

    private readonly IGraphGroupMembershipService _graph;
    private readonly ILogger<UserGroupMembershipGate> _log;

    public UserGroupMembershipGate(IGraphGroupMembershipService graph, ILogger<UserGroupMembershipGate> log)
    {
        _graph = graph;
        _log = log;
    }

    public async Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct)
    {
        // If no allowed user group is configured, allow (user gating is optional).
        if (string.IsNullOrEmpty(context.AllowedUserGroupId))
        {
            return ActionGateResult.Pass();
        }

        // If gating mode excludes user check, skip.
        if (context.GatingMode is "DeviceOnly")
        {
            return ActionGateResult.Pass();
        }

        try
        {
            var userInGroup = await _graph.IsUserInGroupAsync(context.CallerUpn, context.AllowedUserGroupId, ct);

            if (!userInGroup)
            {
                _log.LogWarning(
                    "User group membership gate denied: caller {Caller} not in allowed user group {GroupId}.",
                    context.CallerUpn ?? "(null)", context.AllowedUserGroupId);
                return ActionGateResult.Denied("denied:user-not-in-allowed-group");
            }

            return ActionGateResult.Pass();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _log.LogWarning(ex, "Transient error on user group check for {Caller}; will retry", context.CallerUpn ?? "(null)");
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "User group membership gate check failed for {Caller}", context.CallerUpn ?? "(null)");
            return ActionGateResult.Denied("denied:user-group-check-failed");
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException or OperationCanceledException;
    }
}
