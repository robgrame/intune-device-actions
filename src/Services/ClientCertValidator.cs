using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneWipeApi.Services;

public sealed class ClientCertValidator
{
    private readonly HashSet<string> _allowedThumbprints;
    private readonly string? _allowedIssuer;
    private readonly bool _required;
    private readonly ILogger<ClientCertValidator> _log;

    public ClientCertValidator(IConfiguration cfg, ILogger<ClientCertValidator> log)
    {
        _log = log;
        _allowedThumbprints = (cfg["ClientCert:AllowedThumbprints"] ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToUpperInvariant())
            .ToHashSet();
        _allowedIssuer = cfg["ClientCert:AllowedIssuer"];
        _required = bool.TryParse(cfg["ClientCert:RequireClientCert"], out var r) ? r : true;
    }

    /// <summary>
    /// Validates the client certificate. Returns (ok, cert, reason).
    /// Looks for the cert in HttpContext.Connection.ClientCertificate or X-ARR-ClientCert header.
    /// </summary>
    public (bool Ok, X509Certificate2? Cert, string? Reason) Validate(HttpContext ctx)
    {
        X509Certificate2? cert = ctx.Connection.ClientCertificate;

        if (cert is null && ctx.Request.Headers.TryGetValue("X-ARR-ClientCert", out var header))
        {
            try
            {
                var raw = header.ToString()
                    .Replace("-----BEGIN CERTIFICATE-----", string.Empty)
                    .Replace("-----END CERTIFICATE-----", string.Empty)
                    .Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (!string.IsNullOrEmpty(raw))
                {
                    var bytes = Convert.FromBase64String(raw);
                    cert = new X509Certificate2(bytes);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to parse X-ARR-ClientCert header");
            }
        }

        if (cert is null)
            return (!_required, null, _required ? "client certificate missing" : null);

        // Validity period
        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
            return (false, cert, "certificate expired or not yet valid");

        var thumb = cert.Thumbprint?.ToUpperInvariant() ?? string.Empty;

        if (_allowedThumbprints.Count > 0 && !_allowedThumbprints.Contains(thumb))
            return (false, cert, $"thumbprint {thumb} not in allow-list");

        if (!string.IsNullOrWhiteSpace(_allowedIssuer) &&
            !string.Equals(cert.Issuer, _allowedIssuer, StringComparison.OrdinalIgnoreCase))
            return (false, cert, $"issuer '{cert.Issuer}' not allowed");

        return (true, cert, null);
    }
}
