using System.Text.Json.Serialization;

namespace IntuneDeviceActions.Capabilities.Rename.Models;

/// <summary>
/// Capability-specific payload carried inside the opaque <c>Extras</c> bag of
/// <c>ActionRequest</c>/<c>ActionRequestMessage</c>. The Shared HTTP intake
/// passes this through unchanged; the rename runner deserializes the named
/// <c>rename</c> property of <c>Extras</c> into this shape.
///
/// JSON shape on the wire (inside <c>ActionRequest.Extras</c>):
/// <code>
/// {
///   "actionType": "device-rename",
///   "deviceName": "WS-CONTOSO-001",
///   "entraDeviceId": "...",
///   "intuneDeviceId": "...",
///   "rename": {
///     "serialNumber": "PF3X9ABC",
///     "newName":      "WS-CONTOSO-101"
///   }
/// }
/// </code>
/// Both fields are required — the runner emits
/// <c>rename.denied.missing-serial</c> / <c>rename.denied.missing-new-name</c>
/// and records a terminal status when either is missing or whitespace.
/// </summary>
public sealed class RenameRequestExtras
{
    /// <summary>Hardware serial number passed to the customer REST endpoint as the device key.</summary>
    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    /// <summary>Desired new device name. Validation/normalization is the customer endpoint's responsibility.</summary>
    [JsonPropertyName("newName")]
    public string? NewName { get; set; }
}
