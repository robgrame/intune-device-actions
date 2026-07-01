using System.Text.Json;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Capabilities.Rename.Audit;
using IntuneDeviceActions.Capabilities.Rename.Services;
using IntuneDeviceActions.Models;
using IntuneDeviceActions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Capabilities.Rename.Runners;

/// <summary>
/// <see cref="IActionRunner"/> for the <c>device-rename</c> action. Lives on
/// the dedicated Rename Function App (the proc role only forwards via
/// <see cref="RenameForwardingRunner"/>).
/// </summary>
/// <remarks>
/// Pipeline (LOOKUP + Graph):
/// <list type="number">
///   <item>extract + validate <c>rename</c> payload (serial required);</item>
///   <item>reserve idempotency ledger entry — skip if already issued / rate-limited;</item>
///   <item>LOOKUP the canonical new name from the customer CMDB via
///         <see cref="ICustomerRenameClient"/> (GET serial → newName);</item>
///   <item>collision check — query Entra for existing devices with the same
///         <c>displayName</c> (Entra does not enforce uniqueness on device
///         displayName, unlike on-prem AD). Behaviour controlled by
///         <c>Rename:OnCollision</c> (<c>block</c> | <c>warn</c>);</item>
///   <item>call Microsoft Graph
///         <c>POST /deviceManagement/managedDevices/{id}/setDeviceName</c>;</item>
///   <item>mark ledger Issued/Failed based on classified outcome;</item>
///   <item>open the status-tracker row so <c>GET /api/actions/status</c> works.</item>
/// </list>
/// Permanent errors (lookup NotFound/permanent, Graph 4xx other than 408/429,
/// collision blocked) are swallowed (no queue retry); transient errors throw
/// so the per-capability Service Bus consumer retries via its built-in policy.
/// </remarks>
public sealed class RenameActionRunner : IActionRunner
{
    public string Type => "device-rename";

    private readonly ICustomerRenameClient _customer;
    private readonly GraphRenameService _graph;
    private readonly ActionIdempotencyService _ledger;
    private readonly AuditService _audit;
    private readonly ActionStatusTracker _statusTracker;
    private readonly IConfiguration _cfg;
    private readonly ILogger<RenameActionRunner> _log;

    public RenameActionRunner(ICustomerRenameClient customer, GraphRenameService graph,
        ActionIdempotencyService ledger, AuditService audit, ActionStatusTracker statusTracker,
        IConfiguration cfg, ILogger<RenameActionRunner> log)
    {
        _customer = customer;
        _graph = graph;
        _ledger = ledger;
        _audit = audit;
        _statusTracker = statusTracker;
        _cfg = cfg;
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

        // 0) Payload validation — serial mandatory; intuneDeviceId mandatory for Graph call.
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
        if (string.IsNullOrWhiteSpace(msg.IntuneDeviceId))
        {
            _audit.TrackEvent(RenameAuditEvents.MissingIntuneDeviceId, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                ["serial"]                        = extras.SerialNumber,
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:missing-intune-device-id", ct);
            return;
        }

        var serial = extras.SerialNumber.Trim();

        // 1) Idempotency reservation — same contract as bitlocker/wipe.
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
                [AuditEvents.Prop.ActionType]                = Type,
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

        // 2) LOOKUP — ask the customer CMDB for the canonical new name.
        RenameLookupOutcome lookup;
        try
        {
            lookup = await _customer.ResolveNewNameAsync(serial, msg.CorrelationId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Configuration error (e.g. missing Rename:Endpoint) — permanent.
            // Mark the ledger failed so the rate-limiter doesn't keep eating
            // attempts and surface a terminal status the caller can read.
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"config-error:{ex.Message}", ct);
            _audit.TrackEvent(RenameAuditEvents.LookupFailedPermanent, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["reason"]                        = "config-error",
            }, LogLevel.Error);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:config-error", ct, msg.IntuneDeviceId);
            return;
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.LookupTransientError, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
            }, LogLevel.Warning);
            throw;
        }

        if (lookup.OutcomeKind == RenameLookupOutcome.Kind.NotFound)
        {
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, lookup.Reason, ct);
            _audit.TrackEvent(RenameAuditEvents.LookupNotFound, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["httpStatus"]                    = lookup.StatusCode.ToString(),
            }, LogLevel.Warning);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:lookup-not-found", ct, msg.IntuneDeviceId);
            return;
        }
        if (lookup.OutcomeKind == RenameLookupOutcome.Kind.Permanent)
        {
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, lookup.Reason, ct);
            _audit.TrackEvent(RenameAuditEvents.LookupFailedPermanent, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["httpStatus"]                    = lookup.StatusCode.ToString(),
                ["reason"]                        = lookup.Reason,
            }, LogLevel.Error);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:lookup-permanent", ct, msg.IntuneDeviceId);
            return;
        }
        if (lookup.OutcomeKind == RenameLookupOutcome.Kind.Transient)
        {
            _audit.TrackEvent(RenameAuditEvents.LookupTransientError, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["httpStatus"]                    = lookup.StatusCode.ToString(),
                ["reason"]                        = lookup.Reason,
            }, LogLevel.Warning);
            throw new HttpRequestException(
                $"Customer rename lookup returned transient outcome (status={lookup.StatusCode}, reason={lookup.Reason}).");
        }

        var newName = lookup.NewName!;
        _audit.TrackEvent(RenameAuditEvents.LookupIssued, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
            [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            ["serial"]                        = serial,
            ["newName"]                       = newName,
        });

        // 3) Pre-rename directory cleanup (default) OR legacy collision check.
        //    Entra does NOT enforce uniqueness on device displayName (unlike
        //    on-prem AD), so a hybrid-joined device renamed to a name that a
        //    stale directory object still holds — or a machine that has picked
        //    up several duplicate Entra device objects sharing the same
        //    hardware id — must be cleaned up BEFORE the rename or the next
        //    Entra Connect / MDM sync will collide. When
        //    Rename:PreRenameCleanup=disabled we fall back to the original
        //    non-destructive collision block/warn behaviour.
        var cleanupMode = (_cfg["Rename:PreRenameCleanup"] ?? "enabled").Trim().ToLowerInvariant();
        bool proceed = cleanupMode == "disabled"
            ? await LegacyCollisionCheckAsync(msg, newName, ct)
            : await RunPreRenameCleanupAsync(msg, newName, ct);
        if (!proceed) return;

        // 4) Graph setDeviceName — Intune queues the rename for the next MDM sync.
        //    There is no first-party probe for setDeviceName completion (the
        //    Intune managedDevice eventually shows the new deviceName, but the
        //    timing depends on the MDM sync cycle + the OS reboot for Windows).
        //    Record a terminal "issued" status here rather than initializing a
        //    pending row (no probe is registered for "device-rename" anyway —
        //    initializing pending would leave the row stuck until the rolling
        //    24h window expires).
        try
        {
            await _graph.SetDeviceNameAsync(msg.IntuneDeviceId, newName, ct);
            await _ledger.MarkIssuedAsync(msg.IntuneDeviceId, msg.CorrelationId, ct);
            _audit.TrackEvent(RenameAuditEvents.GraphSetNameIssued, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.DeviceName]     = msg.DeviceName,
                [AuditEvents.Prop.EntraDeviceId]  = msg.EntraDeviceId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["newName"]                       = newName,
            });
            await _statusTracker.RecordTerminalAsync(msg, Type, "issued", ct, msg.IntuneDeviceId);
        }
        catch (Exception ex)
        {
            if (GraphRenameService.Classify(ex) == GraphErrorClassifier.GraphErrorKind.Permanent)
            {
                await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, ex.Message, ct);
                _audit.TrackEvent(RenameAuditEvents.GraphSetNameFailedPermanent, ex, new Dictionary<string, string>
                {
                    [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                    [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                    ["serial"]                        = serial,
                    ["newName"]                       = newName,
                }, LogLevel.Error);
                await _statusTracker.RecordTerminalAsync(msg, Type, "failed:permanent", ct, msg.IntuneDeviceId);
                return;
            }
            _audit.TrackEvent(RenameAuditEvents.GraphSetNameTransientError, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["serial"]                        = serial,
                ["newName"]                       = newName,
            }, LogLevel.Warning);
            throw;
        }
    }

    /// <summary>
    /// Pre-rename directory cleanup. Deletes, via Microsoft Graph:
    /// <list type="number">
    ///   <item>the stale <b>AD-name shadows</b> — Entra device objects whose
    ///         <c>displayName</c> equals the target <paramref name="newName"/>
    ///         and whose <c>trustType</c> is configured as an "AD" type
    ///         (default <c>ServerAd</c>), excluding the device itself;</item>
    ///   <item>the <b>HWID duplicates</b> — every Entra device sharing the
    ///         current device's <c>[HWID]</c>, EXCEPT the Entra ID Joined object
    ///         (<c>trustType == AzureAd</c>) and the device itself (the Hybrid
    ///         Joined object).</item>
    /// </list>
    /// Returns <c>true</c> to continue to <c>setDeviceName</c>, <c>false</c> when
    /// a terminal status was already recorded (caller must stop). Transient
    /// Graph errors are re-thrown so the Service Bus consumer retries.
    /// Requires the <c>Device.ReadWrite.All</c> Graph permission on the Rename UAMI.
    /// </summary>
    private async Task<bool> RunPreRenameCleanupAsync(ActionRequestMessage msg, string newName, CancellationToken ct)
    {
        var selfDeviceId = msg.EntraDeviceId;
        if (string.IsNullOrWhiteSpace(selfDeviceId))
        {
            // Without the device's own Entra deviceId we cannot safely exclude
            // "self" from the delete set — skip cleanup rather than risk
            // deleting the object we are about to rename.
            _audit.TrackEvent(RenameAuditEvents.CleanupSkipped, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["reason"]                        = "missing-entra-device-id",
            }, LogLevel.Warning);
            return await IntuneCollisionCheckAsync(msg, newName, ct);
        }

        var doAdName = !string.Equals((_cfg["Rename:AdNameCleanup"] ?? "enabled").Trim(), "disabled", StringComparison.OrdinalIgnoreCase);
        var doHwid   = !string.Equals((_cfg["Rename:HwidCleanup"]   ?? "enabled").Trim(), "disabled", StringComparison.OrdinalIgnoreCase);
        var trustTypes = RenameCleanupPlanner.ParseTrustTypes(_cfg["Rename:AdNameCleanupTrustTypes"]);
        var maxDelete = int.TryParse(_cfg["Rename:MaxDeletePerCleanup"], out var m) && m > 0 ? m : 25;
        var allowLarge = string.Equals((_cfg["Rename:AllowLargeCleanup"] ?? "false").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var onFailure = (_cfg["Rename:OnCleanupFailure"] ?? "block").Trim().ToLowerInvariant();

        List<EntraDeviceRecord> adDeletions = new();
        List<EntraDeviceRecord> hwidDeletions = new();
        string? hwid = null;

        try
        {
            if (doAdName)
            {
                var matches = await _graph.FindDevicesByDisplayNameAsync(newName, ct);
                adDeletions = RenameCleanupPlanner.PlanAdNameDeletions(matches, selfDeviceId, trustTypes).ToList();
            }

            if (doHwid)
            {
                hwid = await _graph.GetDeviceHwidAsync(selfDeviceId, ct);
                if (!string.IsNullOrEmpty(hwid))
                {
                    var shared = await _graph.FindDevicesByHwidAsync(hwid, ct);
                    var plan = RenameCleanupPlanner.PlanHwidDeletions(shared, selfDeviceId);
                    // Never delete an object that is already queued for AD-name
                    // deletion (dedupe by object id) — avoids a double DELETE.
                    var adIds = new HashSet<string>(adDeletions.Select(d => d.ObjectId), StringComparer.OrdinalIgnoreCase);
                    hwidDeletions = plan.Delete.Where(d => !adIds.Contains(d.ObjectId)).ToList();
                }
                else
                {
                    _audit.TrackEvent(RenameAuditEvents.CleanupSkipped, new Dictionary<string, string>
                    {
                        [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                        [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                        ["newName"]                       = newName,
                        ["reason"]                        = "no-hwid-on-self",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.CleanupFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["phase"]                         = "plan",
            }, LogLevel.Warning);
            if (GraphRenameService.Classify(ex) == GraphErrorClassifier.GraphErrorKind.Transient) throw;
            if (onFailure == "warn") return await IntuneCollisionCheckAsync(msg, newName, ct);
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"cleanup-plan-failed:{ex.Message}", ct);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:cleanup", ct, msg.IntuneDeviceId);
            return false;
        }

        var totalToDelete = adDeletions.Count + hwidDeletions.Count;

        // Guardrail: refuse an over-large delete unless explicitly allowed.
        if (totalToDelete > maxDelete && !allowLarge)
        {
            _audit.TrackEvent(RenameAuditEvents.CleanupCapExceeded, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["plannedDeletions"]              = totalToDelete.ToString(),
                ["maxDelete"]                     = maxDelete.ToString(),
            }, LogLevel.Error);
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"cleanup-cap-exceeded:{totalToDelete}>{maxDelete}", ct);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:cleanup-cap", ct, msg.IntuneDeviceId);
            return false;
        }

        // Execute deletions. A failed DELETE is classified: transient → throw
        // (retry the whole action); permanent → honour Rename:OnCleanupFailure.
        var adDeleted = 0;
        var hwidDeleted = 0;
        try
        {
            foreach (var d in adDeletions)
            {
                await _graph.DeleteDeviceAsync(d.ObjectId, ct);
                adDeleted++;
                _audit.TrackEvent(RenameAuditEvents.CleanupAdNameDeleted, new Dictionary<string, string>
                {
                    [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                    [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                    ["newName"]                       = newName,
                    ["deletedObjectId"]               = d.ObjectId,
                    ["deletedDeviceId"]               = d.DeviceId,
                    ["deletedDisplayName"]            = d.DisplayName,
                    ["deletedTrustType"]              = d.TrustType ?? "(none)",
                });
            }

            foreach (var d in hwidDeletions)
            {
                await _graph.DeleteDeviceAsync(d.ObjectId, ct);
                hwidDeleted++;
                _audit.TrackEvent(RenameAuditEvents.CleanupHwidDeleted, new Dictionary<string, string>
                {
                    [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                    [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                    ["hwid"]                          = hwid ?? "(unknown)",
                    ["deletedObjectId"]               = d.ObjectId,
                    ["deletedDeviceId"]               = d.DeviceId,
                    ["deletedDisplayName"]            = d.DisplayName,
                    ["deletedTrustType"]              = d.TrustType ?? "(none)",
                });
            }
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.CleanupFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["phase"]                         = "delete",
                ["adDeleted"]                     = adDeleted.ToString(),
                ["hwidDeleted"]                   = hwidDeleted.ToString(),
            }, LogLevel.Warning);
            if (GraphRenameService.Classify(ex) == GraphErrorClassifier.GraphErrorKind.Transient) throw;
            if (onFailure == "warn") return await IntuneCollisionCheckAsync(msg, newName, ct);
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"cleanup-delete-failed:{ex.Message}", ct);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:cleanup", ct, msg.IntuneDeviceId);
            return false;
        }

        _audit.TrackEvent(RenameAuditEvents.CleanupCompleted, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
            [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            ["newName"]                       = newName,
            ["adDeleted"]                     = adDeleted.ToString(),
            ["hwidDeleted"]                   = hwidDeleted.ToString(),
            ["hwid"]                          = hwid ?? "(none)",
        });

        // Still block on an Intune-side managedDevice name collision (deleting
        // Intune records is intentionally out of scope for the cleanup).
        return await IntuneCollisionCheckAsync(msg, newName, ct);
    }

    /// <summary>
    /// Non-destructive Intune-side collision guard: blocks (or warns, per
    /// <c>Rename:OnCollision</c>) when another Intune <c>managedDevice</c>
    /// already carries the target <paramref name="newName"/>. Returns
    /// <c>true</c> to proceed, <c>false</c> when a terminal status was recorded.
    /// </summary>
    private async Task<bool> IntuneCollisionCheckAsync(ActionRequestMessage msg, string newName, CancellationToken ct)
    {
        IReadOnlyList<DeviceCollision> intuneCollisions;
        try
        {
            intuneCollisions = await _graph.FindManagedDeviceNameCollisionsAsync(newName, msg.IntuneDeviceId, ct);
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.CollisionCheckFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
            }, LogLevel.Warning);
            if (GraphRenameService.Classify(ex) == GraphErrorClassifier.GraphErrorKind.Transient) throw;
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"collision-check-failed:{ex.Message}", ct);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:collision-check", ct, msg.IntuneDeviceId);
            return false;
        }

        if (intuneCollisions.Count == 0) return true;

        var onCollision = (_cfg["Rename:OnCollision"] ?? "block").Trim().ToLowerInvariant();
        var detail = string.Join(",",
            intuneCollisions.Select(c => $"{c.DisplayName}@{c.EntraDeviceId}"));

        _audit.TrackEvent(RenameAuditEvents.CollisionDetected, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
            [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            ["newName"]                       = newName,
            ["collisions"]                    = detail,
            ["collisionCount"]                = intuneCollisions.Count.ToString(),
            ["intuneCollisions"]              = intuneCollisions.Count.ToString(),
            ["policy"]                        = onCollision,
        }, LogLevel.Warning);

        if (onCollision == "block")
        {
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"name-collision:{intuneCollisions.Count}", ct);
            _audit.TrackEvent(RenameAuditEvents.CollisionBlocked, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["collisions"]                    = detail,
            }, LogLevel.Error);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:name-collision", ct, msg.IntuneDeviceId);
            return false;
        }
        return true; // warn → proceed
    }

    /// <summary>
    /// Legacy non-destructive collision behaviour used when
    /// <c>Rename:PreRenameCleanup=disabled</c>: probes BOTH Entra
    /// (<c>displayName</c>) and Intune (<c>deviceName</c>) and blocks/warns per
    /// <c>Rename:OnCollision</c> without deleting anything.
    /// </summary>
    private async Task<bool> LegacyCollisionCheckAsync(ActionRequestMessage msg, string newName, CancellationToken ct)
    {
        IReadOnlyList<DeviceCollision> entraCollisions;
        IReadOnlyList<DeviceCollision> intuneCollisions;
        try
        {
            entraCollisions  = await _graph.FindDisplayNameCollisionsAsync(newName, msg.EntraDeviceId, ct);
            intuneCollisions = await _graph.FindManagedDeviceNameCollisionsAsync(newName, msg.IntuneDeviceId, ct);
        }
        catch (Exception ex)
        {
            _audit.TrackEvent(RenameAuditEvents.CollisionCheckFailed, ex, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
            }, LogLevel.Warning);
            if (GraphRenameService.Classify(ex) == GraphErrorClassifier.GraphErrorKind.Transient) throw;
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"collision-check-failed:{ex.Message}", ct);
            await _statusTracker.RecordTerminalAsync(msg, Type, "failed:collision-check", ct, msg.IntuneDeviceId);
            return false;
        }

        var allCollisions = entraCollisions.Concat(intuneCollisions).ToList();
        if (allCollisions.Count == 0) return true;

        var onCollision = (_cfg["Rename:OnCollision"] ?? "block").Trim().ToLowerInvariant();
        var detail = string.Join(",",
            allCollisions.Select(c => $"{c.DisplayName}@{c.EntraDeviceId}{(c.AccountEnabled == false ? "(disabled)" : string.Empty)}"));

        _audit.TrackEvent(RenameAuditEvents.CollisionDetected, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
            [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
            ["newName"]                       = newName,
            ["collisions"]                    = detail,
            ["collisionCount"]                = allCollisions.Count.ToString(),
            ["entraCollisions"]               = entraCollisions.Count.ToString(),
            ["intuneCollisions"]              = intuneCollisions.Count.ToString(),
            ["policy"]                        = onCollision,
        }, LogLevel.Warning);

        if (onCollision == "block")
        {
            await _ledger.MarkFailedAsync(msg.IntuneDeviceId, msg.CorrelationId, $"name-collision:{allCollisions.Count}", ct);
            _audit.TrackEvent(RenameAuditEvents.CollisionBlocked, new Dictionary<string, string>
            {
                [AuditEvents.Prop.CorrelationId]  = msg.CorrelationId,
                [AuditEvents.Prop.IntuneDeviceId] = msg.IntuneDeviceId,
                ["newName"]                       = newName,
                ["collisions"]                    = detail,
            }, LogLevel.Error);
            await _statusTracker.RecordTerminalAsync(msg, Type, "denied:name-collision", ct, msg.IntuneDeviceId);
            return false;
        }
        return true; // warn → proceed
    }
}
