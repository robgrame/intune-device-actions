namespace IntuneDeviceActions.Capabilities.Wipe.Services;

/// <summary>
/// Controls how device-group and user-group membership gates interact.
/// Configured via <c>Wipe:GatingMode</c> (case-insensitive).
/// </summary>
public enum GatingMode
{
    /// <summary>
    /// Only the device must be a member of <c>Wipe:AllowedGroupId</c>.
    /// User group is not checked. Default (backward-compatible).
    /// </summary>
    DeviceOnly,

    /// <summary>
    /// Only the caller must be a member of <c>Wipe:AllowedUserGroupId</c>.
    /// Device group is not checked.
    /// </summary>
    UserOnly,

    /// <summary>
    /// Both gates must pass: the device must be in the device group AND
    /// the caller must be in the user group. Most restrictive.
    /// </summary>
    Both,

    /// <summary>
    /// At least one gate must pass: the device is in the device group OR
    /// the caller is in the user group. Least restrictive.
    /// </summary>
    Either,
}
