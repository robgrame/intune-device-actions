using System.Diagnostics;
using System.Reflection;

namespace IntuneDeviceActions.Telemetry;

/// <summary>
/// Single shared <see cref="ActivitySource"/> used by all three Function App
/// hosts (Web, Proc, Wipe) to emit OpenTelemetry spans for code paths that
/// are NOT already instrumented by the Azure SDK or the Functions runtime.
/// </summary>
/// <remarks>
/// <para>
/// Concretely, this is the source the
/// <see cref="Middleware.ServiceBusTraceContextMiddleware"/> uses to start a
/// <c>Consumer</c>-kind activity whose parent context is the W3C
/// <c>traceparent</c> carried in the inbound Service Bus message — that is
/// the link that lets App Insights stitch the Web → Service Bus → Wipe
/// pipeline into a single end-to-end trace.
/// </para>
/// <para>
/// Custom code paths (e.g. runners, status updaters) can also use
/// <see cref="Source"/> directly to add their own spans without taking a
/// dependency on the OpenTelemetry API beyond <see cref="ActivitySource"/>.
/// </para>
/// </remarks>
public static class InstrumentationActivity
{
    /// <summary>
    /// Activity source name. Add this to the OpenTelemetry
    /// <c>TracerProviderBuilder</c> via <c>AddSource</c> to enable export of
    /// spans produced through <see cref="Source"/>.
    /// </summary>
    public const string ServiceName = "IntuneDeviceActions";

    private static readonly string AssemblyVersion =
        typeof(InstrumentationActivity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(InstrumentationActivity).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";

    public static readonly ActivitySource Source = new(ServiceName, AssemblyVersion);
}
