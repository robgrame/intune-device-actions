namespace IntuneDeviceActions.Workers.AdObjectCleanup.Options;

/// <summary>
/// Bound from the <c>AdCleanup</c> configuration section (appsettings.json,
/// environment variables, or command line). Controls Service Bus connectivity
/// and the AD-deletion guardrails.
/// </summary>
public sealed class AdCleanupOptions
{
    public const string SectionName = "AdCleanup";

    // ── Service Bus connectivity ─────────────────────────────────────────────
    /// <summary>
    /// Full connection string of a Listen SAS rule on the queue
    /// (Endpoint=sb://…;SharedAccessKeyName=worker-listen;SharedAccessKey=…).
    /// Takes precedence over <see cref="FullyQualifiedNamespace"/>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>SB namespace FQDN (…​.servicebus.windows.net) for AAD/MI auth when no connection string is set.</summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>Queue name (default <c>ad-object-cleanup</c>).</summary>
    public string QueueName { get; set; } = "ad-object-cleanup";

    /// <summary>Concurrent message handlers (default 1 — deletions are serialized for safety).</summary>
    public int MaxConcurrentCalls { get; set; } = 1;

    // Optional service-principal auth (used when only FullyQualifiedNamespace is
    // set and these three are present; otherwise DefaultAzureCredential is used).
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // ── AD deletion guardrails ───────────────────────────────────────────────
    /// <summary>
    /// Distinguished name of the OU subtree deletions are constrained to
    /// (e.g. <c>OU=Workstations,DC=corp,DC=contoso,DC=com</c>). STRONGLY
    /// recommended. When empty the whole domain is searched.
    /// </summary>
    public string? SearchBase { get; set; }

    /// <summary>Computer names that must never be deleted (case-insensitive).</summary>
    public string[] ExclusionNames { get; set; } = Array.Empty<string>();

    /// <summary>Max AD objects a single message may delete; a message resolving to more is dead-lettered (default 5).</summary>
    public int MaxDeletePerMessage { get; set; } = 5;

    /// <summary>When true, resolves and logs the objects that WOULD be deleted but deletes nothing.</summary>
    public bool DryRun { get; set; }
}
