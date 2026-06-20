using FluentAssertions;
using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntuneDeviceActions.Shared.Tests.Gates;

public sealed class UserGroupMembershipGateTests
{
    private sealed class StubMembershipService : IGraphGroupMembershipService
    {
        public bool UserResult { get; set; }

        public Task<bool> IsDeviceInGroupAsync(string deviceObjectId, string groupId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> IsUserInGroupAsync(string? userUpn, string groupId, CancellationToken ct) =>
            Task.FromResult(UserResult);
    }

    [Fact]
    public async Task CheckAsync_passes_when_no_allowed_user_group_is_configured()
    {
        var service = new StubMembershipService { UserResult = false };
        var gate = new UserGroupMembershipGate(service, NullLogger<UserGroupMembershipGate>.Instance);

        var result = await gate.CheckAsync(new ActionGateContext
        {
            EntraDeviceId = Guid.NewGuid(),
            DeviceObjectId = "device-object",
            DeviceName = "PC1",
            ActionType = "wipe",
            CorrelationId = "c1",
            AllowedUserGroupId = null,
            CallerUpn = "user@contoso.com",
            GatingMode = "UserOnly",
        }, CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Pass);
    }

    [Fact]
    public async Task CheckAsync_denies_when_user_is_not_member_of_allowed_group()
    {
        var service = new StubMembershipService { UserResult = false };
        var gate = new UserGroupMembershipGate(service, NullLogger<UserGroupMembershipGate>.Instance);

        var result = await gate.CheckAsync(new ActionGateContext
        {
            EntraDeviceId = Guid.NewGuid(),
            DeviceObjectId = "device-object",
            DeviceName = "PC1",
            ActionType = "wipe",
            CorrelationId = "c1",
            AllowedUserGroupId = "group-1",
            CallerUpn = "user@contoso.com",
            GatingMode = "UserOnly",
        }, CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Denied);
        result.DenialReason.Should().Be("denied:user-not-in-allowed-group");
    }
}
