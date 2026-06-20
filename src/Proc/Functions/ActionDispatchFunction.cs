using System.Text.Json;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Gates;
using IntuneDeviceActions.Models;
using IntuneDeviceActions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;

namespace IntuneDeviceActions.Functions;

/// <summary>
/// Generic plug-in router. Consumes the <c>action-dispatch</c> storage queue,
/// resolves the matching <see cref="IActionRunner"/> via the
/// <see cref="ActionRunnerRegistry"/> and runs it.
/// </summary>
/// <remarks>
/// <para>
/// This is the only function that needs to know the contract for an action.
/// Adding a new capability never modifies this file — only DI registration
/// (<c>services.AddSingleton&lt;IActionRunner, MyRunner&gt;()</c>) and the
/// runner class itself.
/// </para>
/// <para>
/// Retry policy is driven by <see cref="ActionDispatchMessage.FailOnError"/>:
/// when <c>true</c>, exceptions bubble so the storage queue retries via
/// visibility timeout (poison queue after the host-configured max attempts).
/// </para>
/// </remarks>
public sealed class ActionDispatchFunction
{
    private readonly ActionRunnerRegistry _registry;
    private readonly ActionGateOrchestrator _gates;
    private readonly ActionStatusTracker _statusTracker;
    private readonly GraphServiceClient _graph;
    private readonly IConfiguration _cfg;
    private readonly AuditService _audit;
    private readonly ILogger<ActionDispatchFunction> _log;

    public ActionDispatchFunction(
        ActionRunnerRegistry registry,
        ActionGateOrchestrator gates,
        ActionStatusTracker statusTracker,
        GraphServiceClient graph,
        IConfiguration cfg,
        AuditService audit,
        ILogger<ActionDispatchFunction> log)
    {
        _registry = registry;
        _gates = gates;
        _statusTracker = statusTracker;
        _graph = graph;
        _cfg = cfg;
        _audit = audit;
        _log = log;
    }

    [Function("ActionDispatch")]
    public async Task Run(
        [ServiceBusTrigger("%ServiceBus:ActionDispatchQueue%", Connection = "ServiceBus")] string messageJson,
        CancellationToken ct)
    {
        ActionDispatchMessage env;
        try
        {
            _log.LogDebug("ActionDispatch raw message received: length={Length}", messageJson?.Length ?? 0);
            env = JsonSerializer.Deserialize<ActionDispatchMessage>(messageJson ?? "{}", ActionDispatchEnqueuer.JsonOptions)
                  ?? throw new InvalidOperationException("Empty dispatch envelope.");
            _log.LogDebug("ActionDispatch envelope parsed: corr={Corr} actionType={ActionType} schema={Schema} device={Device}",
                env.CorrelationId, env.ActionType, env.SchemaVersion, env.DeviceName);
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(AuditEvents.ActionDispatchInvalidEnvelope, ex, new Dictionary<string, string>
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

        _audit.TrackEvent(AuditEvents.ActionDispatchReceived, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = env.CorrelationId,
            [AuditEvents.Prop.ActionType]     = env.ActionType,
            [AuditEvents.Prop.DeviceName]     = env.DeviceName,
            [AuditEvents.Prop.SchemaVersion]  = env.SchemaVersion,
        });
        var statusMsg = BuildStatusMessage(env);

        var runner = _registry.Resolve(env.ActionType);
        _log.LogDebug("ActionDispatch runner resolution: type={ActionType} runner={Runner} knownTypes=[{Known}]",
            env.ActionType, runner?.GetType().Name ?? "(none)", string.Join(",", _registry.KnownTypes));
        if (runner is null)
        {
            _audit.TrackEvent(AuditEvents.ActionDispatchNoRunner, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                [AuditEvents.Prop.ActionType]    = env.ActionType,
                ["knownTypes"]                   = string.Join(",", _registry.KnownTypes),
            }, LogLevel.Error);
            // Unknown action type: nothing to retry → swallow so it doesn't loop.
            return;
        }

        var allowedDeviceGroupId = GetActionConfig(env.ActionType, "AllowedGroupId");
        var allowedUserGroupId = GetActionConfig(env.ActionType, "AllowedUserGroupId");
        var gatingMode = NormalizeGatingMode(GetActionConfig(env.ActionType, "GatingMode"));

        var shouldRunGates = IsGatedType(env.ActionType)
                             || !string.IsNullOrWhiteSpace(allowedDeviceGroupId)
                             || !string.IsNullOrWhiteSpace(allowedUserGroupId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (shouldRunGates)
            {
                if (!Guid.TryParse(env.EntraDeviceId, out var entraDeviceId))
                {
                    _audit.TrackEvent(AuditEvents.ScheduleGateDenied, new Dictionary<string, string>
                    {
                        [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                        [AuditEvents.Prop.ActionType] = env.ActionType,
                        [AuditEvents.Prop.DeviceName] = env.DeviceName,
                        [AuditEvents.Prop.EntraDeviceId] = env.EntraDeviceId ?? string.Empty,
                        [AuditEvents.Prop.ScheduleGateReason] = "denied:device-resolve-failed",
                    }, LogLevel.Warning);
                    await _statusTracker.RecordTerminalAsync(statusMsg, env.ActionType, "denied:device-resolve-failed", ct);
                    return;
                }

                string deviceObjectId = string.Empty;
                var needsDeviceObjectResolution = !string.IsNullOrWhiteSpace(allowedDeviceGroupId)
                                                  && !string.Equals(gatingMode, "UserOnly", StringComparison.OrdinalIgnoreCase);

                if (needsDeviceObjectResolution)
                {
                    try
                    {
                        deviceObjectId = await ResolveDeviceObjectIdAsync(env.EntraDeviceId, ct);
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        _log.LogWarning(ex, "Transient gate preflight error resolving device object id for {Device}", env.DeviceName);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _audit.TrackEvent(AuditEvents.DeniedDeviceResolveFailed, ex, new Dictionary<string, string>
                        {
                            [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                            [AuditEvents.Prop.EntraDeviceId] = env.EntraDeviceId,
                            [AuditEvents.Prop.DeviceName] = env.DeviceName,
                            [AuditEvents.Prop.ActionType] = env.ActionType,
                        });
                        await _statusTracker.RecordTerminalAsync(statusMsg, env.ActionType, "denied:device-resolve-failed", ct);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(deviceObjectId))
                    {
                        _audit.TrackEvent(AuditEvents.DeniedDeviceNotInEntra, new Dictionary<string, string>
                        {
                            [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                            [AuditEvents.Prop.EntraDeviceId] = env.EntraDeviceId,
                            [AuditEvents.Prop.DeviceName] = env.DeviceName,
                            [AuditEvents.Prop.ActionType] = env.ActionType,
                        }, LogLevel.Warning);
                        await _statusTracker.RecordTerminalAsync(statusMsg, env.ActionType, "denied:device-not-in-entra", ct);
                        return;
                    }
                }

                var gateResult = await _gates.RunAsync(new ActionGateContext
                {
                    EntraDeviceId = entraDeviceId,
                    DeviceObjectId = deviceObjectId,
                    DeviceName = env.DeviceName,
                    ActionType = env.ActionType,
                    CallerUpn = statusMsg.CallerUpn ?? env.CallerUpn,
                    CorrelationId = env.CorrelationId,
                    AllowedDeviceGroupId = allowedDeviceGroupId,
                    AllowedUserGroupId = allowedUserGroupId,
                    GatingMode = gatingMode,
                }, ct);

                if (gateResult.Status == ActionGateStatus.Deferred)
                {
                    var props = new Dictionary<string, string>
                    {
                        [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                        [AuditEvents.Prop.DeviceName] = env.DeviceName,
                        [AuditEvents.Prop.EntraDeviceId] = env.EntraDeviceId,
                        [AuditEvents.Prop.ActionType] = env.ActionType,
                    };
                    if (gateResult.AvailableAtUtc is { } whenUtc)
                    {
                        props[AuditEvents.Prop.ScheduleScheduledAtUtc] = whenUtc.ToString("O");
                        props[AuditEvents.Prop.ScheduleSecondsUntilFire] = Math.Max(0, (long)(whenUtc - DateTimeOffset.UtcNow).TotalSeconds).ToString();
                    }
                    _audit.TrackEvent(AuditEvents.ScheduleGated, props, LogLevel.Information);
                    return;
                }

                if (gateResult.Status == ActionGateStatus.Denied)
                {
                    var reason = gateResult.DenialReason ?? "denied:unknown";
                    _audit.TrackEvent(AuditEvents.ScheduleGateDenied, new Dictionary<string, string>
                    {
                        [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                        [AuditEvents.Prop.ActionType] = env.ActionType,
                        [AuditEvents.Prop.DeviceName] = env.DeviceName,
                        [AuditEvents.Prop.EntraDeviceId] = env.EntraDeviceId,
                        [AuditEvents.Prop.CallerUpn] = statusMsg.CallerUpn ?? env.CallerUpn ?? string.Empty,
                        [AuditEvents.Prop.ScheduleGateReason] = reason,
                    }, LogLevel.Warning);
                    await _statusTracker.RecordTerminalAsync(statusMsg, env.ActionType, reason, ct);
                    return;
                }
            }

            _log.LogDebug("ActionDispatch invoking runner {Runner} for corr={Corr}", runner.GetType().Name, env.CorrelationId);
            await runner.RunAsync(env, ct);
            sw.Stop();
            _log.LogDebug("ActionDispatch runner {Runner} completed in {Ms} ms corr={Corr}",
                runner.GetType().Name, sw.ElapsedMilliseconds, env.CorrelationId);
            _audit.TrackEvent(AuditEvents.ActionDispatchCompleted, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId] = env.CorrelationId,
                [AuditEvents.Prop.ActionType]    = env.ActionType,
                ["durationMs"]                   = sw.ElapsedMilliseconds.ToString(),
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _audit.TrackEvent(AuditEvents.ActionDispatchRunnerFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]    = env.CorrelationId,
                [AuditEvents.Prop.ActionType]       = env.ActionType,
                [AuditEvents.Prop.ExceptionType]    = ex.GetType().FullName ?? "(unknown)",
                [AuditEvents.Prop.ExceptionMessage] = ex.Message ?? string.Empty,
                ["failOnError"]                     = env.FailOnError.ToString(),
                ["durationMs"]                      = sw.ElapsedMilliseconds.ToString(),
            }, env.FailOnError ? LogLevel.Error : LogLevel.Warning);

            if (env.FailOnError)
            {
                throw; // queue retries via visibility timeout → poison queue after max attempts
            }
            // else: best-effort runner; swallow so we don't poison the queue
        }
    }

    private static ActionRequestMessage BuildStatusMessage(ActionDispatchMessage env)
    {
        ActionRequestMessage? msg = null;
        try
        {
            if (env.Payload.ValueKind != JsonValueKind.Undefined)
            {
                msg = env.Payload.Deserialize<ActionRequestMessage>(ActionDispatchEnqueuer.JsonOptions);
            }
        }
        catch
        {
            // fall back to the envelope fields below
        }

        msg ??= new ActionRequestMessage();

        if (string.IsNullOrWhiteSpace(msg.ActionType)) msg.ActionType = env.ActionType;
        if (string.IsNullOrWhiteSpace(msg.CorrelationId)) msg.CorrelationId = env.CorrelationId;
        if (string.IsNullOrWhiteSpace(msg.DeviceName)) msg.DeviceName = env.DeviceName;
        if (string.IsNullOrWhiteSpace(msg.EntraDeviceId)) msg.EntraDeviceId = env.EntraDeviceId;
        if (string.IsNullOrWhiteSpace(msg.IntuneDeviceId)) msg.IntuneDeviceId = env.IntuneDeviceId;
        if (string.IsNullOrWhiteSpace(msg.CallerUpn)) msg.CallerUpn = env.CallerUpn;

        return msg;
    }

    private static string NormalizeGatingMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "DeviceOnly";
        return raw.Trim().ToLowerInvariant() switch
        {
            "deviceonly" => "DeviceOnly",
            "useronly" => "UserOnly",
            "both" => "Both",
            "either" => "Either",
            _ => "DeviceOnly",
        };
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            return true;
        }

        if (ex is ODataError odata)
        {
            return odata.ResponseStatusCode == 429 || odata.ResponseStatusCode >= 500;
        }

        return false;
    }

    private async Task<string> ResolveDeviceObjectIdAsync(string entraDeviceId, CancellationToken ct)
    {
        var page = await _graph.Devices.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"deviceId eq '{entraDeviceId}'";
            req.QueryParameters.Select = new[] { "id" };
            req.QueryParameters.Top = 1;
        }, ct);

        return page?.Value?.FirstOrDefault()?.Id ?? string.Empty;
    }

    /// <summary>
    /// Decides whether the gate pipeline should run for an action type that has no
    /// device/user group configured (e.g. a schedule-only gate). Driven by the
    /// <c>Actions:GatedTypes</c> CSV so enabling gating for a new capability is an
    /// App Configuration change, not a code change. Defaults to <c>wipe</c> when the
    /// key is absent so existing behaviour is preserved with zero config.
    /// </summary>
    private bool IsGatedType(string actionType)
    {
        var raw = _cfg["Actions:GatedTypes"];
        var types = string.IsNullOrWhiteSpace(raw)
            ? new[] { "wipe" }
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return types.Any(t => string.Equals(t, actionType, StringComparison.OrdinalIgnoreCase));
    }

    private string? GetActionConfig(string actionType, string key)
    {
        // Capability-agnostic section resolution: an explicit alias
        // (Actions:ConfigSection:<actionType>) wins, then the built-in convenience
        // map, then the action type itself (IConfiguration is case-insensitive, so
        // a capability whose section name matches its action type needs no alias).
        var section = _cfg[$"Actions:ConfigSection:{actionType}"];
        if (string.IsNullOrWhiteSpace(section))
        {
            // Runbook-backed variants (e.g. "wipe-runbook", "bitlocker-rotate-runbook")
            // inherit their base capability's config section so the central gate
            // enforces the same group policy as the Function variant. The runbooks
            // run downstream of this dispatcher, so group gating lives here, not in
            // the runbook body.
            const string runbookSuffix = "-runbook";
            var baseType = actionType.EndsWith(runbookSuffix, StringComparison.OrdinalIgnoreCase)
                ? actionType[..^runbookSuffix.Length]
                : actionType;

            section = baseType.ToLowerInvariant() switch
            {
                "wipe" => "Wipe",
                "bitlocker-rotate" => "BitLocker",
                "autopilot-register" => "Autopilot",
                "device-rename" => "Rename",
                _ => baseType,
            };
        }

        return _cfg[$"{section}:{key}"];
    }
}
