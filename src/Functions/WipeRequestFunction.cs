using System.Net;
using System.Text.Json;
using Azure.Storage.Queues;
using IntuneWipeApi.Models;
using IntuneWipeApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Functions;

/// <summary>
/// Public HTTP endpoint: validates the client (cert + payload) and enqueues a wipe request.
/// All heavy lifting (group membership, Graph wipe) happens asynchronously in WipeProcessorFunction.
/// </summary>
public sealed class WipeRequestFunction
{
    private readonly ClientCertValidator _cert;
    private readonly QueueClient _queue;
    private readonly ILogger<WipeRequestFunction> _log;

    public WipeRequestFunction(ClientCertValidator cert, QueueClient queue, ILogger<WipeRequestFunction> log)
    {
        _cert = cert;
        _queue = queue;
        _log = log;
    }

    [Function("WipeRequest")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "wipe")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var scope = _log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        var (ok, cert, reason) = _cert.Validate(req.HttpContext);
        if (!ok)
        {
            _log.LogWarning("Cert validation failed: {Reason}", reason);
            return new ObjectResult(new { status = "denied", message = $"client cert: {reason}", correlationId })
                { StatusCode = (int)HttpStatusCode.Unauthorized };
        }

        WipeRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<WipeRequest>(req.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid JSON");
            return new BadRequestObjectResult(new { status = "error", message = "invalid JSON", correlationId });
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.EntraDeviceId)
            || string.IsNullOrWhiteSpace(body.IntuneDeviceId)
            || string.IsNullOrWhiteSpace(body.DeviceName))
        {
            return new BadRequestObjectResult(new
            {
                status = "error",
                message = "deviceName, entraDeviceId, intuneDeviceId are required",
                correlationId
            });
        }

        if (!Guid.TryParse(body.EntraDeviceId, out _) || !Guid.TryParse(body.IntuneDeviceId, out _))
        {
            return new BadRequestObjectResult(new
            {
                status = "error",
                message = "entraDeviceId and intuneDeviceId must be GUIDs",
                correlationId
            });
        }

        var msg = new WipeQueueMessage
        {
            DeviceName = body.DeviceName!,
            EntraDeviceId = body.EntraDeviceId!,
            IntuneDeviceId = body.IntuneDeviceId!,
            CorrelationId = correlationId,
            ClientCertThumbprint = cert?.Thumbprint,
            RequestedAt = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.Serialize(msg);
        await _queue.CreateIfNotExistsAsync(cancellationToken: ct);
        await _queue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)), ct);

        _log.LogInformation("AUDIT wipe-request enqueued device={DeviceName} entra={EntraId} intune={IntuneId} cert={Thumb} corr={Corr}",
            msg.DeviceName, msg.EntraDeviceId, msg.IntuneDeviceId, msg.ClientCertThumbprint, correlationId);

        return new AcceptedResult(string.Empty, new
        {
            status = "queued",
            message = "wipe request accepted and queued",
            correlationId
        });
    }
}
