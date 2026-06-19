using IntuneDeviceActions.Schedule;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Devices.Item.CheckMemberGroups;

namespace IntuneDeviceActions.Capabilities.Wipe.Schedule;

/// <summary>
/// Adapter that exposes <see cref="WipeScheduleStore"/> through the
/// capability-agnostic <see cref="IScheduleProvider"/> contract so the core
/// <c>GET /api/schedule/me</c> endpoint can include wipe schedules in its
/// response without taking a dependency on wipe-specific types.
/// <para>
/// Supports two membership modes:
/// <list type="bullet">
///   <item><b>Individual</b>: devices explicitly added to the members table.</item>
///   <item><b>Group-based</b>: waves with <see cref="WipeScheduleWave.EntraGroupId"/>
///     set — membership is resolved in real-time via Graph <c>checkMemberGroups</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class WipeScheduleProvider : IScheduleProvider
{
    private readonly WipeScheduleStore _store;
    private readonly GraphServiceClient? _graph;
    private readonly ILogger<WipeScheduleProvider> _log;

    public WipeScheduleProvider(WipeScheduleStore store, ILogger<WipeScheduleProvider> log,
        GraphServiceClient? graph = null)
    {
        _store = store;
        _log = log;
        _graph = graph;
    }

    public string ActionType => WipeScheduleWave.ActionTypeValue;

    public async Task<DeviceScheduleSnapshot?> GetScheduleAsync(Guid entraDeviceId, CancellationToken ct)
    {
        try
        {
            // 1) Individual membership (table-based)
            var individual = await _store.GetScheduleForDeviceAsync(entraDeviceId, ct).ConfigureAwait(false);

            // 2) Group-based membership (Graph)
            var groupSnap = await CheckGroupBasedWavesAsync(entraDeviceId, ct).ConfigureAwait(false);

            // Pick the earliest of the two (if both exist)
            if (individual is null) return groupSnap;
            if (groupSnap is null) return individual;

            // Both exist — return the one with earliest ScheduledAtUtc
            return individual.ScheduledAtUtc <= groupSnap.ScheduledAtUtc ? individual : groupSnap;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WipeScheduleProvider failed for {EntraDeviceId}; returning null.",
                entraDeviceId);
            return null;
        }
    }

    private async Task<DeviceScheduleSnapshot?> CheckGroupBasedWavesAsync(
        Guid entraDeviceId, CancellationToken ct)
    {
        if (_graph is null) return null;

        var groupWaves = await _store.GetGroupBasedWavesAsync(ct).ConfigureAwait(false);
        if (groupWaves.Count == 0) return null;

        // Resolve entraDeviceId → directory object id (needed for checkMemberGroups)
        string? objectId;
        try
        {
            var page = await _graph.Devices.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"deviceId eq '{entraDeviceId}'";
                cfg.QueryParameters.Select = new[] { "id" };
                cfg.QueryParameters.Top = 1;
            }, ct).ConfigureAwait(false);
            objectId = page?.Value?.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to resolve device object id for {EntraDeviceId}.", entraDeviceId);
            return null;
        }

        if (string.IsNullOrEmpty(objectId)) return null;

        // Check all group-based wave groups in a single call
        var groupIds = groupWaves
            .Select(w => w.EntraGroupId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> matchedGroups;
        try
        {
            var body = new CheckMemberGroupsPostRequestBody { GroupIds = groupIds };
            var result = await _graph.Devices[objectId]
                .CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct)
                .ConfigureAwait(false);
            matchedGroups = result?.Value ?? new List<string>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkMemberGroups failed for device {ObjectId}.", objectId);
            return null;
        }

        if (matchedGroups.Count == 0) return null;

        // Filter waves whose group matched
        var matched = groupWaves
            .Where(w => matchedGroups.Contains(w.EntraGroupId!, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return WipeScheduleStore.PickBestCandidate(matched);
    }
}
