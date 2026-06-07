using System.Text.Json;
using IntuneDeviceActions.Capabilities.Rename.Models;
using IntuneDeviceActions.Models;

namespace IntuneDeviceActions.Capabilities.Rename.Runners;

/// <summary>
/// Pulls the capability-owned <c>rename</c> JSON element out of the
/// action-agnostic <see cref="ActionRequestMessage.Extras"/> bag and binds it
/// to <see cref="RenameRequestExtras"/>. Mirrors <c>AutopilotPayloadExtractor</c>.
/// Returns <c>null</c> when the key is absent, JSON-null, or fails to bind —
/// the runner treats all three as "missing payload" and audits a permanent denial.
/// </summary>
internal static class RenamePayloadExtractor
{
    public const string ExtrasKey = "rename";

    public static RenameRequestExtras? TryRead(ActionRequestMessage msg)
    {
        if (msg.Extras is null) return null;
        if (!msg.Extras.TryGetValue(ExtrasKey, out var element)) return null;
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        try
        {
            return element.Deserialize<RenameRequestExtras>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
