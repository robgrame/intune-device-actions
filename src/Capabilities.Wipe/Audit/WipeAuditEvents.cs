namespace IntuneDeviceActions.Capabilities.Wipe.Audit;

/// <summary>
/// Wipe-specific event names and property keys emitted to Application Insights
/// customEvents. Live alongside the wipe capability so a future, separate
/// capability (lock, retire, restartNow, …) keeps its own namespace and the
/// Shared <c>AuditEvents</c> doesn't accumulate per-capability bloat.
///
/// KQL convention: <c>customEvents | where name startswith "wipe."</c> covers
/// every wipe-specific row; combine with <c>name startswith "action."</c> for
/// the full pipeline picture.
/// </summary>
public static class WipeAuditEvents
{
    // Graph wipe call outcomes
    public const string WipeIssued          = "wipe.graph.issued";
    public const string WipeFailedPermanent = "wipe.graph.failed-permanent";
    public const string WipeTransientError  = "wipe.graph.transient-error";

    // Post-wipe fallback nudges (best-effort: syncDevice + rebootNow to push the
    // managed-device to pick up the pending wipe even if it didn't kick in
    // immediately). Failures here do NOT reverse the successful wipe.
    public const string SyncFallbackIssued     = "wipe.graph.sync-fallback.issued";
    public const string SyncFallbackRetrying   = "wipe.graph.sync-fallback.retrying";
    public const string SyncFallbackFailed     = "wipe.graph.sync-fallback.failed";
    public const string SyncFallbackExhausted  = "wipe.graph.sync-fallback.exhausted";
    public const string RebootFallbackIssued   = "wipe.graph.reboot-fallback.issued";
    public const string RebootFallbackRetrying = "wipe.graph.reboot-fallback.retrying";
    public const string RebootFallbackFailed   = "wipe.graph.reboot-fallback.failed";
    public const string RebootFallbackExhausted= "wipe.graph.reboot-fallback.exhausted";

    // Wipe-runner Function App (consumer of the per-capability wipe-action queue)
    public const string WipeActionConsumed           = "wipe.action.consumed";
    public const string WipeActionInvalidEnvelope    = "wipe.action.invalid-envelope";
    public const string WipeActionCompleted          = "wipe.action.completed";
    public const string WipeActionRunnerFailed       = "wipe.action.runner-failed";

    /// <summary>
    /// Wipe-specific property keys. Combine with <c>AuditEvents.Prop.*</c>
    /// (the action-agnostic ones).
    /// </summary>
    public static class Prop
    {
        public const string KeepEnrollmentData = "keepEnrollmentData";
        public const string KeepUserData       = "keepUserData";
    }
}
