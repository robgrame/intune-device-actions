using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.DeviceManagement.ManagedDevices.Item.Wipe;
using Microsoft.Graph.Devices.Item.CheckMemberGroups;

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
        var result = await _graph.Devices[deviceObjectId].CheckMemberGroups.PostAsync(body, cancellationToken: ct);
        var matches = result?.Value ?? new List<string>();
        return matches.Contains(_allowedGroupId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that intuneDeviceId belongs to entraDeviceId (azureADDeviceId match) and returns the managed device id.
    /// </summary>
    public async Task<string?> ResolveAndValidateAsync(string intuneDeviceId, string entraDeviceId, CancellationToken ct)
    {
        var md = await _graph.DeviceManagement.ManagedDevices[intuneDeviceId]
            .GetAsync(rc => rc.QueryParameters.Select = new[] { "id", "deviceName", "azureADDeviceId" }, ct);

        if (md is null) return null;

        if (!string.Equals(md.AzureADDeviceId, entraDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Ownership mismatch: managedDevice {Id} azureADDeviceId={Aad}, request={Req}",
                intuneDeviceId, md.AzureADDeviceId, entraDeviceId);
            return null;
        }
        return md.Id;
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
}
