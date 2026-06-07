using System.Text.Json;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Capabilities.Rename.Audit;
using IntuneDeviceActions.Capabilities.Rename.Runners;
using IntuneDeviceActions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Functions;

/// <summary>
/// Dedicated per-capability consumer for the <c>rename-action</c> Service Bus
/// queue. Runs ONLY on the Rename Function App. The Proc app's
/// <see cref="RenameForwardingRunner"/> is the only producer.
/// </summary>
/// <remarks>
/// Functionally equivalent to <c>BitLockerActionConsumerFunction</c>, bound to
/// a per-capability queue, reusing the same <see cref="ActionDispatchMessage"/>
/// envelope.
/// </remarks>
public sealed class RenameActionConsumerFunction
{
    private readonly RenameActionRunner _runner;
    private readonly AuditService _audit;
    private readonly ILogger<RenameActionConsumerFunction> _log;

    public RenameActionConsumerFunction(RenameActionRunner runner, AuditService audit,
        ILogger<RenameActionConsumerFunction> log)
    {
        _runner = runner;
        _audit = audit;
        _log = log;
    }

    [Function("RenameAction")]
    public async Task Run(
        [ServiceBusTrigger("%ServiceBus:RenameActionQueue%", Connection = "ServiceBus")] string messageJson,
        CancellationToken ct)
    {
        ActionDispatchMessage env;
        try
        {
            env = JsonSerializer.Deserialize<ActionDispatchMessage>(messageJson, ActionDispatchEnqueuer.JsonOptions)
                  ?? throw new InvalidOperationException("Empty rename-action envelope.");
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.ActionInvalidEnvelope, ex, new Dictionary<string, string>
            {
                ["payloadLength"] = (messageJson?.Length ?? 0).ToString(),
            });
            // Invalid envelopes are NEVER retryable — let them poison-out fast.
            return;
        }

        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = env.CorrelationId,
            ["ActionType"]    = env.ActionType,
            ["DeviceName"]    = env.DeviceName,
        });

        _audit.TrackEvent(RenameAuditEvents.ActionConsumed, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
            [AuditEvents.Prop.ActionType]    = env.ActionType,
            [AuditEvents.Prop.DeviceName]    = env.DeviceName,
            [AuditEvents.Prop.SchemaVersion] = env.SchemaVersion,
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _runner.RunAsync(env, ct);
            sw.Stop();
            _audit.TrackEvent(RenameAuditEvents.ActionCompleted, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                ["durationMs"]                   = sw.ElapsedMilliseconds.ToString(),
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _audit.TrackEvent(RenameAuditEvents.ActionRunnerFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]    = env.CorrelationId,
                [AuditEvents.Prop.ExceptionType]    = ex.GetType().FullName ?? "(unknown)",
                [AuditEvents.Prop.ExceptionMessage] = ex.Message ?? string.Empty,
                ["failOnError"]                     = env.FailOnError.ToString(),
                ["durationMs"]                      = sw.ElapsedMilliseconds.ToString(),
            }, env.FailOnError ? LogLevel.Error : LogLevel.Warning);

            if (env.FailOnError) throw;
        }
    }
}
