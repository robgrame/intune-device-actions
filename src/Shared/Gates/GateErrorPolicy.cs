using Microsoft.Extensions.Configuration;

namespace IntuneDeviceActions.Gates;

/// <summary>
/// Controls how the gating pipeline reacts to an <em>unexpected</em> (non-transient)
/// error while evaluating a gate. Transient errors are always retried regardless of
/// this policy.
/// </summary>
public enum GateErrorPolicy
{
    /// <summary>
    /// Deny the action when a gate errors unexpectedly. Safe default for destructive
    /// actions (e.g. wipe): on uncertainty we refuse rather than risk an unintended action.
    /// </summary>
    FailClosed,

    /// <summary>
    /// Allow the action to proceed when a gate errors unexpectedly. Favours availability
    /// over safety; only appropriate for non-destructive capabilities that have other
    /// defense-in-depth (client gate, idempotency ledger).
    /// </summary>
    FailOpen,
}

/// <summary>
/// Reads the gate error policy from configuration so the fail-open/fail-closed decision
/// is a single App Configuration flip rather than a code change.
/// </summary>
public static class GateErrorPolicyConfig
{
    /// <summary>Configuration key: <c>Actions:GateErrorPolicy</c> (values: <c>fail-open</c> / <c>fail-closed</c>).</summary>
    public const string ConfigKey = "Actions:GateErrorPolicy";

    /// <summary>
    /// Reads <see cref="ConfigKey"/>. Recognised values are <c>fail-open</c> and
    /// <c>fail-closed</c> (case-insensitive). Any other / missing value defaults to
    /// <see cref="GateErrorPolicy.FailClosed"/>.
    /// </summary>
    public static GateErrorPolicy ReadGateErrorPolicy(this IConfiguration cfg)
    {
        var raw = cfg[ConfigKey];
        return string.Equals(raw?.Trim(), "fail-open", StringComparison.OrdinalIgnoreCase)
            ? GateErrorPolicy.FailOpen
            : GateErrorPolicy.FailClosed;
    }
}
