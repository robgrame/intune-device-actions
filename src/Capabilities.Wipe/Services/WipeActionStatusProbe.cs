using IntuneDeviceActions.Actions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;

namespace IntuneDeviceActions.Capabilities.Wipe.Services;

/// <summary>
/// <see cref="IActionStatusProbe"/> for the <c>wipe</c> action type. Queries
/// Graph <c>managedDevices/{id}</c> for the latest
/// <c>deviceActionResults[name=='wipe']</c> entry plus surrounding device
/// telemetry (last sync, compliance, OS) so operators can diagnose why a wipe
/// is stuck.
/// </summary>
/// <remarks>
/// Treats a Graph 404 on the managed device as the terminal state
/// <c>removedFromIntune</c> — the device is no longer in Intune, which is the
/// expected end-state of a successful wipe.
/// </remarks>
public sealed class WipeActionStatusProbe : IActionStatusProbe
{
    public string ActionType => "wipe";

    private readonly GraphServiceClient _graph;
    private readonly ILogger<WipeActionStatusProbe> _log;

    public WipeActionStatusProbe(GraphServiceClient graph, ILogger<WipeActionStatusProbe> log)
    {
        _graph = graph;
        _log = log;
    }

    public async Task<ActionProbeSnapshot> ProbeAsync(string managedDeviceId, CancellationToken ct)
    {
        try
        {
            var dev = await _graph.DeviceManagement.ManagedDevices[managedDeviceId].GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[]
                {
                    "id", "deviceName", "operatingSystem", "osVersion",
                    "complianceState", "lastSyncDateTime", "deviceActionResults"
                };
            }, cancellationToken: ct);

            DateTimeOffset? actionStart   = null;
            DateTimeOffset? actionUpdated = null;
            var state = "notReported";

            if (dev?.DeviceActionResults is { Count: > 0 } results)
            {
                var wipe = results
                    .Where(r => string.Equals(r.ActionName, "wipe", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.LastUpdatedDateTime ?? r.StartDateTime ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();

                if (wipe is not null)
                {
                    state         = wipe.ActionState?.ToString().ToLowerInvariant() ?? "unknown";
                    actionStart   = wipe.StartDateTime;
                    actionUpdated = wipe.LastUpdatedDateTime ?? wipe.StartDateTime;
                }
            }

            return new ActionProbeSnapshot(
                State:             state,
                ActionStartedAt:   actionStart,
                ActionLastUpdated: actionUpdated,
                DeviceLastSync:    dev?.LastSyncDateTime,
                ComplianceState:   dev?.ComplianceState?.ToString(),
                OsVersion:         dev?.OsVersion,
                OperatingSystem:   dev?.OperatingSystem);
        }
        catch (ODataError oe) when (oe.ResponseStatusCode == 404)
        {
            // Device removed from Intune is the expected end-state of a
            // successful wipe — fold it into the terminal state set.
            return new ActionProbeSnapshot(
                State: "removedFromIntune",
                ActionStartedAt: null,
                ActionLastUpdated: DateTimeOffset.UtcNow,
                DeviceLastSync: null,
                ComplianceState: null,
                OsVersion: null,
                OperatingSystem: null);
        }
    }
}
