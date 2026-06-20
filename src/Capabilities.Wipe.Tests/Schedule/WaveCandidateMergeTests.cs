using FluentAssertions;
using IntuneDeviceActions.Capabilities.Wipe.Schedule;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Wipe.Tests.Schedule;

/// <summary>
/// Unit tests for the pure union/dedup + best-candidate selection that backs the
/// "group membership is a sufficient condition" wave-gating semantics. These
/// cover the host-agnostic core of <see cref="WipeScheduleStore"/> without
/// touching Azure Tables or Graph.
/// </summary>
public class WaveCandidateMergeTests
{
    private static WipeScheduleWave Wave(string id, DateTimeOffset when, string name = "w")
        => new()
        {
            RowKey = id,
            Name = name,
            ScheduledAtUtc = when,
            Status = IntuneDeviceActions.Schedule.WaveStatus.Scheduled,
        };

    [Fact]
    public void Merge_WithEmptyGroup_ReturnsIndividualOnly()
    {
        var ind = new[] { Wave("a", DateTimeOffset.UtcNow.AddDays(1)) };

        var merged = WipeScheduleStore.MergeWaveCandidates(ind, Array.Empty<WipeScheduleWave>());

        merged.Should().ContainSingle().Which.RowKey.Should().Be("a");
    }

    [Fact]
    public void Merge_WithEmptyIndividual_ReturnsGroupOnly()
    {
        var grp = new[] { Wave("g", DateTimeOffset.UtcNow.AddDays(1)) };

        var merged = WipeScheduleStore.MergeWaveCandidates(Array.Empty<WipeScheduleWave>(), grp);

        merged.Should().ContainSingle().Which.RowKey.Should().Be("g");
    }

    [Fact]
    public void Merge_UnionsDistinctWaves()
    {
        var ind = new[] { Wave("a", DateTimeOffset.UtcNow.AddDays(1)) };
        var grp = new[] { Wave("b", DateTimeOffset.UtcNow.AddDays(2)) };

        var merged = WipeScheduleStore.MergeWaveCandidates(ind, grp);

        merged.Select(w => w.RowKey).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void Merge_DeduplicatesWaveMatchedByBothPaths()
    {
        var when = DateTimeOffset.UtcNow.AddDays(1);
        var ind = new[] { Wave("dup", when) };
        var grp = new[] { Wave("DUP", when) }; // same id, different casing

        var merged = WipeScheduleStore.MergeWaveCandidates(ind, grp);

        merged.Should().ContainSingle("the same wave matched by both individual and group paths must collapse");
    }

    [Fact]
    public void PickBestCandidate_OnMergedUnion_PrefersEarliestFutureWave()
    {
        var soon = DateTimeOffset.UtcNow.AddHours(2);
        var later = DateTimeOffset.UtcNow.AddDays(3);
        var ind = new[] { Wave("later", later) };
        var grp = new[] { Wave("soon", soon) };

        var snap = WipeScheduleStore.PickBestCandidate(
            WipeScheduleStore.MergeWaveCandidates(ind, grp));

        snap.Should().NotBeNull();
        snap!.WaveId.Should().Be("soon");
        snap.IsImmediate.Should().BeFalse();
    }

    [Fact]
    public void PickBestCandidate_OnGroupOnlyPastWave_IsImmediate()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);
        var grp = new[] { Wave("fired", past) };

        var snap = WipeScheduleStore.PickBestCandidate(
            WipeScheduleStore.MergeWaveCandidates(Array.Empty<WipeScheduleWave>(), grp));

        snap.Should().NotBeNull();
        snap!.WaveId.Should().Be("fired");
        snap.IsImmediate.Should().BeTrue("a group-only device whose wave already fired must be allowed (sufficient condition)");
    }
}
