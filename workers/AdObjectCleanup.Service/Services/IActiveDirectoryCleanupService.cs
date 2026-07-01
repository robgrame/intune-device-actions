namespace IntuneDeviceActions.Workers.AdObjectCleanup.Services;

/// <summary>Outcome of an AD cleanup attempt for a single target name.</summary>
public sealed class AdCleanupResult
{
    public int Found { get; init; }
    public int Excluded { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public bool DryRun { get; init; }
    public bool CapExceeded { get; init; }
    public IReadOnlyList<string> Objects { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Thrown for a <b>transient</b> AD failure (DC unreachable, RPC error) so the
/// caller ABANDONS the message for redelivery rather than dead-lettering it.
/// </summary>
public sealed class TransientAdException : Exception
{
    public TransientAdException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IActiveDirectoryCleanupService
{
    /// <summary>
    /// Deletes the on-prem AD computer object(s) named <paramref name="targetName"/>
    /// (scoped by the configured OU), honouring the exclusion list and the
    /// per-message delete cap. Throws <see cref="TransientAdException"/> on a
    /// transient directory failure.
    /// </summary>
    AdCleanupResult DeleteByName(string targetName, CancellationToken ct);
}
