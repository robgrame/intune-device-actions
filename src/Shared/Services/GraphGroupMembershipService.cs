using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Devices.Item.CheckMemberGroups;
using CheckMemberGroupsPostRequestBody = Microsoft.Graph.Devices.Item.CheckMemberGroups.CheckMemberGroupsPostRequestBody;
using UserCheckMemberGroupsPostRequestBody = Microsoft.Graph.Users.Item.CheckMemberGroups.CheckMemberGroupsPostRequestBody;
using Microsoft.Graph.Models.ODataErrors;

namespace IntuneDeviceActions.Services;

/// <summary>
/// Transversally available Graph service for generic group membership checks (device + user).
/// Used by <see cref="Gates.DeviceGroupMembershipGate"/> and <see cref="Gates.UserGroupMembershipGate"/>.
/// </summary>
public sealed class GraphGroupMembershipService : IGraphGroupMembershipService
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<GraphGroupMembershipService> _log;

    public GraphGroupMembershipService(GraphServiceClient graph, ILogger<GraphGroupMembershipService> log)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Checks if a device (by directory object id) is a direct or transitive member of a specific group.
    /// </summary>
    /// <param name="deviceObjectId">Directory object id of the device (NOT the Entra device id)</param>
    /// <param name="groupId">Directory id of the group</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>true if device is a member; false if not a member or if the group does not exist</returns>
    /// <exception cref="HttpRequestException">Permanent Graph error (4xx other than 404)</exception>
    /// <exception cref="OperationCanceledException">Cancellation or timeout</exception>
    public async Task<bool> IsDeviceInGroupAsync(string deviceObjectId, string groupId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceObjectId))
            throw new ArgumentException("deviceObjectId cannot be null or whitespace", nameof(deviceObjectId));
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("groupId cannot be null or whitespace", nameof(groupId));

        try
        {
            _log.LogDebug("Checking if device {Device} is in group {Group}", deviceObjectId, groupId);
            var body = new CheckMemberGroupsPostRequestBody { GroupIds = new List<string> { groupId } };
            var result = await _graph.Devices[deviceObjectId]
                .CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct);
            
            var matches = result?.Value ?? new List<string>();
            var isMember = matches.Contains(groupId, StringComparer.OrdinalIgnoreCase);
            _log.LogDebug("Device {Device} group {Group} check: {Result}", deviceObjectId, groupId, isMember ? "member" : "not a member");
            return isMember;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Device or group does not exist; treat as "not a member"
            _log.LogWarning("Device or group not found during membership check: {Error}", ex.Message);
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode >= System.Net.HttpStatusCode.BadRequest
                                              && ex.StatusCode < System.Net.HttpStatusCode.InternalServerError
                                              && ex.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            // Permanent 4xx error (other than 404 which we handle above)
            _log.LogError(ex, "Permanent Graph error checking device {Device} in group {Group}", deviceObjectId, groupId);
            throw;
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode >= 500)
        {
            // Transient 5xx error
            _log.LogWarning(ex, "Transient Graph error checking device {Device} in group {Group}", deviceObjectId, groupId);
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // 429 = transient throttling
            _log.LogWarning(ex, "Graph throttling (429) checking device {Device} in group {Group}", deviceObjectId, groupId);
            throw;
        }
    }

    /// <summary>
    /// Checks if a user (by UPN) is a direct or transitive member of a specific group.
    /// </summary>
    /// <param name="userUpn">User Principal Name (e.g. 'user@contoso.onmicrosoft.com')</param>
    /// <param name="groupId">Directory id of the group</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>true if user is a member; false if not a member or if the group does not exist</returns>
    /// <exception cref="ArgumentException">userUpn is null or whitespace</exception>
    /// <exception cref="HttpRequestException">Permanent Graph error (4xx other than 404)</exception>
    /// <exception cref="OperationCanceledException">Cancellation or timeout</exception>
    public async Task<bool> IsUserInGroupAsync(string? userUpn, string groupId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userUpn))
            throw new ArgumentException("userUpn cannot be null or whitespace", nameof(userUpn));
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("groupId cannot be null or whitespace", nameof(groupId));

        try
        {
            _log.LogDebug("Checking if user {User} is in group {Group}", userUpn, groupId);
            var body = new UserCheckMemberGroupsPostRequestBody
            {
                GroupIds = new List<string> { groupId }
            };
            var result = await _graph.Users[userUpn]
                .CheckMemberGroups
                .PostAsCheckMemberGroupsPostResponseAsync(body, cancellationToken: ct);
            
            var matches = result?.Value ?? new List<string>();
            var isMember = matches.Contains(groupId, StringComparer.OrdinalIgnoreCase);
            _log.LogDebug("User {User} group {Group} check: {Result}", userUpn, groupId, isMember ? "member" : "not a member");
            return isMember;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // User or group does not exist; treat as "not a member"
            _log.LogWarning("User or group not found during membership check: {Error}", ex.Message);
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode >= System.Net.HttpStatusCode.BadRequest
                                              && ex.StatusCode < System.Net.HttpStatusCode.InternalServerError
                                              && ex.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            // Permanent 4xx error (other than 404)
            _log.LogError(ex, "Permanent Graph error checking user {User} in group {Group}", userUpn, groupId);
            throw;
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode >= 500)
        {
            // Transient 5xx error
            _log.LogWarning(ex, "Transient Graph error checking user {User} in group {Group}", userUpn, groupId);
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // 429 = transient throttling
            _log.LogWarning(ex, "Graph throttling (429) checking user {User} in group {Group}", userUpn, groupId);
            throw;
        }
    }
}
