using System.Text.Json;
using FluentAssertions;
using IntuneDeviceActions.Models;
using Xunit;

namespace IntuneDeviceActions.Shared.Tests.Models;

public sealed class CallerUpnPropagationTests
{
    [Fact]
    public void ActionRequest_CallerUpn_deserializes_from_json()
    {
        var json = """{"actionType":"wipe","deviceName":"PC1","entraDeviceId":"e1","intuneDeviceId":"i1","callerUpn":"alice@contoso.com"}""";
        var req = JsonSerializer.Deserialize<ActionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        req!.CallerUpn.Should().Be("alice@contoso.com");
    }

    [Fact]
    public void ActionRequest_CallerUpn_is_optional_and_defaults_to_null()
    {
        var json = """{"actionType":"wipe","deviceName":"PC1","entraDeviceId":"e1","intuneDeviceId":"i1"}""";
        var req = JsonSerializer.Deserialize<ActionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        req!.CallerUpn.Should().BeNull();
    }

    [Fact]
    public void ActionRequestMessage_CallerUpn_serializes_as_camelCase()
    {
        var msg = new ActionRequestMessage
        {
            ActionType = "wipe",
            DeviceName = "PC1",
            EntraDeviceId = "e1",
            IntuneDeviceId = "i1",
            CorrelationId = "c1",
            CallerUpn = "bob@contoso.com",
        };

        var json = JsonSerializer.Serialize(msg);
        json.Should().Contain("\"callerUpn\":\"bob@contoso.com\"");
    }

    [Fact]
    public void ActionRequestMessage_CallerUpn_is_in_ReservedExtrasKeys()
    {
        ActionRequestMessage.ReservedExtrasKeys.Should().Contain("callerUpn");
    }

    [Fact]
    public void CallerUpn_in_extras_is_stripped_by_SanitizeExtras()
    {
        var extras = new Dictionary<string, JsonElement>
        {
            ["callerUpn"] = JsonSerializer.SerializeToElement("spoofed@evil.com"),
            ["autopilot"] = JsonSerializer.SerializeToElement(new { groupTag = "pilot" }),
        };

        var dropped = new List<string>();
        var sanitized = ActionRequestMessage.SanitizeExtras(extras, dropped);

        dropped.Should().Contain("callerUpn");
        sanitized.Should().NotContainKey("callerUpn");
        sanitized.Should().ContainKey("autopilot");
    }

    [Fact]
    public void ActionDispatchMessage_CallerUpn_roundtrips_via_json()
    {
        var envelope = new IntuneDeviceActions.Actions.ActionDispatchMessage
        {
            ActionType = "wipe",
            CorrelationId = "c1",
            DeviceName = "PC1",
            EntraDeviceId = "e1",
            IntuneDeviceId = "i1",
            CallerUpn = "carol@contoso.com",
            Payload = JsonSerializer.SerializeToElement(new { test = true }),
        };

        var json = JsonSerializer.Serialize(envelope);
        var deserialized = JsonSerializer.Deserialize<IntuneDeviceActions.Actions.ActionDispatchMessage>(json);

        deserialized!.CallerUpn.Should().Be("carol@contoso.com");
    }

    [Fact]
    public void ActionDispatchMessage_CallerUpn_null_when_not_provided()
    {
        var json = """{"schemaVersion":"1","actionType":"wipe","correlationId":"c1","deviceName":"PC1","entraDeviceId":"e1","intuneDeviceId":"i1"}""";
        var deserialized = JsonSerializer.Deserialize<IntuneDeviceActions.Actions.ActionDispatchMessage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        deserialized!.CallerUpn.Should().BeNull();
    }
}
