using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Capabilities.Rename.Services;

/// <summary>
/// <see cref="ICustomerRenameClient"/> over <see cref="HttpClient"/>. Reads
/// the endpoint URL and an optional auth header (name + value, typically a
/// Key Vault reference) from configuration:
/// <list type="bullet">
///   <item><c>Rename:Endpoint</c> — absolute URL (required).</item>
///   <item><c>Rename:AuthHeaderName</c> — header name. Default <c>X-Api-Key</c>. Empty disables.</item>
///   <item><c>Rename:AuthHeaderValue</c> — header value. Recommended: Key Vault reference.</item>
///   <item><c>Rename:TimeoutSeconds</c> — request timeout. Default <c>30</c>.</item>
/// </list>
///
/// Classification mirrors <c>GraphErrorClassifier</c>:
/// <list type="bullet">
///   <item>2xx → <see cref="RenameRestOutcome.Kind.Accepted"/></item>
///   <item>4xx (except 408 / 429) → <see cref="RenameRestOutcome.Kind.Permanent"/></item>
///   <item>5xx, 408, 429, <see cref="TaskCanceledException"/> (timeout), <see cref="HttpRequestException"/> → <see cref="RenameRestOutcome.Kind.Transient"/></item>
/// </list>
/// </summary>
public sealed class HttpCustomerRenameClient : ICustomerRenameClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<HttpCustomerRenameClient> _log;

    public HttpCustomerRenameClient(HttpClient http, IConfiguration cfg, ILogger<HttpCustomerRenameClient> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;

        // Only set the timeout once — HttpClient.Timeout is process-wide and
        // throws if set after a request. The IHttpClientFactory typed-client
        // gives us a fresh instance per scope so this branch is safe.
        if (_http.Timeout == TimeSpan.FromSeconds(100)) // default HttpClient.Timeout
        {
            var seconds = int.TryParse(_cfg["Rename:TimeoutSeconds"], out var t) && t > 0 ? t : 30;
            _http.Timeout = TimeSpan.FromSeconds(seconds);
        }
    }

    public async Task<RenameRestOutcome> RenameAsync(RenameRestRequest request, CancellationToken ct)
    {
        var endpoint = _cfg["Rename:Endpoint"]
            ?? throw new InvalidOperationException(
                "Rename:Endpoint is not configured (must be the customer-internal rename URL).");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request),
        };

        var headerName  = _cfg["Rename:AuthHeaderName"]  ?? "X-Api-Key";
        var headerValue = _cfg["Rename:AuthHeaderValue"];
        if (!string.IsNullOrWhiteSpace(headerName) && !string.IsNullOrWhiteSpace(headerValue))
        {
            req.Headers.TryAddWithoutValidation(headerName, headerValue);
        }

        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            req.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException tcex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(tcex, "Customer rename endpoint timed out (corr={Corr})", request.CorrelationId);
            return new RenameRestOutcome(RenameRestOutcome.Kind.Transient, 0, "timeout");
        }
        catch (HttpRequestException hrex)
        {
            _log.LogWarning(hrex, "Customer rename endpoint unreachable (corr={Corr})", request.CorrelationId);
            return new RenameRestOutcome(RenameRestOutcome.Kind.Transient, 0, $"network:{hrex.Message}");
        }

        var status = (int)resp.StatusCode;
        if (resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            return new RenameRestOutcome(RenameRestOutcome.Kind.Accepted, status, "accepted");
        }

        string body;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Length > 200) body = body[..200] + "…";
        }
        catch { body = "(unavailable)"; }
        resp.Dispose();

        var kind = status switch
        {
            (int)HttpStatusCode.RequestTimeout  => RenameRestOutcome.Kind.Transient, // 408
            (int)HttpStatusCode.TooManyRequests => RenameRestOutcome.Kind.Transient, // 429
            >= 400 and < 500                    => RenameRestOutcome.Kind.Permanent,
            >= 500                              => RenameRestOutcome.Kind.Transient,
            _                                   => RenameRestOutcome.Kind.Transient,
        };

        return new RenameRestOutcome(kind, status, $"http-{status}:{body}");
    }
}
