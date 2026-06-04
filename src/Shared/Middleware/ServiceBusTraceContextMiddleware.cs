using System.Diagnostics;
using System.Text.Json;
using IntuneDeviceActions.Telemetry;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Middleware;

/// <summary>
/// Restores the W3C <c>traceparent</c> context on Service Bus-triggered
/// Function invocations so that the trace id flows end-to-end across
/// Web → Service Bus → Proc → Service Bus → Wipe.
/// </summary>
/// <remarks>
/// <para>
/// The Azure Service Bus SDK 7.18+ automatically stamps the active
/// <see cref="Activity"/> context onto outgoing messages
/// (<c>ApplicationProperties["traceparent"]</c> and the legacy
/// <c>ApplicationProperties["Diagnostic-Id"]</c>). However, the Functions
/// isolated worker does NOT automatically restore that parent context when
/// the consumer-side <c>[ServiceBusTrigger]</c> fires — the invocation
/// activity is rooted at the gRPC call from the host, not at the producer.
/// </para>
/// <para>
/// This middleware reads the inbound message's <c>ApplicationProperties</c>
/// from <see cref="FunctionContext.BindingContext"/> (so the binding
/// signatures stay as <c>string messageJson</c>), parses out the
/// <c>traceparent</c>/<c>Diagnostic-Id</c> and any <c>tracestate</c>, and
/// opens a child <see cref="Activity"/> in
/// <see cref="InstrumentationActivity.Source"/> with that parent context.
/// Any downstream Activities created by the function body, the Azure SDK
/// senders, or the App Insights telemetry initializers inherit the same
/// trace id.
/// </para>
/// <para>
/// Non-Service-Bus invocations are skipped with zero overhead.
/// </para>
/// </remarks>
public sealed class ServiceBusTraceContextMiddleware : IFunctionsWorkerMiddleware
{
    // Keys the Functions isolated worker uses to surface Service Bus message
    // metadata via FunctionContext.BindingContext.BindingData. The value at
    // "ApplicationProperties" is a JSON-serialised dictionary of the message's
    // application properties (the Azure SDK keeps both "traceparent" and the
    // legacy "Diagnostic-Id" keys here).
    private const string ApplicationPropertiesKey = "ApplicationProperties";
    private const string MessageIdKey = "MessageId";
    private const string CorrelationIdKey = "CorrelationId";
    private const string EnqueuedTimeUtcKey = "EnqueuedTimeUtc";

    // W3C and legacy diagnostic header names that the Azure Service Bus SDK
    // stamps onto outbound messages.
    private const string TraceParentHeader = "traceparent";
    private const string TraceStateHeader = "tracestate";
    private const string LegacyDiagnosticIdHeader = "Diagnostic-Id";
    private const string LegacyCorrelationContextHeader = "Correlation-Context";

    private readonly ILogger<ServiceBusTraceContextMiddleware> _log;

    public ServiceBusTraceContextMiddleware(ILogger<ServiceBusTraceContextMiddleware> log)
    {
        _log = log;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!TryGetServiceBusApplicationProperties(context, out var appProps))
        {
            // Not a Service Bus invocation (or no app properties at all) — passthrough.
            await next(context);
            return;
        }

        var (traceParent, traceState) = ExtractTraceContext(appProps);
        if (string.IsNullOrEmpty(traceParent))
        {
            // Producer did not propagate a parent context — let the existing
            // invocation activity be the root, no need to start an extra span.
            await next(context);
            return;
        }

        if (!ActivityContext.TryParse(traceParent, traceState, out var parentContext))
        {
            _log.LogDebug("ServiceBusTraceContextMiddleware: ignoring malformed traceparent '{TraceParent}'", traceParent);
            await next(context);
            return;
        }

        var activityName = $"ServiceBus.process {context.FunctionDefinition.Name}";
        using var activity = InstrumentationActivity.Source.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext);

        if (activity is not null)
        {
            // Conventional semantic attributes (loose alignment with the
            // OpenTelemetry Messaging semantic conventions — full conformance
            // is not required for App Insights correlation).
            activity.SetTag("messaging.system", "servicebus");
            activity.SetTag("messaging.operation", "process");
            activity.SetTag("messaging.destination_kind", "queue");
            activity.SetTag("faas.trigger", "pubsub");
            activity.SetTag("faas.invocation_id", context.InvocationId);
            activity.SetTag("code.function", context.FunctionDefinition.Name);

            if (TryGetString(context.BindingContext.BindingData, MessageIdKey, out var messageId))
            {
                activity.SetTag("messaging.message.id", messageId);
            }
            if (TryGetString(context.BindingContext.BindingData, CorrelationIdKey, out var correlationId))
            {
                activity.SetTag("messaging.message.conversation_id", correlationId);
            }
            if (TryGetString(context.BindingContext.BindingData, EnqueuedTimeUtcKey, out var enqueuedTime))
            {
                activity.SetTag("messaging.servicebus.enqueued_time_utc", enqueuedTime);
            }
        }

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // Mark the activity as errored so the span shows up red in the
            // App Insights end-to-end transaction view; rethrow so the
            // Functions runtime still routes to the configured retry/poison
            // pipeline.
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddTag("exception.type", ex.GetType().FullName);
                activity.AddTag("exception.message", Truncate(ex.Message, 512));
            }
            throw;
        }
    }

    /// <summary>
    /// Locates the inbound Service Bus message's <c>ApplicationProperties</c>
    /// dictionary in the function's binding data. The Functions isolated
    /// worker exposes this as either an already-deserialised dictionary OR a
    /// JSON-encoded string depending on the binding shape, so we handle both.
    /// Returns <c>false</c> for triggers that aren't Service Bus.
    /// </summary>
    private static bool TryGetServiceBusApplicationProperties(
        FunctionContext context,
        out IReadOnlyDictionary<string, string?> appProps)
    {
        appProps = new Dictionary<string, string?>(0);
        var data = context.BindingContext?.BindingData;
        if (data is null || !data.TryGetValue(ApplicationPropertiesKey, out var raw) || raw is null)
        {
            return false;
        }

        // Common case: the worker hands back the raw JSON string of the props.
        if (raw is string rawString)
        {
            if (string.IsNullOrWhiteSpace(rawString) || rawString == "{}")
            {
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(rawString);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                            => prop.Value.GetRawText(),
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText(),
                    };
                }
                appProps = dict;
                return appProps.Count > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // Less common: already-typed dictionary (defensive — older/newer SDKs).
        if (raw is IReadOnlyDictionary<string, object?> typed)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in typed)
            {
                dict[kv.Key] = kv.Value?.ToString();
            }
            appProps = dict;
            return appProps.Count > 0;
        }

        return false;
    }

    private static (string? TraceParent, string? TraceState) ExtractTraceContext(
        IReadOnlyDictionary<string, string?> appProps)
    {
        // Prefer the W3C standard header; fall back to the legacy Diagnostic-Id
        // (older Azure SDK versions used this format exclusively).
        if (appProps.TryGetValue(TraceParentHeader, out var tp) && !string.IsNullOrWhiteSpace(tp))
        {
            appProps.TryGetValue(TraceStateHeader, out var ts);
            return (tp, ts);
        }
        if (appProps.TryGetValue(LegacyDiagnosticIdHeader, out var legacy) && !string.IsNullOrWhiteSpace(legacy))
        {
            appProps.TryGetValue(LegacyCorrelationContextHeader, out var legacyState);
            return (legacy, legacyState);
        }
        return (null, null);
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object?>? data,
        string key,
        out string value)
    {
        value = string.Empty;
        if (data is null || !data.TryGetValue(key, out var raw) || raw is null) return false;
        value = raw switch
        {
            string s => s,
            _ => raw.ToString() ?? string.Empty,
        };
        return !string.IsNullOrEmpty(value);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }
}
