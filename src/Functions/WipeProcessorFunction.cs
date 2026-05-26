using System.Text.Json;
using IntuneWipeApi.Models;
using IntuneWipeApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Functions;

/// <summary>
/// Internal queue-triggered processor: validates Entra group membership and ownership,
/// then issues the actual Intune wipe via Microsoft Graph.
/// Throws on transient/Graph errors so the queue retries (and eventually dead-letters to -poison).
/// </summary>
public sealed class WipeProcessorFunction
{
    private readonly GraphWipeService _graph;
    private readonly ILogger<WipeProcessorFunction> _log;

    public WipeProcessorFunction(GraphWipeService graph, ILogger<WipeProcessorFunction> log)
    {
        _graph = graph;
        _log = log;
    }

    [Function("WipeProcessor")]
    public async Task Run(
        [QueueTrigger("%Queue:WipeQueueName%", Connection = "AzureWebJobsStorage")] string messageJson,
        CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<WipeQueueMessage>(messageJson)
            ?? throw new InvalidOperationException("Empty/invalid queue payload");

        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = msg.CorrelationId,
            ["DeviceName"]    = msg.DeviceName,
            ["EntraDeviceId"] = msg.EntraDeviceId
        });

        _log.LogInformation("Processing wipe request for {Device}", msg.DeviceName);

        // 1) Resolve device directory object id from azureADDeviceId
        var deviceObjId = await _graph.GetDeviceObjectIdAsync(msg.EntraDeviceId, ct);
        if (deviceObjId is null)
        {
            _log.LogWarning("AUDIT denied reason=device-not-found-in-entra corr={Corr}", msg.CorrelationId);
            return; // do not retry — device just isn't there
        }

        // 2) Group membership check
        if (!await _graph.IsDeviceInAllowedGroupAsync(deviceObjId, ct))
        {
            _log.LogWarning("AUDIT denied reason=device-not-in-allowed-group device={Device} entra={Entra} corr={Corr}",
                msg.DeviceName, msg.EntraDeviceId, msg.CorrelationId);
            return; // not retryable
        }

        // 3) Ownership: managedDevice.azureADDeviceId must match
        var managedId = await _graph.ResolveAndValidateAsync(msg.IntuneDeviceId, msg.EntraDeviceId, ct);
        if (managedId is null)
        {
            _log.LogWarning("AUDIT denied reason=ownership-mismatch device={Device} corr={Corr}",
                msg.DeviceName, msg.CorrelationId);
            return;
        }

        // 4) Execute wipe — exceptions here trigger queue retry
        await _graph.WipeAsync(managedId, ct);

        _log.LogInformation("AUDIT wipe-issued device={Device} entra={Entra} intune={Intune} managed={Managed} corr={Corr}",
            msg.DeviceName, msg.EntraDeviceId, msg.IntuneDeviceId, managedId, msg.CorrelationId);
    }
}
