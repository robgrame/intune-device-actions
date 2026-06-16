using Azure;
using Azure.Core;
using Azure.Messaging.EventGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Services;

/// <summary>
/// Publishes audit events to an Event Grid custom topic so external operator
/// surfaces (portal/automation/SIEM) can react in near real-time.
/// </summary>
public sealed class EventGridAuditEventPublisher : IAuditEventPublisher
{
    private readonly EventGridPublisherClient? _client;
    private readonly ILogger<EventGridAuditEventPublisher> _log;
    private readonly string _role;
    private readonly bool _enabled;

    public EventGridAuditEventPublisher(IConfiguration cfg, TokenCredential cred, ILogger<EventGridAuditEventPublisher> log)
    {
        _log = log;
        _role = cfg["App:Role"] ?? "unknown";

        _enabled = bool.TryParse(cfg["EventGrid:Enabled"], out var enabled) && enabled;
        if (!_enabled)
        {
            return;
        }

        var endpoint = cfg["EventGrid:AuditTopicEndpoint"] ?? cfg["EventGrid:TopicEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("EventGrid audit stream enabled but EventGrid:AuditTopicEndpoint is missing or invalid.");
            return;
        }

        var topicKey = cfg["EventGrid:TopicKey"];
        _client = string.IsNullOrWhiteSpace(topicKey)
            ? new EventGridPublisherClient(endpointUri, cred)
            : new EventGridPublisherClient(endpointUri, new AzureKeyCredential(topicKey));
    }

    public void Publish(string eventName, IDictionary<string, string> properties, LogLevel logLevel)
    {
        if (!_enabled || _client is null) return;

        var corr = properties.TryGetValue(AuditEvents.Prop.CorrelationId, out var correlationId)
            ? correlationId
            : "none";
        var actionType = properties.TryGetValue(AuditEvents.Prop.ActionType, out var at)
            ? at
            : "unknown";

        var subject = $"/intune-device-actions/{_role}/{actionType}/{corr}";
        var payload = new AuditStreamEnvelope(
            EventName: eventName,
            EmittedAtUtc: DateTimeOffset.UtcNow,
            Role: _role,
            LogLevel: logLevel.ToString(),
            Properties: new Dictionary<string, string>(properties, StringComparer.Ordinal));

        var evt = new EventGridEvent(
            subject: subject,
            eventType: eventName,
            dataVersion: "1.0",
            data: BinaryData.FromObjectAsJson(payload));

        try
        {
            _client.SendEvent(evt);
        }
        catch (RequestFailedException ex)
        {
            _log.LogWarning(ex, "EventGrid audit publish failed for {EventName} (status={Status})", eventName, ex.Status);
        }
    }

    private sealed record AuditStreamEnvelope(
        string EventName,
        DateTimeOffset EmittedAtUtc,
        string Role,
        string LogLevel,
        Dictionary<string, string> Properties);
}

