namespace IntuneDeviceActions.Capabilities.Rename.Audit;

/// <summary>
/// Rename-specific event names emitted to Application Insights customEvents.
/// Mirrors the convention used by other capabilities (Wipe / Autopilot /
/// BitLocker) but with a <c>rest</c> verb segment (instead of <c>graph</c>)
/// since rename targets a customer-internal HTTP endpoint, not Microsoft
/// Graph.
///
/// KQL convention: <c>customEvents | where name startswith "rename."</c>
/// covers every rename-specific row; combine with
/// <c>name startswith "action."</c> for the full pipeline picture.
/// </summary>
public static class RenameAuditEvents
{
    // Customer REST call outcomes
    public const string RestIssued          = "rename.rest.issued";
    public const string RestFailedPermanent = "rename.rest.failed-permanent";
    public const string RestTransientError  = "rename.rest.transient-error";

    // Rename Function App (consumer of the per-capability rename-action queue)
    public const string ActionConsumed        = "rename.action.consumed";
    public const string ActionInvalidEnvelope = "rename.action.invalid-envelope";
    public const string ActionCompleted       = "rename.action.completed";
    public const string ActionRunnerFailed    = "rename.action.runner-failed";

    // Validation
    public const string MissingSerial         = "rename.denied.missing-serial";
    public const string MissingNewName        = "rename.denied.missing-new-name";
}
