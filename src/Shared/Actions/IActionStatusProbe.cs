namespace IntuneDeviceActions.Actions;

/// <summary>
/// Per-capability probe that interrogates the back-end (typically Microsoft
/// Graph) for the current execution state of an in-flight action. Implemented
/// once per <see cref="ActionType"/>; resolved via DI as a registry on the
/// generic <c>ActionStatusTracker</c> so the tracker stays decoupled from
/// any specific capability (no compile-time reference to GraphWipeService etc).
/// </summary>
/// <remarks>
/// Lifecycle: registered as a singleton by each capability project (e.g.
/// <c>WipeActionStatusProbe</c> in <c>Capabilities.Wipe</c>). The
/// <c>ActionStatusTracker</c> consumes <see cref="IEnumerable{IActionStatusProbe}"/>
/// and indexes by <see cref="ActionType"/> (case-insensitive).
/// </remarks>
public interface IActionStatusProbe
{
    /// <summary>
    /// Lower-case action type discriminator (e.g. <c>"wipe"</c>) — matches the
    /// <c>ActionRequestMessage.ActionType</c> persisted on the status row.
    /// </summary>
    string ActionType { get; }

    /// <summary>
    /// Probes the back-end for the current state of the action on the given
    /// managed device. Implementations should:
    /// <list type="bullet">
    ///   <item>Map back-end-specific terminal states to lowercase strings
    ///         (e.g. <c>done</c>, <c>failed</c>, <c>canceled</c>, <c>notsupported</c>,
    ///         <c>removedfromintune</c>) so the tracker's terminal-state set
    ///         matches uniformly across capabilities.</item>
    ///   <item>Return <c>State=pending</c> (or similar non-terminal) when the
    ///         back-end has not yet reported anything for the action.</item>
    ///   <item>Throw on transient errors so the tracker can record a poll
    ///         error and retry next tick.</item>
    /// </list>
    /// </summary>
    Task<ActionProbeSnapshot> ProbeAsync(string managedDeviceId, CancellationToken ct);
}
