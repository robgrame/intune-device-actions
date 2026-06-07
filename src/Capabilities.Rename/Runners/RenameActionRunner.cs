using System.Text.Json;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Capabilities.Rename.Audit;
using IntuneDeviceActions.Capabilities.Rename.Services;
using IntuneDeviceActions.Models;
using IntuneDeviceActions.Services;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Capabilities.Rename.Runners;

/// <summary>
/// <see cref="IActionRunner"/> for the <c>device-rename</c> action — calls the
/// customer-internal REST endpoint with the device serial number. Lives on
/// the dedicated Rename Function App (the proc role only forwards via
/// <see cref="RenameForwardingRunner"/>).
/// </summary>
/// <remarks>
/// Pipeline (simpler than the Graph capabilities — no device-resolve, no group
/// check, no ownership check: those guardrails are the customer endpoint's
/// responsibility on its side of the boundary):
/// <list type="number">
///   <item>extract + validate <c>rename</c> payload (serial + new name);</item>
///   <item>reserve idempotency ledger entry — skip if already issued / rate-limited;</item>
///   <item>POST to customer endpoint via <see cref="ICustomerRenameClient"/>;</item>
///   <item>mark ledger Issued/Failed based on classified outcome;</item>
///   <item>open the status-tracker row so <c>GET /api/actions/status</c> works.</item>
/// </list>
/// Permanent errors swallow (no queue retry); transient errors throw so the
/// per-capability Service Bus consumer retries via its built-in policy.
/// </remarks>
public sealed class RenameActionRunner : IActionRunner
{
    public string Type => "device-rename";

    private readonly ICustomerRenameClient _client;
    private readonly ActionIdempotencyService _ledger;
    private readonly AuditService _audit;
    private readonly ActionStatusTracker _statusTracker;
    private readonly ILogger<RenameActionRunner> _log;

    public RenameActionRunner(ICustomerRenameClient client, ActionIdempotencyService ledger,
        AuditService audit, ActionStatusTracker statusTracker, ILogger<RenameActionRunner> log)
    {
        _client = client;
        _ledger = ledger;
        _audit = audit;
        _statusTracker = statusTracker;
        _log = log;
    }

    public async Task RunAsync(ActionDispatchMessage envelope, CancellationToken ct)
    {
        var msg = envelope.Payload.Deserialize<ActionRequestMessage>()
            ?? throw new InvalidOperationException("Rename payload missing/invalid in dispatch envelope.");

        if (string.IsNullOrEmpty(msg.CorrelationId))  msg.CorrelationId  = envelope.CorrelationId;
        if (string.IsNullOrEmpty(msg.DeviceName))     msg.DeviceName     = envelope.DeviceName;
        if (string.IsNullOrEmpty(msg.EntraDeviceId))  msg.EntraDeviceId  = envelope.EntraDeviceId;
        if (string.IsNullOrEmpty(msg.IntuneDeviceId)) msg.IntuneDeviceId = envelope.IntuneDeviceId;

        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"]  = msg.CorrelationId,
            ["DeviceName"]     = msg.DeviceName,
            ["IntuneDeviceId"] = msg.IntuneDeviceId,
            ["ActionType"]     = Type,
        });

        _log.LogInformation("Running device-rename action for {Device}", msg.DeviceName);

        // 0) Payload validation — serial + new name are mandatory.
        var extras = RenamePayloadExtractor.TryRead(msg);
        if (extras is null || string.IsNullOrWhiteSpace(extras.SerialNumber))
        {
            _audit.TrackEvent(RenameAuditEvents.MissingSerial, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:missing-serial", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(extras.NewName))
        {
            _audit.TrackEvent(RenameAuditEvents.MissingNewName, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:missing-new-name", ct);
            return;
        }

        // 1) Idempotency reservation (rate-limited / already-issued / in-progress-elsewhere
        //    all short-circuit with a terminal status — same contract as bitlocker/wipe).
        //    Key off intuneDeviceId so rename attempts within the window collapse to
        //    the same ledger row.
        var reserve = await _ledger.ReserveAsync(msg.IntuneDeviceId, msg.CorrelationId, msg.ForceRearm, ct);
        var state = reserve.State;
        var entry = reserve.Entry;

        if (state == ActionIdempotencyService.State.RateLimited)
        {
            _audit.TrackEvent(AuditEvents.DeniedRateLimited, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]             = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]                = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId]            = msg.IntuneDeviceId,
                [AuditEvents.Prop.RecentActionsInWindow]     = reserve.RecentActionsInWindow.ToString(),
                [AuditEvents.Prop.MaxActionsPerDevicePerDay] = reserve.MaxActionsPerDevicePerDay.ToString(),
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:rate-limited", ct);
            return;
        }

        if (reserve.Rearmed != ActionIdempotencyService.RearmReason.None)
        {
            var rearmEvent = reserve.Rearmed switch
            {
                ActionIdempotencyService.RearmReason.AfterSuccess     => AuditEvents.LedgerRearmedAfterSuccess,
                ActionIdempotencyService.RearmReason.AfterFailure     => AuditEvents.LedgerRearmedAfterFailure,
                ActionIdempotencyService.RearmReason.AfterPollTimeout => AuditEvents.LedgerRearmedAfterTimeout,
                ActionIdempotencyService.RearmReason.Forced           => AuditEvents.LedgerRearmedForced,
                _                                                     => AuditEvents.LedgerRearmedAfterSuccess,
            };
            _audit.TrackEvent(rearmEvent, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]         = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]            = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId]        = msg.IntuneDeviceId,
                [AuditEvents.Prop.ActionSequence]        = entry.ActionSequence.ToString(),
                [AuditEvents.Prop.PreviousTerminalState] = entry.LastTerminalState ?? "(unknown)",
                [AuditEvents.Prop.RearmReason]           = reserve.Rearmed.ToString(),
            });
        }

        if (state == ActionIdempotencyService.State.Issued)
        {
            _audit.TrackEvent(AuditEvents.ActionAlreadyIssued, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]         = msg.CorrelationId,
                [AuditEvents.Prop.OriginalCorrelationId] = entry.CorrelationId,
                [AuditEvents.Prop.DeviceName]            = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId]        = msg.IntuneDeviceId,
                [AuditEvents.Prop.ActionSequence]        = entry.ActionSequence.ToString(),
            });
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:already-issued", ct);
            return;
        }
        if (state == ActionIdempotencyService.State.Reserved && entry.CorrelationId != msg.CorrelationId)
        {
            _audit.TrackEvent(AuditEvents.ActionInProgressElsewhere, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]         = msg.CorrelationId,
                [AuditEvents.Prop.OriginalCorrelationId] = entry.CorrelationId,
                [AuditEvents.Prop.DeviceName]            = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId]        = msg.IntuneDeviceId,
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:in-progress-elsewhere", ct);
            return;
        }

        // 2) Call customer REST endpoint
        var restReq = new RenameRestRequest(
            SerialNumber:   extras.SerialNumber.Trim(),
            NewName:        extras.NewName.Trim(),
            CorrelationId:  msg.CorrelationId,
            IntuneDeviceId: string.IsNullOrEmpty(msg.IntuneDeviceId) ? null : msg.IntuneDeviceId,
            DeviceName:     string.IsNullOrEmpty(msg.DeviceName)     ? null : msg.DeviceName);

        RenameRestOutcome outcome;
        try
        {
            outcome = await _client.RenameAsync(restReq, ct);
        }
        catch (Exception ex)
        {
            // Unexpected exception from the client (should be classified into
            // an outcome, but defend in depth): treat as transient.
            _audit.TrackEvent(RenameAuditEvents.RestTransientError, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]   = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                         = restReq.SerialNumber,
            }, LogLevel.Warning);
            throw;
        }

        if (outcome.OutcomeKind == RenameRestOutcome.Kind.Accepted)
        {
            await _ledger.MarkIssuedAsync(msg.IntuneDeviceId, msg.CorrelationId, ct);
            _audit.TrackEvent(RenameAuditEvents.RestIssued, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.EntraDeviceId]  = msg.EntraDeviceId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = restReq.SerialNumber,
                ["newName"]                       = restReq.NewName,
                ["httpStatus"]                    = outcome.StatusCode.ToString(),
            });

            try { await _statusTracker.InitializeAsync(msg, Type, restReq.SerialNumber, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Status tracker initialization failed for {Corr}", msg.CorrelationId); }
            return;
        }

        if (outcome.OutcomeKind == RenameRestOutcome.Kind.Permanent)
        {
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, outcome.Reason, ct);
            _audit.TrackEvent(RenameAuditEvents.RestFailedPermanent, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = restReq.SerialNumber,
                ["httpStatus"]                    = outcome.StatusCode.ToString(),
                ["reason"]                        = outcome.Reason,
            });
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:permanent", ct, restReq.SerialNumber);
            // Do not throw — no retry on permanent errors.
            return;
        }

        // Transient — emit the audit and throw so the SB consumer retries.
        _audit.TrackEvent(RenameAuditEvents.RestTransientError, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
            [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
            [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            ["serial"]                        = restReq.SerialNumber,
            ["httpStatus"]                    = outcome.StatusCode.ToString(),
            ["reason"]                        = outcome.Reason,
        }, LogLevel.Warning);
        throw new HttpRequestException(
            $"Customer rename endpoint returned transient outcome (status={outcome.StatusCode}, reason={outcome.Reason}).");
    }
}
