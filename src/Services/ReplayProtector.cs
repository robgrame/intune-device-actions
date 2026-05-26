using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace IntuneWipeApi.Services;

/// <summary>
/// Validates the freshness of a request via X-Request-Timestamp and ensures the
/// X-Request-Nonce has not been seen recently. Nonces are kept in memory for
/// 2x the allowed skew window, which is sufficient to defeat replays
/// since older timestamps are rejected outright.
/// </summary>
public sealed class ReplayProtector
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _maxSkew;

    public ReplayProtector(IMemoryCache cache, IConfiguration cfg)
    {
        _cache = cache;
        var seconds = int.TryParse(cfg["Replay:MaxTimestampSkewSeconds"], out var s) ? s : 300;
        _maxSkew = TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 3600));
    }

    public (bool Ok, string? Reason) Validate(string? timestampHeader, string? nonceHeader)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader))
            return (false, "missing X-Request-Timestamp header");
        if (string.IsNullOrWhiteSpace(nonceHeader))
            return (false, "missing X-Request-Nonce header");

        if (!DateTimeOffset.TryParse(timestampHeader, null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var ts))
            return (false, "X-Request-Timestamp is not a valid ISO-8601 datetime");

        var delta = (DateTimeOffset.UtcNow - ts).Duration();
        if (delta > _maxSkew)
            return (false, $"X-Request-Timestamp skew {(int)delta.TotalSeconds}s exceeds {(int)_maxSkew.TotalSeconds}s");

        if (!Guid.TryParse(nonceHeader, out var nonce))
            return (false, "X-Request-Nonce must be a GUID");

        var key = $"nonce:{nonce:N}";
        if (_cache.TryGetValue(key, out _))
            return (false, "duplicate X-Request-Nonce (replay)");

        _cache.Set(key, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _maxSkew + _maxSkew,
            Size = 1
        });
        return (true, null);
    }
}
