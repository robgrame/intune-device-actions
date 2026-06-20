using Azure;
using Azure.Data.Tables;
using IntuneDeviceActions.Schedule;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Devices.Item.CheckMemberGroups;

namespace IntuneDeviceActions.Capabilities.Wipe.Schedule;

/// <summary>
/// CRUD facade over two Azure Tables that together model "wipe schedule
/// waves":
/// <list type="bullet">
///   <item><description><b>waves table</b> — one row per wave (constant PK,
///   RowKey = wave id).</description></item>
///   <item><description><b>members table</b> — one row per device-in-wave
///   assignment (PK = wave id, RowKey = entra device id).</description></item>
/// </list>
/// <para>
/// Tables are auto-created on first use (lazy, single-flight) so a fresh
/// deployment needs no extra provisioning step.
/// </para>
/// <para>
/// This store lives entirely inside the wipe capability project and is the
/// single source of truth for wipe scheduling. The portal writes to it
/// directly via <c>TableClient</c> (cross-process contract documented on
/// table/column names). The wipe runner reads from it to enforce
/// capability-level temporal gating. The core (Web) never touches it — it
/// only sees <see cref="DeviceScheduleSnapshot"/> via the generic
/// <see cref="IScheduleProvider"/> contract.
/// </para>
/// </summary>
public sealed class WipeScheduleStore
{
    private readonly TableClient _waves;
    private readonly TableClient _members;
    private readonly ILogger<WipeScheduleStore> _log;

    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _tablesEnsured;

    public WipeScheduleStore(TableClient wavesTable, TableClient membersTable,
        ILogger<WipeScheduleStore> log)
    {
        _waves = wavesTable;
        _members = membersTable;
        _log = log;
    }

    // ----- waves -----------------------------------------------------------

    /// <summary>
    /// Inserts a new wave or replaces an existing one by id. <paramref name="wave"/>
    /// MUST have a non-empty <see cref="WipeScheduleWave.RowKey"/>.
    /// </summary>
    public async Task<WipeScheduleWave> UpsertWaveAsync(
        WipeScheduleWave wave, CancellationToken ct = default)
    {
        if (wave is null) throw new ArgumentNullException(nameof(wave));
        if (string.IsNullOrWhiteSpace(wave.RowKey))
            throw new ArgumentException("RowKey (wave id) must be set.", nameof(wave));
        wave.PartitionKey = WipeScheduleWave.DefaultPartition;
        wave.RowKey = wave.RowKey.ToLowerInvariant();
        if (wave.CreatedAtUtc == default) wave.CreatedAtUtc = DateTimeOffset.UtcNow;
        wave.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await EnsureTablesAsync(ct).ConfigureAwait(false);
        await _waves.UpsertEntityAsync(wave, TableUpdateMode.Replace, ct)
            .ConfigureAwait(false);
        return wave;
    }

    /// <summary>Returns the wave or <c>null</c> if missing.</summary>
    public async Task<WipeScheduleWave?> GetWaveAsync(Guid waveId, CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await _waves.GetEntityAsync<WipeScheduleWave>(
                WipeScheduleWave.DefaultPartition,
                waveId.ToString("D").ToLowerInvariant(), cancellationToken: ct)
                .ConfigureAwait(false);
            return resp.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Enumerates all waves ordered by ScheduledAtUtc ascending.</summary>
    public async Task<IReadOnlyList<WipeScheduleWave>> ListWavesAsync(CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var list = new List<WipeScheduleWave>();
        await foreach (var w in _waves.QueryAsync<WipeScheduleWave>(
            $"PartitionKey eq '{WipeScheduleWave.DefaultPartition}'",
            cancellationToken: ct))
        {
            list.Add(w);
        }
        list.Sort((a, b) => a.ScheduledAtUtc.CompareTo(b.ScheduledAtUtc));
        return list;
    }

    /// <summary>
    /// Deletes the wave row AND all its member rows. Best-effort: a partial
    /// failure leaves orphan members in the members table; they are
    /// harmlessly skipped by <see cref="GetScheduleForDeviceAsync"/> (which
    /// re-resolves the wave row and silently drops members pointing at a
    /// missing wave). <see cref="ListMembersAsync"/> does NOT filter
    /// orphans — but it's only called by the portal with a wave id that
    /// must exist, so orphans cannot reach the UI.
    /// </summary>
    public async Task DeleteWaveAsync(Guid waveId, CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var pk = waveId.ToString("D").ToLowerInvariant();

        await foreach (var m in _members.QueryAsync<WipeScheduleWaveMember>(
            $"PartitionKey eq '{pk}'", cancellationToken: ct))
        {
            try
            {
                await _members.DeleteEntityAsync(m.PartitionKey, m.RowKey,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* ok */ }
        }

        try
        {
            await _waves.DeleteEntityAsync(WipeScheduleWave.DefaultPartition, pk,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* ok */ }
    }

    // ----- members ---------------------------------------------------------

    /// <summary>
    /// Adds a device to a wave (or refreshes its metadata if already present).
    /// </summary>
    public async Task<WipeScheduleWaveMember> AddMemberAsync(
        Guid waveId, Guid entraDeviceId, string deviceName,
        string? intuneDeviceId = null, string? addedBy = null,
        CancellationToken ct = default)
    {
        if (waveId == Guid.Empty) throw new ArgumentException("Empty wave id.", nameof(waveId));
        if (entraDeviceId == Guid.Empty) throw new ArgumentException("Empty entra device id.", nameof(entraDeviceId));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name required.", nameof(deviceName));

        var member = new WipeScheduleWaveMember
        {
            PartitionKey = waveId.ToString("D").ToLowerInvariant(),
            RowKey = entraDeviceId.ToString("D").ToLowerInvariant(),
            DeviceName = deviceName,
            IntuneDeviceId = string.IsNullOrWhiteSpace(intuneDeviceId) ? null : intuneDeviceId,
            AddedBy = addedBy,
            AddedAtUtc = DateTimeOffset.UtcNow,
        };

        await EnsureTablesAsync(ct).ConfigureAwait(false);
        await _members.UpsertEntityAsync(member, TableUpdateMode.Replace, ct)
            .ConfigureAwait(false);
        return member;
    }

    /// <summary>Removes a device from a wave. 404 is success (idempotent).</summary>
    public async Task RemoveMemberAsync(Guid waveId, Guid entraDeviceId, CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        try
        {
            await _members.DeleteEntityAsync(
                waveId.ToString("D").ToLowerInvariant(),
                entraDeviceId.ToString("D").ToLowerInvariant(),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* ok */ }
    }

    /// <summary>Lists all members of a wave (single partition scan).</summary>
    public async Task<IReadOnlyList<WipeScheduleWaveMember>> ListMembersAsync(
        Guid waveId, CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var list = new List<WipeScheduleWaveMember>();
        var pk = waveId.ToString("D").ToLowerInvariant();
        await foreach (var m in _members.QueryAsync<WipeScheduleWaveMember>(
            $"PartitionKey eq '{pk}'", cancellationToken: ct))
        {
            list.Add(m);
        }
        return list;
    }

    // ----- device → schedule lookup ---------------------------------------

    /// <summary>
    /// Returns the next imminent schedule entry for <paramref name="entraDeviceId"/>,
    /// considering ONLY individual (members-table) membership. Group-based waves
    /// are resolved by the <paramref name="graph"/>-aware overload. Kept for the
    /// advisory read path; prefer the overload that also resolves group waves.
    /// </summary>
    public Task<DeviceScheduleSnapshot?> GetScheduleForDeviceAsync(
        Guid entraDeviceId, CancellationToken ct = default)
        => GetScheduleForDeviceAsync(entraDeviceId, graph: null, ct);

    /// <summary>
    /// Returns the next imminent schedule entry for <paramref name="entraDeviceId"/>,
    /// or null if the device is not enrolled in any client-visible wave.
    /// <para>
    /// Membership is the UNION of two sufficient conditions:
    /// <list type="bullet">
    ///   <item><b>Individual</b> — a row in the members table (cross-partition
    ///   scan by RowKey).</item>
    ///   <item><b>Group-based</b> — the device belongs to a wave's
    ///   <see cref="WipeScheduleWave.EntraGroupId"/>, resolved in real time via
    ///   Graph <c>checkMemberGroups</c> when <paramref name="graph"/> is supplied.
    ///   Group membership is <i>sufficient</i>: a device in the wave's group is
    ///   enrolled even without an individual row.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Graph failures are intentionally NOT swallowed here so the enforcement
    /// caller (<c>WipeScheduleGate</c>) can apply its fail-closed/open policy.
    /// The advisory provider wraps this call and degrades to null on error.
    /// </para>
    /// Acceptable for &lt;1000 waves; for larger volumes introduce a reverse-index
    /// table.
    /// </summary>
    public async Task<DeviceScheduleSnapshot?> GetScheduleForDeviceAsync(
        Guid entraDeviceId, GraphServiceClient? graph, CancellationToken ct = default)
    {
        if (entraDeviceId == Guid.Empty) return null;
        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var individual = await CollectIndividualWavesAsync(entraDeviceId, ct).ConfigureAwait(false);
        var group = graph is null
            ? new List<WipeScheduleWave>()
            : await CollectGroupWavesAsync(entraDeviceId, graph, ct).ConfigureAwait(false);

        return PickBestCandidate(MergeWaveCandidates(individual, group));
    }

    /// <summary>Collects client-visible waves the device joins via an individual member row.</summary>
    private async Task<List<WipeScheduleWave>> CollectIndividualWavesAsync(
        Guid entraDeviceId, CancellationToken ct)
    {
        var rk = entraDeviceId.ToString("D").ToLowerInvariant();
        var memberships = new List<WipeScheduleWaveMember>();
        await foreach (var m in _members.QueryAsync<WipeScheduleWaveMember>(
            $"RowKey eq '{rk}'", cancellationToken: ct))
        {
            memberships.Add(m);
        }

        var candidates = new List<WipeScheduleWave>();
        foreach (var m in memberships)
        {
            if (!Guid.TryParse(m.PartitionKey, out var waveId)) continue;
            var wave = await GetWaveAsync(waveId, ct).ConfigureAwait(false);
            if (wave is null) continue;
            if (!WaveStatus.ClientVisible.Contains(wave.Status)) continue;
            candidates.Add(wave);
        }
        return candidates;
    }

    /// <summary>
    /// Collects client-visible group-based waves whose Entra group contains the
    /// device, resolved via Graph <c>checkMemberGroups</c>. Graph exceptions
    /// propagate to the caller (see <see cref="GetScheduleForDeviceAsync(Guid, GraphServiceClient?, CancellationToken)"/>).
    /// </summary>
    private async Task<List<WipeScheduleWave>> CollectGroupWavesAsync(
        Guid entraDeviceId, GraphServiceClient graph, CancellationToken ct)
    {
        var groupWaves = await GetGroupBasedWavesAsync(ct).ConfigureAwait(false);
        if (groupWaves.Count == 0) return new List<WipeScheduleWave>();

        // Resolve entraDeviceId → directory object id (needed for checkMemberGroups).
        var page = await graph.Devices.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filter = $"deviceId eq '{entraDeviceId}'";
            cfg.QueryParameters.Select = new[] { "id" };
            cfg.QueryParameters.Top = 1;
        }, ct).ConfigureAwait(false);
        var objectId = page?.Value?.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(objectId)) return new List<WipeScheduleWave>();

        var groupIds = groupWaves
            .Select(w => w.EntraGroupId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var body = new CheckMemberGroupsPostRequestBody { GroupIds = groupIds };
        var result = await graph.Devices[objectId]
            .CheckMemberGroups
            .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct)
            .ConfigureAwait(false);
        var matchedGroups = result?.Value ?? new List<string>();
        if (matchedGroups.Count == 0) return new List<WipeScheduleWave>();

        return groupWaves
            .Where(w => matchedGroups.Contains(w.EntraGroupId!, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Unions individual and group wave candidates, de-duplicated by wave id (a
    /// device may match the same wave both via an individual row and its Entra
    /// group).
    /// </summary>
    internal static List<WipeScheduleWave> MergeWaveCandidates(
        IReadOnlyList<WipeScheduleWave> individual, IReadOnlyList<WipeScheduleWave> group)
    {
        var byId = new Dictionary<string, WipeScheduleWave>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in individual) byId[w.RowKey] = w;
        foreach (var w in group) byId[w.RowKey] = w;
        return byId.Values.ToList();
    }

    /// <summary>
    /// Returns all client-visible waves that use Entra group membership
    /// (EntraGroupId is not null/empty). Used by the provider to check if
    /// the device belongs to any of these groups via Graph.
    /// </summary>
    public async Task<IReadOnlyList<WipeScheduleWave>> GetGroupBasedWavesAsync(CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        var list = new List<WipeScheduleWave>();
        await foreach (var w in _waves.QueryAsync<WipeScheduleWave>(
            $"PartitionKey eq '{WipeScheduleWave.DefaultPartition}'",
            cancellationToken: ct))
        {
            if (string.IsNullOrWhiteSpace(w.EntraGroupId)) continue;
            if (!WaveStatus.ClientVisible.Contains(w.Status)) continue;
            list.Add(w);
        }
        return list;
    }

    /// <summary>
    /// Given a list of candidate waves, pick the best one (next future, or most recent past).
    /// Returns null if candidates is empty.
    /// </summary>
    internal static DeviceScheduleSnapshot? PickBestCandidate(IReadOnlyList<WipeScheduleWave> candidates)
    {
        if (candidates.Count == 0) return null;

        var now = DateTimeOffset.UtcNow;
        var future = candidates.Where(c => c.ScheduledAtUtc > now).ToList();
        WipeScheduleWave next;
        if (future.Count > 0)
        {
            future.Sort((a, b) => a.ScheduledAtUtc.CompareTo(b.ScheduledAtUtc));
            next = future[0];
        }
        else
        {
            var past = candidates.ToList();
            past.Sort((a, b) => b.ScheduledAtUtc.CompareTo(a.ScheduledAtUtc));
            next = past[0];
        }

        return new DeviceScheduleSnapshot
        {
            WaveId = next.RowKey,
            Name = next.Name,
            ActionType = WipeScheduleWave.ActionTypeValue,
            ScheduledAtUtc = next.ScheduledAtUtc,
            Status = next.Status,
            IsImmediate = next.ScheduledAtUtc <= now,
            Description = next.Description,
            GeneratedAtUtc = now,
        };
    }

    /// <summary>
    /// Tells the wipe runner whether a wipe for <paramref name="entraDeviceId"/>
    /// should be DEFERRED because the device is enrolled in a future wave.
    /// Returns <c>(false, null)</c> when no wave exists OR the wave has
    /// already fired. Returns <c>(true, scheduledAtUtc)</c> when the wipe
    /// must be deferred. Defense-in-depth companion to client-side gating.
    /// </summary>
    public Task<(bool Defer, DateTimeOffset? ScheduledAtUtc)> ShouldDeferWipeAsync(
        Guid entraDeviceId, CancellationToken ct = default)
        => ShouldDeferWipeAsync(entraDeviceId, graph: null, ct);

    /// <summary>
    /// Group-aware overload of <see cref="ShouldDeferWipeAsync(Guid, CancellationToken)"/>:
    /// when <paramref name="graph"/> is supplied, wave enrollment also considers
    /// the device's membership in a wave's Entra group (sufficient condition).
    /// Graph failures propagate so the gate can apply its error policy.
    /// </summary>
    public async Task<(bool Defer, DateTimeOffset? ScheduledAtUtc)> ShouldDeferWipeAsync(
        Guid entraDeviceId, GraphServiceClient? graph, CancellationToken ct = default)
    {
        var snap = await GetScheduleForDeviceAsync(entraDeviceId, graph, ct).ConfigureAwait(false);
        if (snap is null) return (false, null);
        if (snap.IsImmediate) return (false, snap.ScheduledAtUtc);
        return (true, snap.ScheduledAtUtc);
    }

    // ----- bootstrap -------------------------------------------------------

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        if (_tablesEnsured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tablesEnsured) return;
            await _waves.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
            await _members.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
            _tablesEnsured = true;
            _log.LogDebug("WipeScheduleStore tables ensured ({Waves}, {Members}).",
                _waves.Name, _members.Name);
        }
        finally
        {
            _ensureGate.Release();
        }
    }
}
