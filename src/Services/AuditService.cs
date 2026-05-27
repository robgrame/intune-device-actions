using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Services;

/// <summary>
/// Emits security/audit events for the wipe pipeline as Application Insights
/// <c>customEvents</c> (queryable as <c>customEvents | where name startswith "wipe."</c>).
///
/// Why not just <see cref="ILogger"/>? Logger traces ride on the same App Insights
/// pipeline as application traces and are subject to adaptive sampling; a destructive
/// operation requires audit evidence to NEVER be dropped. <c>TrackEvent</c> on the
/// telemetry client bypasses adaptive sampling when sampling is disabled on the
/// worker telemetry options (see Program.cs <c>EnableAdaptiveSampling = false</c>),
/// and the events are stored in a dedicated table for retention/archival.
///
/// The method also writes an ILogger entry so support engineers without App Insights
/// access can still grep logs locally during dev/test.
/// </summary>
public sealed class AuditService
{
    private const string AuditMarkerKey = "audit";
    private const string AuditMarkerValue = "true";

    private readonly TelemetryClient _telemetry;
    private readonly ILogger<AuditService> _log;

    public AuditService(TelemetryClient telemetry, ILogger<AuditService> log)
    {
        _telemetry = telemetry;
        _log = log;
    }

    public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
    {
        var props = properties is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(properties, StringComparer.Ordinal);
        props[AuditMarkerKey] = AuditMarkerValue;

        _telemetry.TrackEvent(eventName, props);

        // Mirror to ILogger as a single structured entry. Stays useful for local dev
        // (Azurite + console) and for non-AppInsights log sinks if added later.
        _log.LogInformation("AUDIT {EventName} {@Properties}", eventName, props);
    }

    /// <summary>
    /// Convenience overload for events tied to an exception (transient/permanent
    /// failure paths). Exception goes through TrackException as well so it shows
    /// up in the exceptions table with the same correlation id.
    /// </summary>
    public void TrackEvent(string eventName, Exception exception, IDictionary<string, string>? properties = null)
    {
        var props = properties is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(properties, StringComparer.Ordinal);
        props[AuditEvents.Prop.ExceptionType] = exception.GetType().FullName ?? exception.GetType().Name;
        props[AuditEvents.Prop.ExceptionMessage] = exception.Message;
        TrackEvent(eventName, props);
        _telemetry.TrackException(exception, props);
    }
}
