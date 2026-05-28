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

    public GraphWipeService(GraphServiceClient graph, IConfiguration cfg, ILogger<GraphWipeService> log)
    {
        _graph = graph;
        _log = log;
        _keepEnrollment = bool.TryParse(cfg["Wipe:KeepEnrollmentData"], out var ke) && ke;
        _keepUserData = bool.TryParse(cfg["Wipe:KeepUserData"], out var ku) && ku;
        _allowedGroupId = cfg["Wipe:AllowedGroupId"]
            ?? throw new InvalidOperationException("Wipe:AllowedGroupId must be configured");
    }

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
