using System.Text.Json.Serialization;

namespace IntuneDeviceActions.Models;

public sealed class ActionRequestMessage
{
    [JsonPropertyName("deviceName")]       public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("entraDeviceId")]    public string EntraDeviceId { get; set; } = string.Empty;
    [JsonPropertyName("intuneDeviceId")]   public string IntuneDeviceId { get; set; } = string.Empty;
    [JsonPropertyName("correlationId")]    public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("clientCertThumbprint")] public string? ClientCertThumbprint { get; set; }
    [JsonPropertyName("requestedAt")]      public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// If true AND the worker has <c>Idempotency:AllowForceRearm=true</c>, the
    /// idempotency ledger will be re-armed unconditionally for this request,
    /// bypassing the tracker-based completion check. Intended for DEV/testing
    /// scenarios where repeated wipes must be issued to the same device
    /// (especially with keepEnrollmentData=true). Set from the
    /// <c>X-Force-Rearm: true</c> request header at the HTTP boundary.
    /// </summary>
    [JsonPropertyName("forceRearm")]       public bool ForceRearm { get; set; }
}
