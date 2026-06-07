using System.Text;
using System.Text.Json;
using IntuneDeviceActions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace IntuneDeviceActions.Capabilities.Rename.Services;

/// <summary>
/// Thin wrapper around <see cref="GraphServiceClient"/> exposing the
/// Rename-capability-specific Microsoft Graph operations:
/// <list type="bullet">
///   <item><see cref="FindDisplayNameCollisionsAsync"/> — pre-flight check that
///         queries Entra <c>/devices</c> for objects already using the desired
///         <c>displayName</c> (Entra does NOT enforce uniqueness on device
///         displayName, unlike on-prem AD).</item>
///   <item><see cref="SetDeviceNameAsync"/> — POSTs the Intune managed-device
///         <c>setDeviceName</c> action which schedules the rename + reboot on
///         the next MDM sync.</item>
/// </list>
/// </summary>
public sealed class GraphRenameService
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<GraphRenameService> _log;

    public GraphRenameService(GraphServiceClient graph, ILogger<GraphRenameService> log)
    {
        _graph = graph;
        _log = log;
    }

    /// <summary>
    /// Returns Entra device objects whose <c>displayName</c> already matches
    /// <paramref name="newName"/>. The current device (identified by its
    /// <paramref name="excludeEntraDeviceId"/>, the Entra <c>deviceId</c>
    /// GUID) is filtered out — re-naming a device to its current name is
    /// idempotent, not a collision.
    /// </summary>
    public async Task<IReadOnlyList<DeviceCollision>> FindDisplayNameCollisionsAsync(
        string newName, string? excludeEntraDeviceId, CancellationToken ct)
    {
        // OData filter — single-quote escape per OData v4.
        var escaped = newName.Replace("'", "''");
        var page = await _graph.Devices.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"displayName eq '{escaped}'";
            rc.QueryParameters.Select = new[] { "id", "deviceId", "displayName", "accountEnabled" };
            rc.QueryParameters.Top    = 25;
        }, ct);

        var list = page?.Value ?? new List<Microsoft.Graph.Models.Device>();
        var results = new List<DeviceCollision>(list.Count);
        foreach (var d in list)
        {
            if (!string.IsNullOrEmpty(excludeEntraDeviceId)
                && !string.IsNullOrEmpty(d.DeviceId)
                && string.Equals(d.DeviceId, excludeEntraDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue; // same device — not a collision.
            }
            results.Add(new DeviceCollision(
                EntraObjectId: d.Id ?? string.Empty,
                EntraDeviceId: d.DeviceId ?? string.Empty,
                DisplayName:   d.DisplayName ?? string.Empty,
                AccountEnabled: d.AccountEnabled));
        }
        return results;
    }

    /// <summary>
    /// Returns Intune <c>managedDevice</c> objects whose <c>deviceName</c>
    /// already matches <paramref name="newName"/>. Complements
    /// <see cref="FindDisplayNameCollisionsAsync"/> because the Intune side
    /// may carry a rename that has not yet propagated back to Entra (Intune
    /// queues the rename for the next MDM sync, then re-syncs the new name
    /// up to Entra), so checking only Entra would miss in-flight renames
    /// applied via another channel (manual portal action, another runner
    /// instance that already issued setDeviceName, …).
    /// The current device is filtered out via <paramref name="excludeManagedDeviceId"/>
    /// (the managed-device id == <c>id</c> on the managedDevices entity).
    /// </summary>
    public async Task<IReadOnlyList<DeviceCollision>> FindManagedDeviceNameCollisionsAsync(
        string newName, string excludeManagedDeviceId, CancellationToken ct)
    {
        var escaped = newName.Replace("'", "''");
        var page = await _graph.DeviceManagement.ManagedDevices.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = $"deviceName eq '{escaped}'";
            rc.QueryParameters.Select = new[] { "id", "azureADDeviceId", "deviceName" };
            rc.QueryParameters.Top    = 25;
        }, ct);

        var list = page?.Value ?? new List<Microsoft.Graph.Models.ManagedDevice>();
        var results = new List<DeviceCollision>(list.Count);
        foreach (var d in list)
        {
            if (!string.IsNullOrEmpty(excludeManagedDeviceId)
                && !string.IsNullOrEmpty(d.Id)
                && string.Equals(d.Id, excludeManagedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            results.Add(new DeviceCollision(
                EntraObjectId:  string.Empty,
                EntraDeviceId:  d.AzureADDeviceId ?? string.Empty,
                DisplayName:    d.DeviceName ?? string.Empty,
                AccountEnabled: null));
        }
        return results;
    }

    /// <summary>
    /// Invokes the Intune Graph <c>POST /deviceManagement/managedDevices/{id}/setDeviceName</c>
    /// action. Intune queues the rename and applies it on the next MDM sync;
    /// Windows requires a reboot to complete the change.
    /// </summary>
    /// <remarks>
    /// The Graph .NET SDK v6 does not generate a strongly-typed
    /// <c>SetDeviceName</c> request builder for this action, so we use the
    /// underlying Kiota <c>RequestAdapter</c> directly. Errors are surfaced
    /// as <see cref="ODataError"/> (via the standard error code map, mirroring
    /// BitLocker's <c>4XX</c>/<c>5XX</c>/<c>XXX</c> triple) so
    /// <see cref="GraphErrorClassifier"/> can classify them uniformly.
    /// </remarks>
    public async Task SetDeviceNameAsync(string managedDeviceId, string newName, CancellationToken ct)
    {
        var url = $"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices/{Uri.EscapeDataString(managedDeviceId)}/setDeviceName";
        var ri = new RequestInformation
        {
            HttpMethod = Method.POST,
            URI = new Uri(url),
        };
        ri.Headers.Add("Content-Type", "application/json");
        var bodyJson = JsonSerializer.Serialize(new { deviceName = newName });
        ri.Content = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));

        var errorMapping = new Dictionary<string, ParsableFactory<IParsable>>
        {
            { "4XX", ODataError.CreateFromDiscriminatorValue },
            { "5XX", ODataError.CreateFromDiscriminatorValue },
            { "XXX", ODataError.CreateFromDiscriminatorValue },
        };
        await _graph.RequestAdapter.SendNoContentAsync(ri, errorMapping, ct);
    }

    /// <summary>
    /// Mirrors <c>GraphErrorClassifier</c>. Centralised here so the runner can
    /// pattern-match <see cref="ODataError"/> exceptions thrown by either
    /// <see cref="FindDisplayNameCollisionsAsync"/> or <see cref="SetDeviceNameAsync"/>.
    /// </summary>
    public static GraphErrorClassifier.GraphErrorKind Classify(Exception ex) => GraphErrorClassifier.Classify(ex);
}

/// <summary>
/// A pre-existing Entra device whose <c>displayName</c> collides with the name
/// the runner is about to apply. Emitted in <c>rename.collision.detected</c>
/// audit events.
/// </summary>
public sealed record DeviceCollision(
    string EntraObjectId,
    string EntraDeviceId,
    string DisplayName,
    bool? AccountEnabled);
