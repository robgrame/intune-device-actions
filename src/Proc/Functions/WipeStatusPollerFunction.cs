using IntuneWipeApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Functions;

/// <summary>
/// Timer-triggered poller that drives the wipe-action status tracking loop.
/// Runs every 5 minutes by default (configurable via <c>WipeStatusPoller:CronExpression</c>).
/// </summary>
/// <remarks>
/// <para>
/// Reads all non-terminal rows from the <c>wipestatus</c> table and asks Graph
/// for the current <c>deviceActionResults[wipe].actionState</c>. State changes
/// are recorded to the audit pipeline (App Insights + auditevents table).
/// </para>
/// <para>
/// Singleton: Functions runtime guarantees only one instance of a timer
/// trigger runs across all worker instances, so we don't need to coordinate
/// poll passes across scaled-out workers.
/// </para>
/// </remarks>
public sealed class WipeStatusPollerFunction
{
    private readonly WipeStatusTracker _tracker;
    private readonly ILogger<WipeStatusPollerFunction> _log;

    public WipeStatusPollerFunction(WipeStatusTracker tracker, ILogger<WipeStatusPollerFunction> log)
    {
        _tracker = tracker;
        _log = log;
    }

    // NCRONTAB: every 5 minutes (sec min hour day month dayOfWeek). Override
    // with %WipeStatusPoller:CronExpression% app setting if needed.
    [Function("WipeStatusPoller")]
    public async Task Run(
        [TimerTrigger("%WipeStatusPoller:CronExpression%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_tracker.IsEnabled)
        {
            _log.LogDebug("WipeStatusPoller skipped: tracker not configured");
            return;
        }

        var processed = 0;
        var transitions = 0;
        await foreach (var row in _tracker.EnumeratePendingAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var beforeState = row.GetString("LastState");
            try
            {
                await _tracker.PollOneAsync(row, ct);
                processed++;
                // The row object is updated in-place by PollOneAsync for the
                // audit emit; cheap state-change detection.
                var afterState = row.GetString("LastState");
                if (!string.Equals(beforeState, afterState, StringComparison.OrdinalIgnoreCase))
                {
                    transitions++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WipeStatusPoller: unhandled error on {PK}", row.PartitionKey);
            }
        }

        _log.LogInformation("WipeStatusPoller tick: polled={Polled} transitions={Transitions}", processed, transitions);
    }
}
