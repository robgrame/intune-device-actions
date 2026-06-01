using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Services;

/// <summary>
/// Conditional-write based idempotency ledger. One blob per Intune device id.
/// <para>
/// Original behaviour (pre-rearm): a single wipe per device was permitted; any
/// subsequent attempt returned <see cref="State.Issued"/> until the blob was
/// manually deleted. That was safe for destructive wipes (the device gets a
/// fresh <c>IntuneDeviceId</c> after re-enrollment) but blocked legitimate
/// re-wipes when <c>keepEnrollmentData=true</c> or after a failed wipe.
/// </para>
/// <para>
/// New behaviour (this revision): the ledger consults <see cref="WipeStatusTracker"/>
/// at <see cref="ReserveAsync"/> time. If the previous wipe for the same device
/// has reached an Intune-side terminal state (success / failure / pollTimeout
/// past a configurable grace period), the ledger atomically re-arms itself
/// (new correlationId, incremented sequence, prior outcome preserved for
/// audit) and lets the new wipe proceed. A per-device daily rate limit
/// prevents runaway loops. A dev-only <c>forceRearm</c> path skips the
/// tracker check entirely.
/// </para>
/// <para>
/// Blob lifecycle (one Entry per device, JSON):
/// <list type="bullet">
///   <item><c>State=Reserved</c> — a wipe is in flight for this correlationId.</item>
///   <item><c>State=Issued</c> — Graph wipe was successfully issued; tracker owns the outcome.</item>
///   <item><c>State=Failed</c> — permanent Graph failure on the previous attempt.</item>
/// </list>
/// All updates after the initial create use ETag-based optimistic concurrency
/// so concurrent rearms (two queue workers picking up duplicate messages) cannot
/// double-issue.
/// </para>
/// </summary>
public sealed class IdempotencyService
{
    public enum State { New, Reserved, Issued, Failed, RateLimited }

    public enum RearmReason
    {
        None,
        AfterSuccess,
        AfterFailure,
        AfterPollTimeout,
        Forced,
    }

    private readonly BlobContainerClient _container;
    private readonly WipeStatusTracker? _tracker;
    private readonly ILogger<IdempotencyService> _log;
    private readonly int _maxWipesPerDay;
    private readonly int _rearmGracePeriodHours;
    private readonly bool _allowForceRearm;
    private const int MaxRearmAttempts = 3;
    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(24);

    // Set of LastState values (from WipeStatusTracker) considered "wipe completed
    // successfully on the device side". Lowercased for comparison.
    private static readonly HashSet<string> SuccessStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "removedfromintune"
    };

    // Set of LastState values considered "wipe failed permanently" — a re-wipe
    // is legitimate without operator intervention.
    private static readonly HashSet<string> FailureStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "canceled", "notsupported"
    };

    public IdempotencyService(BlobContainerClient container, IConfiguration cfg,
        ILogger<IdempotencyService> log, WipeStatusTracker? tracker = null)
    {
        _container = container;
        _tracker = tracker;
        _log = log;
        _maxWipesPerDay        = int.TryParse(cfg["Idempotency:MaxWipesPerDevicePerDay"], out var m) ? Math.Max(1, m) : 5;
        _rearmGracePeriodHours = int.TryParse(cfg["Idempotency:RearmGracePeriodHours"], out var g) ? Math.Max(1, g) : 48;
        _allowForceRearm       = bool.TryParse(cfg["Idempotency:AllowForceRearm"], out var f) && f;
    }

    public int MaxWipesPerDay => _maxWipesPerDay;
    public int RearmGracePeriodHours => _rearmGracePeriodHours;
    public bool AllowForceRearm => _allowForceRearm;

    /// <summary>
    /// One row of the ledger. Backward-compatible with old blobs:
    /// missing fields deserialize to their CLR defaults.
    /// </summary>
    public sealed class Entry
    {
        public string IntuneDeviceId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string State { get; set; } = nameof(IdempotencyService.State.Reserved);
        public DateTimeOffset ReservedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? IssuedAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public string? FailureReason { get; set; }
        public int Attempts { get; set; } = 1;

        // --- Re-arm bookkeeping (added in the rearm refactor) ----------
        /// <summary>Increments on every successful rearm (1 for the first wipe).</summary>
        public int WipeSequence { get; set; } = 1;
        public DateTimeOffset? LastRearmedAt { get; set; }
        /// <summary>Terminal Intune-side state of the wipe that was superseded by the last rearm.</summary>
        public string? LastTerminalState { get; set; }
        public IdempotencyService.RearmReason LastRearmReason { get; set; } = IdempotencyService.RearmReason.None;
        /// <summary>UTC timestamps of every wipe successfully <em>issued</em> for this device (append on MarkIssued).
        /// Pruned to entries within the rate-limit window (24h) on read. Drives per-device daily rate limiting.</summary>
        public List<DateTimeOffset> RecentWipeTimestamps { get; set; } = new();
    }

    /// <summary>
    /// Outcome of a <see cref="ReserveAsync"/> call. The caller (typically
    /// <c>WipeActionRunner</c>) uses <see cref="State"/> to decide whether to
    /// proceed with the Graph wipe and uses <see cref="Rearmed"/> and
    /// <see cref="RecentWipesInWindow"/> to emit the appropriate audit events.
    /// </summary>
    public sealed class ReserveResult
    {
        public State State { get; init; }
        public Entry Entry { get; init; } = new();
        /// <summary>Non-None when this call re-armed an existing Issued/Failed ledger.</summary>
        public RearmReason Rearmed { get; init; } = RearmReason.None;
        public int RecentWipesInWindow { get; init; }
        /// <summary>Set when <see cref="State"/>=RateLimited; reflects the configured cap at decision time.</summary>
        public int MaxWipesPerDay { get; init; }
        /// <summary>Hours elapsed since the prior wipe reached terminal state (for AfterTimeout/AfterSuccess audits).</summary>
        public double? AgeSinceTerminalHours { get; init; }
    }

    /// <summary>
    /// Reserves the idempotency slot for a wipe attempt. See class summary for
    /// the decision matrix. Returns a <see cref="ReserveResult"/> describing
    /// whether the caller should proceed, was rejected as duplicate, was
    /// rate-limited, or triggered an automatic rearm.
    /// </summary>
    public async Task<ReserveResult> ReserveAsync(
        string intuneDeviceId, string correlationId, bool forceRearm, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(BlobName(intuneDeviceId));
        _log.LogDebug("Idempotency ReserveAsync entry: device={Device} corr={Corr} forceRearm={Force} blob={Blob}",
            intuneDeviceId, correlationId, forceRearm, blob.Name);

        // Attempt #1: optimistic create (no prior wipe for this device id).
        var fresh = new Entry
        {
            IntuneDeviceId = intuneDeviceId,
            CorrelationId  = correlationId,
            State          = nameof(State.Reserved),
            WipeSequence   = 1,
        };
        var freshBytes = JsonSerializer.SerializeToUtf8Bytes(fresh);

        try
        {
            await blob.UploadAsync(
                new BinaryData(freshBytes),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken: ct);
            _log.LogDebug("Idempotency NEW reservation created device={Device} corr={Corr}", intuneDeviceId, correlationId);
            return new ReserveResult { State = State.New, Entry = fresh, MaxWipesPerDay = _maxWipesPerDay };
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            // Blob exists — fall through to decision matrix.
        }

        // Loop to handle ETag conflicts on the rearm path (another worker
        // may rearm concurrently — we re-read and re-decide).
        for (var attempt = 0; attempt < MaxRearmAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (existing, etag) = await ReadAsync(blob, ct);

            // Same-correlation retry: idempotent re-call from the same runner.
            if (string.Equals(existing.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                return new ReserveResult
                {
                    State = ParseState(existing.State),
                    Entry = existing,
                    MaxWipesPerDay = _maxWipesPerDay,
                };
            }

            // Different correlation, blob in Reserved → another runner is actively
            // processing this device. Don't touch it.
            var existingState = ParseState(existing.State);
            if (existingState == State.Reserved)
            {
                return new ReserveResult
                {
                    State = State.Reserved,
                    Entry = existing,
                    MaxWipesPerDay = _maxWipesPerDay,
                };
            }

            // Existing.State == Issued or Failed. Decide if we can rearm.
            var (rearmDecision, ageHours) = await DecideRearmAsync(existing, forceRearm, ct);
            _log.LogDebug("Idempotency rearm decision: device={Device} existingState={State} ageHours={Age} decision={Decision} forceRearm={Force}",
                intuneDeviceId, existing.State, ageHours, rearmDecision, forceRearm);
            if (rearmDecision == RearmReason.None)
            {
                // No rearm permitted — preserve existing state and surface to caller.
                return new ReserveResult
                {
                    State = existingState,
                    Entry = existing,
                    MaxWipesPerDay = _maxWipesPerDay,
                    AgeSinceTerminalHours = ageHours,
                };
            }

            // Rate limiter — prune wipes outside the 24h window, count what's left.
            var now = DateTimeOffset.UtcNow;
            var recent = (existing.RecentWipeTimestamps ?? new List<DateTimeOffset>())
                .Where(t => now - t < RateWindow)
                .OrderBy(t => t)
                .ToList();

            if (recent.Count >= _maxWipesPerDay && rearmDecision != RearmReason.Forced)
            {
                _log.LogDebug("Idempotency RATE-LIMITED device={Device} recentCount={Recent} cap={Cap}",
                    intuneDeviceId, recent.Count, _maxWipesPerDay);
                return new ReserveResult
                {
                    State = State.RateLimited,
                    Entry = existing,
                    RecentWipesInWindow = recent.Count,
                    MaxWipesPerDay = _maxWipesPerDay,
                    AgeSinceTerminalHours = ageHours,
                };
            }

            // Atomic rearm: replace blob in-place with the new reservation.
            var rearmed = new Entry
            {
                IntuneDeviceId         = intuneDeviceId,
                CorrelationId          = correlationId,
                State                  = nameof(State.Reserved),
                ReservedAt             = now,
                Attempts               = 1,
                WipeSequence           = existing.WipeSequence + 1,
                LastRearmedAt          = now,
                LastTerminalState      = string.IsNullOrEmpty(existing.LastTerminalState)
                                          ? ToTerminalDescriptor(existing)
                                          : existing.LastTerminalState,
                LastRearmReason        = rearmDecision,
                RecentWipeTimestamps   = recent,
            };
            // On Forced rearm we still capture the original outcome for audit.
            if (rearmDecision == RearmReason.Forced && _tracker is not null)
            {
                try
                {
                    var snap = await _tracker.GetStatusAsync(existing.CorrelationId, ct);
                    if (snap is not null && snap.Terminal && !string.IsNullOrEmpty(snap.LastState))
                        rearmed.LastTerminalState = snap.LastState;
                }
                catch { /* best-effort enrichment */ }
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(rearmed);
            try
            {
                await blob.UploadAsync(
                    new BinaryData(bytes),
                    new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = etag } },
                    cancellationToken: ct);

                return new ReserveResult
                {
                    State = State.New,
                    Entry = rearmed,
                    Rearmed = rearmDecision,
                    RecentWipesInWindow = recent.Count,
                    MaxWipesPerDay = _maxWipesPerDay,
                    AgeSinceTerminalHours = ageHours,
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Concurrent rearm — re-read and re-decide on next iteration.
                _log.LogInformation("Ledger rearm ETag conflict for {Device} on attempt {Attempt}, retrying",
                    intuneDeviceId, attempt + 1);
                continue;
            }
        }

        // Exhausted retries — surface as a benign duplicate so the queue doesn't
        // poison; the audit log will show the contention.
        _log.LogWarning("Ledger rearm gave up after {Max} ETag conflicts for {Device}", MaxRearmAttempts, intuneDeviceId);
        var (final, _) = await ReadAsync(blob, ct);
        return new ReserveResult
        {
            State = ParseState(final.State),
            Entry = final,
            MaxWipesPerDay = _maxWipesPerDay,
        };
    }

    /// <summary>Marks the slot as <c>Issued</c> and appends a timestamp to the rate-limit window.</summary>
    public Task MarkIssuedAsync(string intuneDeviceId, string correlationId, CancellationToken ct)
        => UpdateAsync(intuneDeviceId, correlationId, (e, now) =>
        {
            e.State = nameof(State.Issued);
            e.IssuedAt = now;
            e.RecentWipeTimestamps ??= new List<DateTimeOffset>();
            e.RecentWipeTimestamps.Add(now);
            // Trim recent list to the rate window to keep blob size bounded.
            e.RecentWipeTimestamps = e.RecentWipeTimestamps
                .Where(t => now - t < RateWindow)
                .OrderBy(t => t)
                .ToList();
        }, ct);

    public Task MarkFailedAsync(string intuneDeviceId, string correlationId, string reason, CancellationToken ct)
        => UpdateAsync(intuneDeviceId, correlationId, (e, now) =>
        {
            e.State = nameof(State.Failed);
            e.FailedAt = now;
            e.FailureReason = reason;
        }, ct);

    /// <summary>
    /// Reads the current ledger entry for a device without modifying it.
    /// Returns null if no ledger exists. Used by the admin GET endpoint and
    /// for diagnostic tooling.
    /// </summary>
    public async Task<Entry?> GetEntryAsync(string intuneDeviceId, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(BlobName(intuneDeviceId));
        try
        {
            var (entry, _) = await ReadAsync(blob, ct);
            return entry;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Operator-driven manual reset: archives the current ledger blob under
    /// <c>_archive/{deviceId}/{timestamp}.json</c> (immutable copy for audit)
    /// and removes the live blob so the next wipe request starts fresh.
    /// Throws if no ledger exists.
    /// </summary>
    public async Task<(Entry archived, string archivePath)> ResetAsync(
        string intuneDeviceId, string actor, string reason, CancellationToken ct)
    {
        var live = _container.GetBlobClient(BlobName(intuneDeviceId));
        Entry current;
        ETag etag;
        try
        {
            (current, etag) = await ReadAsync(live, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"No ledger entry exists for device {intuneDeviceId}");
        }

        // Enrich the archived copy with reset metadata so the historical record
        // explains why it was reset and by whom.
        var enriched = new
        {
            entry             = current,
            archivedAt        = DateTimeOffset.UtcNow,
            resetByActor      = actor,
            resetReason       = reason,
        };
        var archiveName = $"_archive/{intuneDeviceId.ToLowerInvariant()}/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json";
        var archive = _container.GetBlobClient(archiveName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(enriched, new JsonSerializerOptions { WriteIndented = true });
        await archive.UploadAsync(new BinaryData(bytes), overwrite: false, cancellationToken: ct);

        // Delete the live blob (with ETag so a concurrent runner write loses).
        await live.DeleteAsync(DeleteSnapshotsOption.None,
            new BlobRequestConditions { IfMatch = etag }, ct);

        return (current, archiveName);
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Decides whether a previously-issued/failed ledger entry can be re-armed
    /// and why. Returns <see cref="RearmReason.None"/> when the rearm must be
    /// blocked (in-flight wipe, no tracker info, grace period not elapsed, ...).
    /// </summary>
    private async Task<(RearmReason reason, double? ageHours)> DecideRearmAsync(
        Entry existing, bool forceRearm, CancellationToken ct)
    {
        // Forced rearm short-circuit (gated by config) — for DEV/testing.
        if (forceRearm && _allowForceRearm)
        {
            return (RearmReason.Forced, null);
        }

        // Without the tracker we cannot prove the previous wipe finished —
        // stay conservative and block.
        if (_tracker is null || !_tracker.IsEnabled)
        {
            return (RearmReason.None, null);
        }

        WipeStatusSnapshot? snap;
        try
        {
            snap = await _tracker.GetStatusAsync(existing.CorrelationId, ct);
        }
        catch
        {
            // Transient failure reading the tracker → conservative block; the
            // next ReserveAsync attempt will retry.
            return (RearmReason.None, null);
        }

        if (snap is null || !snap.Terminal)
        {
            return (RearmReason.None, null);
        }

        var ageHours = (DateTimeOffset.UtcNow - snap.LastChangedAt).TotalHours;

        if (SuccessStates.Contains(snap.LastState))
            return (RearmReason.AfterSuccess, ageHours);
        if (FailureStates.Contains(snap.LastState))
            return (RearmReason.AfterFailure, ageHours);
        if (string.Equals(snap.LastState, "polltimeout", StringComparison.OrdinalIgnoreCase))
        {
            if (ageHours >= _rearmGracePeriodHours)
                return (RearmReason.AfterPollTimeout, ageHours);
            // Still within grace — keep blocking.
            return (RearmReason.None, ageHours);
        }

        // Unknown terminal state — conservative block.
        return (RearmReason.None, ageHours);
    }

    private static string ToTerminalDescriptor(Entry e)
        => e.State switch
        {
            nameof(State.Issued) => "issued-no-tracker-feedback",
            nameof(State.Failed) => $"failed:{e.FailureReason ?? "unknown"}",
            _                    => e.State,
        };

    private async Task UpdateAsync(string intuneDeviceId, string correlationId,
        Action<Entry, DateTimeOffset> mutate, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(BlobName(intuneDeviceId));
        Entry current;
        try
        {
            (current, _) = await ReadAsync(blob, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            current = new Entry { IntuneDeviceId = intuneDeviceId, CorrelationId = correlationId };
        }
        current.Attempts++;
        mutate(current, DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(current);
        await blob.UploadAsync(new BinaryData(bytes), overwrite: true, cancellationToken: ct);
    }

    private static async Task<(Entry entry, ETag etag)> ReadAsync(BlobClient blob, CancellationToken ct)
    {
        var resp = await blob.DownloadContentAsync(ct);
        var entry = JsonSerializer.Deserialize<Entry>(resp.Value.Content.ToStream()) ?? new Entry();
        return (entry, resp.GetRawResponse().Headers.ETag ?? ETag.All);
    }

    private static string BlobName(string intuneDeviceId)
        => $"{intuneDeviceId.ToLowerInvariant()}.json";

    private static State ParseState(string s)
        => Enum.TryParse<State>(s, true, out var v) ? v : State.New;
}
