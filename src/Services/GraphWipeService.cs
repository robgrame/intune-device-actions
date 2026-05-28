using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.DeviceManagement.ManagedDevices.Item.Wipe;
using Microsoft.Graph.Devices.Item.CheckMemberGroups;
using Microsoft.Graph.Models.ODataErrors;

namespace IntuneWipeApi.Services;

public sealed class GraphWipeService
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<GraphWipeService> _log;
    private readonly bool _keepEnrollment;
    private readonly bool _keepUserData;
    private readonly string _allowedGroupId;
    private readonly int _syncFallbackDelaySeconds;
    private readonly int _rebootFallbackDelaySeconds;

    public GraphWipeService(GraphServiceClient graph, IConfiguration cfg, ILogger<GraphWipeService> log)
    {
        _graph = graph;
        _log = log;
        _keepEnrollment = bool.TryParse(cfg["Wipe:KeepEnrollmentData"], out var ke) && ke;
        _keepUserData = bool.TryParse(cfg["Wipe:KeepUserData"], out var ku) && ku;
        _allowedGroupId = cfg["Wipe:AllowedGroupId"]
            ?? throw new InvalidOperationException("Wipe:AllowedGroupId must be configured");

        // Post-wipe fallback nudges. Default: syncDevice 60s after wipe, then
        // rebootNow 60s after the sync. Set either to 0 to disable that step.
        _syncFallbackDelaySeconds   = int.TryParse(cfg["Wipe:SyncFallbackDelaySeconds"],   out var s) ? Math.Max(0, s) : 60;
        _rebootFallbackDelaySeconds = int.TryParse(cfg["Wipe:RebootFallbackDelaySeconds"], out var r) ? Math.Max(0, r) : 60;
    }

    public int SyncFallbackDelaySeconds   => _syncFallbackDelaySeconds;
    public int RebootFallbackDelaySeconds => _rebootFallbackDelaySeconds;

    /// <summary>
    /// Resolves the directory object id of an Entra device by its deviceId (azureADDeviceId).
    /// </summary>
    public async Task<string?> GetDeviceObjectIdAsync(string entraDeviceId, CancellationToken ct)
    {
        if (!Guid.TryParse(entraDeviceId, out _))
            throw new ArgumentException("entraDeviceId must be a GUID", nameof(entraDeviceId));

        var page = await _graph.Devices.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"deviceId eq '{entraDeviceId}'";
            rc.QueryParameters.Select = new[] { "id", "deviceId", "displayName" };
            rc.QueryParameters.Top = 1;
        }, ct);
        return page?.Value?.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Checks if the device (by directory object id) is a member (direct or transitive) of the allowed group.
    /// </summary>
    public async Task<bool> IsDeviceInAllowedGroupAsync(string deviceObjectId, CancellationToken ct)
    {
        var body = new CheckMemberGroupsPostRequestBody { GroupIds = new List<string> { _allowedGroupId } };
        var result = await _graph.Devices[deviceObjectId]
            .CheckMemberGroups
            .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct);
        var matches = result?.Value ?? new List<string>();
        return matches.Contains(_allowedGroupId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the Intune managedDevice.id by querying managedDevices filtered by azureADDeviceId.
    /// The cert-bound entraDeviceId is the authoritative server-side input (the client-supplied
    /// IntuneDeviceId from registry — DeviceClientId/EnrollmentId — is NOT the same as managedDevice.id
    /// and must NOT be trusted for resolution).
    ///
    /// Returns the managedDevice.id on success, null when:
    ///   - no managedDevice exists for that azureADDeviceId (device not enrolled in Intune or sync gap)
    ///   - more than one matches (ambiguity, fail-closed)
    /// </summary>
    public async Task<string?> ResolveAndValidateAsync(string entraDeviceId, CancellationToken ct)
    {
        if (!Guid.TryParse(entraDeviceId, out _))
            throw new ArgumentException("entraDeviceId must be a GUID", nameof(entraDeviceId));

        var page = await _graph.DeviceManagement.ManagedDevices.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"azureADDeviceId eq '{entraDeviceId}'";
            rc.QueryParameters.Select = new[] { "id", "deviceName", "azureADDeviceId", "managementState" };
            rc.QueryParameters.Top    = 2;
        }, ct);

        var matches = page?.Value ?? new List<Microsoft.Graph.Models.ManagedDevice>();

        if (matches.Count == 0)
        {
            _log.LogWarning("No managedDevice found for azureADDeviceId={Aad} (device not Intune-enrolled or replication lag)", entraDeviceId);
            return null;
        }
        if (matches.Count > 1)
        {
            _log.LogWarning("Ambiguous managedDevice resolution for azureADDeviceId={Aad}: {Count} matches — fail-closed", entraDeviceId, matches.Count);
            return null;
        }
        return matches[0].Id;
    }

    public async Task WipeAsync(string managedDeviceId, CancellationToken ct)
    {
        var body = new WipePostRequestBody
        {
            KeepEnrollmentData = _keepEnrollment,
            KeepUserData = _keepUserData
        };
        await _graph.DeviceManagement.ManagedDevices[managedDeviceId].Wipe.PostAsync(body, cancellationToken: ct);
        _log.LogInformation("Wipe issued for managedDevice {Id}", managedDeviceId);
    }

    /// <summary>
    /// Forces an Intune MDM check-in on the device so it pulls pending actions
    /// (including a freshly-issued wipe). Best-effort: caller should treat any
    /// exception as non-fatal.
    /// </summary>
    public async Task SyncDeviceAsync(string managedDeviceId, CancellationToken ct)
    {
        await _graph.DeviceManagement.ManagedDevices[managedDeviceId].SyncDevice.PostAsync(cancellationToken: ct);
        _log.LogInformation("syncDevice issued for managedDevice {Id}", managedDeviceId);
    }

    /// <summary>
    /// Sends a remote rebootNow. Combined with a prior syncDevice, this is the
    /// last-ditch nudge to make a stuck IME session apply the queued wipe.
    /// Best-effort: caller should treat any exception as non-fatal.
    /// </summary>
    public async Task RebootAsync(string managedDeviceId, CancellationToken ct)
    {
        await _graph.DeviceManagement.ManagedDevices[managedDeviceId].RebootNow.PostAsync(cancellationToken: ct);
        _log.LogInformation("rebootNow issued for managedDevice {Id}", managedDeviceId);
    }

    /// <summary>
    /// Queries the current state of the wipe device action on a managed device.
    /// Returns:
    ///   - the (actionState, lastUpdatedDateTime) of the first <c>actionName == "wipe"</c>
    ///     entry in <c>deviceActionResults</c>;
    ///   - <c>("removedFromIntune", now)</c> if Graph returns 404 — strong signal that
    ///     the wipe completed and the device de-enrolled or was removed;
    ///   - <c>("notReported", null)</c> if the device exists but has no wipe action
    ///     recorded yet (race condition between issue and first IME check-in).
    /// Other Graph errors are thrown and should be retried by the caller.
    /// </summary>
    public async Task<(string State, DateTimeOffset? LastUpdated)> GetWipeActionStatusAsync(
        string managedDeviceId, CancellationToken ct)
    {
        try
        {
            var dev = await _graph.DeviceManagement.ManagedDevices[managedDeviceId].GetAsync(rc =>
            {
                // Select only what we need to keep the response small. Note: Graph
                // requires 'id' to be in $select for the entity to deserialize.
                rc.QueryParameters.Select = new[] { "id", "deviceName", "deviceActionResults" };
            }, cancellationToken: ct);

            if (dev?.DeviceActionResults is null || dev.DeviceActionResults.Count == 0)
            {
                return ("notReported", null);
            }

            // Take the most-recently-updated wipe action. There may be multiple
            // entries across the device's history (e.g. wipe → autopilot reset →
            // wipe) so always prefer the freshest one.
            var wipe = dev.DeviceActionResults
                .Where(r => string.Equals(r.ActionName, "wipe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.LastUpdatedDateTime ?? r.StartDateTime ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (wipe is null)
            {
                return ("notReported", null);
            }

            return (wipe.ActionState?.ToString().ToLowerInvariant() ?? "unknown", wipe.LastUpdatedDateTime ?? wipe.StartDateTime);
        }
        catch (ODataError oe) when (oe.ResponseStatusCode == 404)
        {
            // Device gone from Intune — best inferable signal that the wipe ran
            // to completion (it factory-reset and either re-enrolled with a new
            // managed-device id or stayed out of MDM).
            return ("removedFromIntune", DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Classifies a Microsoft Graph exception as Transient (retry) or Permanent (do not retry).
    /// </summary>
    public static GraphErrorKind Classify(Exception ex)
    {
        if (ex is OperationCanceledException) return GraphErrorKind.Transient;

        if (ex is ODataError oe)
        {
            var status = oe.ResponseStatusCode;
            if (status == 408 || status == 429 || status >= 500) return GraphErrorKind.Transient;
            if (status >= 400 && status < 500) return GraphErrorKind.Permanent;
        }
        // Network / DNS / TLS — let it retry.
        if (ex is HttpRequestException or TimeoutException) return GraphErrorKind.Transient;
        return GraphErrorKind.Transient;
    }

    public enum GraphErrorKind { Transient, Permanent }
}
