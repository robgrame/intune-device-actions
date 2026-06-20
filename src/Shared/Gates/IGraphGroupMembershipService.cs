namespace IntuneDeviceActions.Gates;

/// <summary>
/// Generic service for checking group membership in Entra.
/// Used by device and user group membership gates.
/// </summary>
public interface IGraphGroupMembershipService
{
    /// <summary>
    /// Check if a device (by directory object id) is a member of a specific group.
    /// </summary>
    Task<bool> IsDeviceInGroupAsync(string deviceObjectId, string groupId, CancellationToken ct);

    /// <summary>
    /// Check if a user (by UPN) is a member of a specific group.
    /// </summary>
    Task<bool> IsUserInGroupAsync(string? userUpn, string groupId, CancellationToken ct);
}
