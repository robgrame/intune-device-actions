using System.Text.Json;
using FluentAssertions;
using IntuneDeviceActions.Capabilities.Autopilot.Models;
using IntuneDeviceActions.Capabilities.Autopilot.Runners;
using IntuneDeviceActions.Models;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Autopilot.Tests.Runners;

/// <summary>
/// Unit tests for the standalone extraction helper that pulls the
/// capability-owned <c>autopilot</c> payload out of the action-agnostic
/// <see cref="ActionRequestMessage.Extras"/> bag. Behavioural contract:
/// returns <c>null</c> for missing / empty / null / malformed input;
/// returns the deserialized payload otherwise. The runner treats every null
/// outcome as a permanent denial (missing hardware hash).
/// </summary>
public sealed class AutopilotPayloadExtractorTests
{
    private static ActionRequestMessage NewMessage(string? extrasJson = null)
    {
        var msg = new ActionRequestMessage
        {
            ActionType     = "autopilot-register",
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
        AutopilotPayloadExtractor.TryRead(NewMessage()).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_autopilot_key_is_absent()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","otherCapability":{"x":1}}""");
        AutopilotPayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_autopilot_value_is_explicitly_null()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","autopilot":null}""");
        AutopilotPayloadExtractor.TryRead(msg).Should().BeNull();
    }

    [Fact]
    public void Returns_payload_when_autopilot_key_present_with_full_object()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","autopilot":{"hardwareHash":"H","serialNumber":"S","groupTag":"G"}}""");

        var p = AutopilotPayloadExtractor.TryRead(msg);

        p.Should().NotBeNull();
        p!.HardwareHash.Should().Be("H");
        p.SerialNumber.Should().Be("S");
        p.GroupTag.Should().Be("G");
    }

    [Fact]
    public void Returns_payload_with_only_hardware_hash_when_other_fields_omitted()
    {
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","autopilot":{"hardwareHash":"only-hash"}}""");

        var p = AutopilotPayloadExtractor.TryRead(msg)!;
        p.HardwareHash.Should().Be("only-hash");
        p.SerialNumber.Should().BeNull();
    }

    [Fact]
    public void Returns_payload_with_null_hardware_hash_when_value_is_wrong_type()
    {
        // The Web JSON serializer used by System.Text.Json silently binds a
        // numeric JSON value to a string property as null (web defaults) —
        // bind the value rather than throw. The runner subsequently sees
        // HardwareHash=null and treats it as a missing-hash denial. We assert
        // that this binding is non-throwing.
        var msg = NewMessage("""{"correlationId":"c","deviceName":"d","entraDeviceId":"e","intuneDeviceId":"i","autopilot":{"hardwareHash":null,"serialNumber":"S"}}""");

        var p = AutopilotPayloadExtractor.TryRead(msg);

        p.Should().NotBeNull();
        p!.HardwareHash.Should().BeNull();
        p.SerialNumber.Should().Be("S");
    }
}
