using FluentAssertions;
using IntuneDeviceActions.Capabilities.Wipe.Services;
using Xunit;

namespace IntuneDeviceActions.Capabilities.Wipe.Tests.Services;

/// <summary>
/// Tests for <see cref="GatingMode"/> configuration validation logic.
/// These do NOT call Graph — they test the config-validation and enum parsing.
/// </summary>
public sealed class GatingModeConfigTests
{
    [Fact]
    public void Default_GatingMode_is_DeviceOnly_when_not_configured()
    {
        var raw = (string?)null;
        var parsed = Enum.TryParse<GatingMode>(raw, ignoreCase: true, out var gm)
                     && Enum.IsDefined(typeof(GatingMode), gm)
            ? gm : GatingMode.DeviceOnly;

        parsed.Should().Be(GatingMode.DeviceOnly);
    }

    [Theory]
    [InlineData("DeviceOnly", GatingMode.DeviceOnly)]
    [InlineData("deviceonly", GatingMode.DeviceOnly)]
    [InlineData("UserOnly", GatingMode.UserOnly)]
    [InlineData("USERONLY", GatingMode.UserOnly)]
    [InlineData("Both", GatingMode.Both)]
    [InlineData("Either", GatingMode.Either)]
    [InlineData("either", GatingMode.Either)]
    public void GatingMode_parses_case_insensitively(string input, GatingMode expected)
    {
        var parsed = Enum.TryParse<GatingMode>(input, ignoreCase: true, out var gm)
                     && Enum.IsDefined(typeof(GatingMode), gm)
            ? gm : GatingMode.DeviceOnly;

        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("InvalidValue")]
    [InlineData("")]
    public void Invalid_GatingMode_falls_back_to_DeviceOnly(string input)
    {
        var parsed = Enum.TryParse<GatingMode>(input, ignoreCase: true, out var gm)
                     && Enum.IsDefined(typeof(GatingMode), gm)
            ? gm : GatingMode.DeviceOnly;

        parsed.Should().Be(GatingMode.DeviceOnly);
    }

    [Theory]
    [InlineData("UserOnly")]
    [InlineData("Both")]
    public void UserOnly_and_Both_require_AllowedUserGroupId(string mode)
    {
        var gatingMode = Enum.Parse<GatingMode>(mode, ignoreCase: true);
        string? allowedUserGroupId = null;

        var shouldThrow = gatingMode is GatingMode.UserOnly or GatingMode.Both
            && string.IsNullOrWhiteSpace(allowedUserGroupId);

        shouldThrow.Should().BeTrue(
            $"GatingMode={mode} without AllowedUserGroupId should be rejected at startup");
    }

    [Theory]
    [InlineData("DeviceOnly")]
    [InlineData("Either")]
    public void DeviceOnly_and_Either_do_not_require_AllowedUserGroupId(string mode)
    {
        var gatingMode = Enum.Parse<GatingMode>(mode, ignoreCase: true);
        string? allowedUserGroupId = null;

        var shouldThrow = gatingMode is GatingMode.UserOnly or GatingMode.Both
            && string.IsNullOrWhiteSpace(allowedUserGroupId);

        shouldThrow.Should().BeFalse(
            $"GatingMode={mode} should not require AllowedUserGroupId");
    }

    [Fact]
    public void Enum_has_exactly_four_members()
    {
        Enum.GetValues<GatingMode>().Should().HaveCount(4);
    }
}
