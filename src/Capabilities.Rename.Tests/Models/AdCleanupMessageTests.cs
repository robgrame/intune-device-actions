using System.Text.Json;
using FluentAssertions;
using IntuneDeviceActions.Capabilities.Rename.Models;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Rename.Tests.Models;

/// <summary>
/// Contract tests for <see cref="AdCleanupMessage"/> — the capability-owned
/// payload published to the <c>ad-object-cleanup</c> Service Bus queue and
/// consumed by the on-prem hybrid worker. The property names on the wire are
/// part of the contract with the PowerShell worker, so they are pinned here.
/// </summary>
public sealed class AdCleanupMessageTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Constants_are_stable()
    {
        AdCleanupMessage.MessageType.Should().Be("ad-object-cleanup");
        AdCleanupMessage.CurrentSchemaVersion.Should().Be("1");
    }

    [Fact]
    public void New_message_defaults_schema_version_and_timestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var msg = new AdCleanupMessage { TargetName = "PC-NEW" };

        msg.SchemaVersion.Should().Be(AdCleanupMessage.CurrentSchemaVersion);
        msg.RequestedUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Serializes_with_camelCase_wire_names()
    {
        var msg = new AdCleanupMessage
        {
            CorrelationId    = "corr-1",
            TargetName       = "PC-NEW",
            SourceDeviceName = "PC-OLD",
            EntraDeviceId    = "11111111-1111-1111-1111-111111111111",
            IntuneDeviceId   = "22222222-2222-2222-2222-222222222222",
            SerialNumber     = "PF3X9ABC",
        };

        var json = JsonSerializer.Serialize(msg, Web);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetString().Should().Be("1");
        root.GetProperty("correlationId").GetString().Should().Be("corr-1");
        root.GetProperty("targetName").GetString().Should().Be("PC-NEW");
        root.GetProperty("sourceDeviceName").GetString().Should().Be("PC-OLD");
        root.GetProperty("entraDeviceId").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        root.GetProperty("intuneDeviceId").GetString().Should().Be("22222222-2222-2222-2222-222222222222");
        root.GetProperty("serialNumber").GetString().Should().Be("PF3X9ABC");
        root.TryGetProperty("requestedUtc", out _).Should().BeTrue();
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var msg = new AdCleanupMessage
        {
            CorrelationId  = "corr-2",
            TargetName     = "PC-42",
            IntuneDeviceId = "33333333-3333-3333-3333-333333333333",
        };

        var json = JsonSerializer.Serialize(msg, Web);
        var back = JsonSerializer.Deserialize<AdCleanupMessage>(json, Web)!;

        back.CorrelationId.Should().Be("corr-2");
        back.TargetName.Should().Be("PC-42");
        back.IntuneDeviceId.Should().Be("33333333-3333-3333-3333-333333333333");
        back.SchemaVersion.Should().Be("1");
    }
}
