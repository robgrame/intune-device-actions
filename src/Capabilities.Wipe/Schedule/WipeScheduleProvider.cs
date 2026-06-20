using IntuneDeviceActions.Schedule;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace IntuneDeviceActions.Capabilities.Wipe.Schedule;

/// <summary>
/// Adapter that exposes <see cref="WipeScheduleStore"/> through the
/// capability-agnostic <see cref="IScheduleProvider"/> contract so the core
/// <c>GET /api/schedule/me</c> endpoint can include wipe schedules in its
/// response without taking a dependency on wipe-specific types.
/// <para>
/// Wave membership is the UNION of individual device rows AND group-based waves
/// (<see cref="WipeScheduleWave.EntraGroupId"/>, resolved in real time via Graph
/// <c>checkMemberGroups</c>). The union/dedup/earliest logic lives in the store
/// so the enforcement gate and this advisory endpoint share identical semantics.
/// This advisory read path swallows lookup failures and degrades to <c>null</c>
/// so a single failing provider never breaks the aggregator response.
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
            return await _store.GetScheduleForDeviceAsync(entraDeviceId, _graph, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WipeScheduleProvider failed for {EntraDeviceId}; returning null.",
                entraDeviceId);
            return null;
        }
    }
}
