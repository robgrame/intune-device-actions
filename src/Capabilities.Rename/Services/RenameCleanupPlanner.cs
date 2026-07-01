namespace IntuneDeviceActions.Capabilities.Rename.Services;

/// <summary>
/// A minimal projection of an Entra <c>device</c> directory object, carrying
/// only the fields the pre-rename cleanup needs. Kept capability-local (never
/// in Shared) per the plug-in architecture rules.
/// </summary>
public sealed record EntraDeviceRecord(
    string ObjectId,
    string DeviceId,
    string DisplayName,
    string? TrustType,
    IReadOnlyList<string> PhysicalIds,
    bool? AccountEnabled);

/// <summary>
/// Result of planning the HWID-duplicate cleanup: the devices to keep (the
/// Entra ID Joined object and the device itself) and the duplicates to delete.
/// </summary>
public sealed record HwidCleanupPlan(
    IReadOnlyList<EntraDeviceRecord> Keep,
    IReadOnlyList<EntraDeviceRecord> Delete,
    bool HasEntraJoinedKeeper);

/// <summary>
/// Pure (side-effect-free, unit-testable) planning logic for the pre-rename
/// directory cleanup. All Microsoft Graph I/O lives in
/// <see cref="GraphRenameService"/>; this class only classifies records so the
/// keep/delete decision can be asserted in isolation.
/// </summary>
public static class RenameCleanupPlanner
{
    /// <summary>Entra <c>trustType</c> for an Entra ID Joined device.</summary>
    public const string TrustEntraJoined = "AzureAd";

    /// <summary>Entra <c>trustType</c> for a Hybrid / on-prem AD joined device.</summary>
    public const string TrustHybrid = "ServerAd";

    /// <summary>Prefix Entra uses for the hardware id inside <c>physicalIds</c> (form <c>[HWID]:h:&lt;value&gt;</c>).</summary>
    public const string HwidPrefix = "[HWID]:";

    /// <summary>
    /// Extracts the full <c>[HWID]:h:&lt;value&gt;</c> token from a device's
    /// <c>physicalIds</c> collection, or <c>null</c> when the device carries no
    /// hardware id. The token is returned verbatim so it can be used as an
    /// exact OData equality match against other devices' <c>physicalIds</c>.
    /// </summary>
    public static string? ExtractHwid(IEnumerable<string>? physicalIds)
    {
        if (physicalIds is null) return null;
        foreach (var p in physicalIds)
        {
            if (!string.IsNullOrWhiteSpace(p)
                && p.StartsWith(HwidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// From the set of Entra devices already carrying the target
    /// <c>displayName</c>, selects the "AD" objects to delete: those whose
    /// <c>trustType</c> is in <paramref name="trustTypes"/> (default
    /// <see cref="TrustHybrid"/>) and that are NOT the device being renamed
    /// (matched by <paramref name="selfDeviceId"/>). Renaming a device to a
    /// name a stale hybrid object still holds would collide on the next
    /// Entra Connect sync, so those shadows are removed first.
    /// </summary>
    public static IReadOnlyList<EntraDeviceRecord> PlanAdNameDeletions(
        IReadOnlyList<EntraDeviceRecord> matches,
        string? selfDeviceId,
        ISet<string> trustTypes)
    {
        var result = new List<EntraDeviceRecord>();
        foreach (var d in matches)
        {
            if (IsSelf(d, selfDeviceId)) continue;
            if (d.TrustType is not null && trustTypes.Contains(d.TrustType))
            {
                result.Add(d);
            }
        }
        return result;
    }

    /// <summary>
    /// Classifies the devices sharing one HWID into keep vs delete. Keepers are
    /// the Entra ID Joined object (<c>trustType == AzureAd</c>) and the device
    /// itself (the Hybrid Joined object, matched by
    /// <paramref name="selfDeviceId"/>); everything else is a duplicate to
    /// delete. <see cref="HwidCleanupPlan.HasEntraJoinedKeeper"/> reports
    /// whether an Entra ID Joined keeper was present (a safety signal for the
    /// runner — the device itself is always kept regardless).
    /// </summary>
    public static HwidCleanupPlan PlanHwidDeletions(
        IReadOnlyList<EntraDeviceRecord> shared,
        string? selfDeviceId)
    {
        var keep = new List<EntraDeviceRecord>();
        var delete = new List<EntraDeviceRecord>();
        var hasEntraJoinedKeeper = false;

        foreach (var d in shared)
        {
            var isSelf = IsSelf(d, selfDeviceId);
            var isEntraJoined = string.Equals(d.TrustType, TrustEntraJoined, StringComparison.OrdinalIgnoreCase);
            if (isEntraJoined) hasEntraJoinedKeeper = true;

            if (isSelf || isEntraJoined)
            {
                keep.Add(d);
            }
            else
            {
                delete.Add(d);
            }
        }

        return new HwidCleanupPlan(keep, delete, hasEntraJoinedKeeper);
    }

    /// <summary>Parses a CSV of trust-type names into a case-insensitive set (default <see cref="TrustHybrid"/>).</summary>
    public static ISet<string> ParseTrustTypes(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }
        }
        if (set.Count == 0) set.Add(TrustHybrid);
        return set;
    }

    private static bool IsSelf(EntraDeviceRecord d, string? selfDeviceId) =>
        !string.IsNullOrEmpty(selfDeviceId)
        && !string.IsNullOrEmpty(d.DeviceId)
        && string.Equals(d.DeviceId, selfDeviceId, StringComparison.OrdinalIgnoreCase);
}
