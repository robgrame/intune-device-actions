using FluentAssertions;
using IntuneDeviceActions.Capabilities.Rename.Services;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Rename.Tests.Services;

/// <summary>
/// Unit tests for the pure pre-rename cleanup planner: HWID extraction plus the
/// keep/delete classification for the AD-name shadows and the HWID duplicates.
/// </summary>
public sealed class RenameCleanupPlannerTests
{
    private const string Self = "11111111-1111-1111-1111-111111111111";
    private const string Hwid = "[HWID]:h:6896239577502718";

    private static EntraDeviceRecord Dev(
        string objectId, string deviceId, string displayName, string? trustType,
        params string[] physicalIds) =>
        new(objectId, deviceId, displayName, trustType, physicalIds, AccountEnabled: true);

    // ── ExtractHwid ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractHwid_returns_full_token_when_present()
    {
        var ids = new[] { "[ZTDID]:h:abc", "[HWID]:h:6896239577502718", "[GID]:g:1" };
        RenameCleanupPlanner.ExtractHwid(ids).Should().Be("[HWID]:h:6896239577502718");
    }

    [Fact]
    public void ExtractHwid_is_case_insensitive_on_prefix()
    {
        var ids = new[] { "[hwid]:h:XYZ" };
        RenameCleanupPlanner.ExtractHwid(ids).Should().Be("[hwid]:h:XYZ");
    }

    [Fact]
    public void ExtractHwid_returns_null_when_absent()
    {
        RenameCleanupPlanner.ExtractHwid(new[] { "[ZTDID]:h:abc" }).Should().BeNull();
    }

    [Fact]
    public void ExtractHwid_returns_null_for_null_or_empty()
    {
        RenameCleanupPlanner.ExtractHwid(null).Should().BeNull();
        RenameCleanupPlanner.ExtractHwid(Array.Empty<string>()).Should().BeNull();
    }

    // ── PlanAdNameDeletions ─────────────────────────────────────────────────

    [Fact]
    public void PlanAdName_deletes_serverAd_matches_excluding_self()
    {
        var trustTypes = RenameCleanupPlanner.ParseTrustTypes(null); // default ServerAd
        var matches = new[]
        {
            Dev("o-hybrid", "d-hybrid", "TARGET", RenameCleanupPlanner.TrustHybrid),
            Dev("o-self",   Self,       "TARGET", RenameCleanupPlanner.TrustHybrid), // self — keep
            Dev("o-joined", "d-joined", "TARGET", RenameCleanupPlanner.TrustEntraJoined), // not an AD type
        };

        var del = RenameCleanupPlanner.PlanAdNameDeletions(matches, Self, trustTypes);

        del.Select(d => d.ObjectId).Should().ContainSingle().Which.Should().Be("o-hybrid");
    }

    [Fact]
    public void PlanAdName_respects_configured_trust_types()
    {
        var trustTypes = RenameCleanupPlanner.ParseTrustTypes("ServerAd,Workplace");
        var matches = new[]
        {
            Dev("o-hybrid",   "d-h", "TARGET", RenameCleanupPlanner.TrustHybrid),
            Dev("o-reg",      "d-r", "TARGET", "Workplace"),
            Dev("o-joined",   "d-j", "TARGET", RenameCleanupPlanner.TrustEntraJoined),
        };

        var del = RenameCleanupPlanner.PlanAdNameDeletions(matches, Self, trustTypes);

        del.Select(d => d.ObjectId).Should().BeEquivalentTo(new[] { "o-hybrid", "o-reg" });
    }

    [Fact]
    public void PlanAdName_returns_empty_when_no_matches()
    {
        var del = RenameCleanupPlanner.PlanAdNameDeletions(
            Array.Empty<EntraDeviceRecord>(), Self, RenameCleanupPlanner.ParseTrustTypes(null));
        del.Should().BeEmpty();
    }

    // ── PlanHwidDeletions ───────────────────────────────────────────────────

    [Fact]
    public void PlanHwid_keeps_entraJoined_and_self_deletes_the_rest()
    {
        var shared = new[]
        {
            Dev("o-joined",  "d-joined", "PC-JOINED", RenameCleanupPlanner.TrustEntraJoined, Hwid),
            Dev("o-self",    Self,       "PC-SELF",   RenameCleanupPlanner.TrustHybrid,      Hwid),
            Dev("o-dup1",    "d-dup1",   "PC-DUP1",   RenameCleanupPlanner.TrustHybrid,      Hwid),
            Dev("o-dup2",    "d-dup2",   "PC-DUP2",   "Workplace",                           Hwid),
        };

        var plan = RenameCleanupPlanner.PlanHwidDeletions(shared, Self);

        plan.Keep.Select(d => d.ObjectId).Should().BeEquivalentTo(new[] { "o-joined", "o-self" });
        plan.Delete.Select(d => d.ObjectId).Should().BeEquivalentTo(new[] { "o-dup1", "o-dup2" });
        plan.HasEntraJoinedKeeper.Should().BeTrue();
    }

    [Fact]
    public void PlanHwid_keeps_self_even_without_entraJoined_keeper()
    {
        var shared = new[]
        {
            Dev("o-self", Self,     "PC-SELF", RenameCleanupPlanner.TrustHybrid, Hwid),
            Dev("o-dup",  "d-dup",  "PC-DUP",  RenameCleanupPlanner.TrustHybrid, Hwid),
        };

        var plan = RenameCleanupPlanner.PlanHwidDeletions(shared, Self);

        plan.Keep.Select(d => d.ObjectId).Should().ContainSingle().Which.Should().Be("o-self");
        plan.Delete.Select(d => d.ObjectId).Should().ContainSingle().Which.Should().Be("o-dup");
        plan.HasEntraJoinedKeeper.Should().BeFalse();
    }

    [Fact]
    public void PlanHwid_self_match_is_case_insensitive()
    {
        var shared = new[]
        {
            Dev("o-self", Self.ToUpperInvariant(), "PC-SELF", RenameCleanupPlanner.TrustHybrid, Hwid),
        };

        var plan = RenameCleanupPlanner.PlanHwidDeletions(shared, Self.ToLowerInvariant());

        plan.Keep.Should().ContainSingle();
        plan.Delete.Should().BeEmpty();
    }

    // ── ParseTrustTypes ─────────────────────────────────────────────────────

    [Fact]
    public void ParseTrustTypes_defaults_to_serverAd()
    {
        var set = RenameCleanupPlanner.ParseTrustTypes(null);
        set.Should().ContainSingle().Which.Should().Be(RenameCleanupPlanner.TrustHybrid);
    }

    [Fact]
    public void ParseTrustTypes_parses_csv_and_trims()
    {
        var set = RenameCleanupPlanner.ParseTrustTypes(" ServerAd , Workplace ");
        set.Should().BeEquivalentTo(new[] { "ServerAd", "Workplace" });
    }

    [Fact]
    public void ParseTrustTypes_is_case_insensitive_membership()
    {
        var set = RenameCleanupPlanner.ParseTrustTypes("ServerAd");
        set.Contains("serverad").Should().BeTrue();
    }
}
