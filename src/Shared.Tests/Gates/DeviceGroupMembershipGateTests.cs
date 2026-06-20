using FluentAssertions;
using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntuneDeviceActions.Shared.Tests.Gates;

public sealed class DeviceGroupMembershipGateTests
{
    private sealed class StubMembershipService : IGraphGroupMembershipService
    {
        public bool DeviceResult { get; set; }

        public Task<bool> IsDeviceInGroupAsync(string deviceObjectId, string groupId, CancellationToken ct) =>
            Task.FromResult(DeviceResult);

        public Task<bool> IsUserInGroupAsync(string? userUpn, string groupId, CancellationToken ct) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task CheckAsync_passes_when_no_allowed_device_group_is_configured()
    {
        var service = new StubMembershipService { DeviceResult = false };
        var gate = new DeviceGroupMembershipGate(service, NullLogger<DeviceGroupMembershipGate>.Instance);

        var result = await gate.CheckAsync(new ActionGateContext
        {
            EntraDeviceId = Guid.NewGuid(),
            DeviceObjectId = "device-object",
            DeviceName = "PC1",
            ActionType = "wipe",
            CorrelationId = "c1",
            AllowedDeviceGroupId = null,
            GatingMode = "DeviceOnly",
        }, CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Pass);
    }

    [Fact]
    public async Task CheckAsync_denies_when_device_is_not_member_of_allowed_group()
    {
        var service = new StubMembershipService { DeviceResult = false };
        var gate = new DeviceGroupMembershipGate(service, NullLogger<DeviceGroupMembershipGate>.Instance);

        var result = await gate.CheckAsync(new ActionGateContext
        {
            EntraDeviceId = Guid.NewGuid(),
            DeviceObjectId = "device-object",
            DeviceName = "PC1",
            ActionType = "wipe",
            CorrelationId = "c1",
            AllowedDeviceGroupId = "group-1",
            GatingMode = "DeviceOnly",
        }, CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Denied);
        result.DenialReason.Should().Be("denied:device-not-in-allowed-group");
    }
}
