using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Services;

/// <summary>
/// Optional secondary sink for audit events (e.g. Event Grid fanout).
/// </summary>
public interface IAuditEventPublisher
{
    void Publish(string eventName, IDictionary<string, string> properties, LogLevel logLevel);
}

