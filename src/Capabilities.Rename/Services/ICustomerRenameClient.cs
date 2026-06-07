namespace IntuneDeviceActions.Capabilities.Rename.Services;

/// <summary>
/// Abstraction over the customer-internal device-rename REST endpoint. Lives
/// behind an interface so the runner can be unit-tested without an HTTP
/// dependency and so customer-specific transports (mTLS, API gateway, Service
/// Bus relay, …) can be swapped in without touching the runner.
/// </summary>
public interface ICustomerRenameClient
{
    /// <summary>
    /// POST the rename request to the customer endpoint. The implementation
    /// classifies the HTTP outcome into a <see cref="RenameRestOutcome"/> the
    /// runner uses to drive ledger/audit/status. Network-level failures and
    /// 5xx/429/408/timeouts MUST be surfaced as <see cref="RenameRestOutcome.Kind.Transient"/>
    /// so the per-capability Service Bus consumer can retry; client errors
    /// (4xx other than 429/408) are <see cref="RenameRestOutcome.Kind.Permanent"/>.
    /// </summary>
    Task<RenameRestOutcome> RenameAsync(RenameRestRequest request, CancellationToken ct);
}

/// <summary>
/// Body posted to the customer endpoint. <c>serialNumber</c> is the customer's
/// authoritative device key and <c>newName</c> is the desired hostname;
/// correlation id and intune ids are passed along for the customer's own
/// auditing/correlation but are NOT required by the contract.
/// </summary>
public sealed record RenameRestRequest(
    string SerialNumber,
    string NewName,
    string CorrelationId,
    string? IntuneDeviceId,
    string? DeviceName);

/// <summary>
/// Classified result of the customer REST call. The runner uses
/// <see cref="OutcomeKind"/> to decide between mark-issued (accepted),
/// mark-failed-permanent (4xx other than 429/408), or throw-for-retry
/// (transient).
/// </summary>
public sealed record RenameRestOutcome(
    RenameRestOutcome.Kind OutcomeKind,
    int StatusCode,
    string Reason)
{
    public enum Kind
    {
        /// <summary>Customer accepted the rename (2xx).</summary>
        Accepted,
        /// <summary>Permanent client error (4xx, NOT 408 or 429). No retry.</summary>
        Permanent,
        /// <summary>Transient (5xx, 408, 429, timeout, network). Throw so the SB consumer retries.</summary>
        Transient,
    }
}
