using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace IntuneWipeApi.Services;

/// <summary>
/// Resolves a directory device id (Entra Device Id) from claim values that are not GUIDs,
/// by querying Microsoft Graph. Intended for legacy AD CS / on-prem PKI scenarios where the
/// client certificate carries an AD-style identity (Subject DN / SAN DNS FQDN) instead of an
/// Entra Device Id.
///
/// Security model:
///   * Fail-closed on Graph errors, zero matches, OR multiple matches (ambiguity = denial).
///   * Cache hits (and explicit misses) reduce Graph load and mitigate throttling. The cache
///     stores both positive and negative results with separate TTLs.
///   * Cached negatives only persist for a short window so that newly-onboarded devices are
///     pickable quickly, while still amortising cost when a single bogus cert hits the API.
///   * Cache is not authoritative: group-membership check downstream still enforces the
///     authorization gate, so a stale positive does NOT grant a wipe by itself.
/// </summary>
public sealed class DeviceDirectoryResolver
{
    private readonly GraphServiceClient _graph;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DeviceDirectoryResolver> _log;
    private readonly TimeSpan _positiveTtl;
    private readonly TimeSpan _negativeTtl;

    public DeviceDirectoryResolver(GraphServiceClient graph, IMemoryCache cache,
        IConfiguration cfg, ILogger<DeviceDirectoryResolver> log)
    {
        _graph = graph;
        _cache = cache;
        _log = log;
        _positiveTtl = TimeSpan.FromMinutes(
            int.TryParse(cfg["ClientCert:DirectoryLookupPositiveTtlMinutes"], out var p) && p > 0 ? p : 15);
        _negativeTtl = TimeSpan.FromMinutes(
            int.TryParse(cfg["ClientCert:DirectoryLookupNegativeTtlMinutes"], out var n) && n > 0 ? n : 1);
    }

    /// <summary>
    /// Looks up a device's EntraDeviceId by the DNS name observed in the client certificate
    /// SAN. Tries the full FQDN first, then the short hostname (left-most label) — AD CS
    /// templates frequently issue with both forms while Entra displayName usually carries
    /// just the short name.
    /// Returns null on: not found, ambiguity (>1 match), or Graph failure.
    /// </summary>
    public async Task<string?> ResolveByDnsNameAsync(string? dnsName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dnsName)) return null;

        var fqdn = dnsName.Trim().ToLowerInvariant();
        var shortName = fqdn.Split('.', 2)[0];

        // Try FQDN first, then short hostname. Each lookup is independently cached so that
        // a hit on the short-name path doesn't get re-evaluated on every subsequent request.
        return await ResolveCachedAsync(fqdn, ct) ?? await ResolveCachedAsync(shortName, ct);
    }

    private async Task<string?> ResolveCachedAsync(string displayName, CancellationToken ct)
    {
        var cacheKey = "devdir:" + displayName;
        if (_cache.TryGetValue<string?>(cacheKey, out var cached))
        {
            return cached; // may be null (cached negative)
        }

        string? resolved = null;
        try
        {
            var page = await _graph.Devices.GetAsync(req =>
            {
                req.QueryParameters.Filter = $"displayName eq '{EscapeOData(displayName)}'";
                req.QueryParameters.Select = new[] { "id", "deviceId", "displayName", "accountEnabled" };
                req.QueryParameters.Top = 2; // we only care about "exactly one"
            }, ct);

            var matches = page?.Value ?? new List<Microsoft.Graph.Models.Device>();
            if (matches.Count == 0)
            {
                _log.LogInformation("Directory lookup: no device with displayName='{Name}'", displayName);
            }
            else if (matches.Count > 1)
            {
                _log.LogWarning("Directory lookup: AMBIGUOUS displayName='{Name}' returned {Count} devices; fail-closed",
                    displayName, matches.Count);
            }
            else
            {
                var d = matches[0];
                resolved = d.DeviceId; // the GUID we want is `deviceId`, not `id` (directory object id)
                if (string.IsNullOrEmpty(resolved))
                {
                    _log.LogWarning("Directory lookup: displayName='{Name}' matched a device with empty deviceId; fail-closed", displayName);
                    resolved = null;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Directory lookup failed for displayName='{Name}'; fail-closed", displayName);
            // Do NOT cache the failure as a permanent negative — let next request retry.
            return null;
        }

        _cache.Set(cacheKey, resolved, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = resolved is null ? _negativeTtl : _positiveTtl,
            Size = 1
        });
        return resolved;
    }

    private static string EscapeOData(string s) => s.Replace("'", "''");
}
