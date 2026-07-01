using System.Text.Json;
using FluentAssertions;
using IntuneDeviceActions.Capabilities.Rename.Runners;
using IntuneDeviceActions.Models;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Rename.Tests.Runners;

/// <summary>
/// Unit tests for the helper that pulls the capability-owned <c>rename</c>
/// payload out of the action-agnostic <see cref="ActionRequestMessage.Extras"/>
/// bag. Contract: returns <c>null</c> for missing / empty / null / malformed
/// input; returns the deserialized payload otherwise. The runner treats every
/// null outcome as a permanent denial (missing serial).
/// </summary>
public sealed class RenamePayloadExtractorTests
{
    private static ActionRequestMessage NewMessage(string? extrasJson = null)
    {
        var msg = new ActionRequestMessage
        {
            ActionType     = "device-rename",
            DeviceName     = "PC-01",
            EntraDeviceId  = "11111111-1111-1111-1111-111111111111",
            IntuneDeviceId = "22222222-2222-2222-2222-222222222222",
            CorrelationId  = "c-1",
        };
        if (extrasJson is not null)
        {
            var roundTripped = JsonSerializer.Deserialize<ActionRequestMessage>(extrasJson)!;
            msg.Extras = roundTripped.Extras;
        }
        return msg;
    }

    [Fact]
    public void Returns_null_when_Extras_is_null()
    {
        RenamePayloadExtractor.TryRead(NewMessage()).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_rename_key_is_absent()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","otherCapability":{"x":1}}""");
        RenamePayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_rename_value_is_explicitly_null()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":null}""");
        RenamePayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_payload_when_rename_key_present_with_serial()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":{"serialNumber":"PF3X9ABC"}}""");

        var p = RenamePayloadExtractor.TryRead(msg);

        p.Should().NotBeNull();
        p!.SerialNumber.Should().Be("PF3X9ABC");
    }

    [Fact]
    public void Returns_payload_with_null_serial_when_value_is_json_null()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":{"serialNumber":null}}""");

        var p = RenamePayloadExtractor.TryRead(msg);

        p.Should().NotBeNull();
        p!.SerialNumber.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_serialNumber_has_wrong_json_type()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":{"serialNumber":123}}""");

        RenamePayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_rename_value_is_a_string()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":"bad"}""");

        RenamePayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_rename_value_is_an_array()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","rename":[]}""");

        RenamePayloadExtractor.TryRead(msg).Should().BeNull();
    }
}
