using System.Text.Json;
using Azure.Messaging.ServiceBus;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Capabilities.Rename.Senders;
using IntuneDeviceActions.Services;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Capabilities.Rename.Runners;

/// <summary>
/// <see cref="IActionRunner"/> registered on the proc role for the
/// <c>device-rename</c> action type. Forwards the dispatch envelope to the
/// per-capability <c>rename-action</c> Service Bus queue consumed by the
/// dedicated Rename Function App. Structurally identical to
/// <c>BitLockerForwardingRunner</c>.
/// </summary>
public sealed class RenameForwardingRunner : IActionRunner
{
    public string Type => "device-rename";

    private readonly RenameActionSender _sender;
    private readonly AuditService _audit;
    private readonly ILogger<RenameForwardingRunner> _log;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RenameForwardingRunner(RenameActionSender sender, AuditService audit,
        ILogger<RenameForwardingRunner> log)
    {
        _sender = sender;
        _audit = audit;
        _log = log;
    }

    public async Task RunAsync(ActionDispatchMessage envelope, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        _log.LogDebug("RenameForwardingRunner sending envelope: corr={Corr} bytes={Bytes} queue={Queue}",
            envelope.CorrelationId, System.Text.Encoding.UTF8.GetByteCount(json), _sender.Sender.EntityPath);

        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = envelope.CorrelationId,
            CorrelationId = envelope.CorrelationId,
        };
        sbMessage.ApplicationProperties["actionType"] = envelope.ActionType;
        sbMessage.ApplicationProperties["schemaVersion"] = envelope.SchemaVersion;
        await _sender.Sender.SendMessageAsync(sbMessage, ct);

        _audit.TrackEvent(AuditEvents.ActionForwarded, new Dictionary<string, string>
        {
            [AuditEvents.Prop.CorrelationId]  = envelope.CorrelationId,
            [AuditEvents.Prop.ActionType]     = envelope.ActionType,
            [AuditEvents.Prop.DeviceName]     = envelope.DeviceName,
            [AuditEvents.Prop.EntraDeviceId]  = envelope.EntraDeviceId,
            [AuditEvents.Prop.IntuneDeviceId] = envelope.IntuneDeviceId,
            ["targetQueue"]                   = _sender.Sender.EntityPath,
            ["targetApp"]                     = "rename-runner",
        });

        _log.LogInformation(
            "Forwarded action '{ActionType}' for {Device} to dedicated rename-runner queue '{Queue}' (corr={Corr})",
            envelope.ActionType, envelope.DeviceName, _sender.Sender.EntityPath, envelope.CorrelationId);
    }
}
