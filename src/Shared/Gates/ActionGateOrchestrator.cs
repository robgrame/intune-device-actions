using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntuneDeviceActions.Gates;

/// <summary>
/// Orchestrates cross-cutting action gates.
/// Runs gates in sequence until one defers/denies or all pass.
/// </summary>
public sealed class ActionGateOrchestrator
{
    private readonly IReadOnlyList<IActionGate> _gates;
    private readonly GateErrorPolicy _errorPolicy;
    private readonly ILogger<ActionGateOrchestrator> _log;

    public ActionGateOrchestrator(IEnumerable<IActionGate> gates, IConfiguration cfg, ILogger<ActionGateOrchestrator> log)
    {
        _gates = gates.ToList().AsReadOnly();
        _errorPolicy = cfg.ReadGateErrorPolicy();
        _log = log;
    }

    /// <summary>
    /// Run all gates in sequence until one defers/denies or all pass.
    /// </summary>
    public async Task<ActionGateResult> RunAsync(ActionGateContext context, CancellationToken ct)
    {
        _log.LogDebug("Running {GateCount} gates for action {ActionType} on device {Device}",
            _gates.Count, context.ActionType, context.DeviceName);

        foreach (var gate in _gates)
        {
            ActionGateResult result;
            try
            {
                result = await gate.CheckAsync(context, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // Transient error from gate — propagate so caller can retry
                _log.LogWarning(ex, "Gate {GateName} raised transient error for device {Device}", gate.Name, context.DeviceName);
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected gate error — apply the configured policy.
                if (_errorPolicy == GateErrorPolicy.FailOpen)
                {
                    _log.LogError(ex, "Gate {GateName} raised unexpected error for device {Device}; failing open (policy=FailOpen)", gate.Name, context.DeviceName);
                    continue;
                }

                _log.LogError(ex, "Gate {GateName} raised unexpected error for device {Device}; failing closed (policy=FailClosed)", gate.Name, context.DeviceName);
                return ActionGateResult.Denied("denied:gate-error");
            }

            switch (result.Status)
            {
                case ActionGateStatus.Deferred:
                    _log.LogInformation("Gate {GateName} deferred action for device {Device} until {When}",
                        gate.Name, context.DeviceName, result.AvailableAtUtc);
                    return result;

                case ActionGateStatus.Denied:
                    _log.LogWarning("Gate {GateName} denied action for device {Device}: {Reason}",
                        gate.Name, context.DeviceName, result.DenialReason);
                    return result;

                case ActionGateStatus.Pass:
                    _log.LogDebug("Gate {GateName} passed for device {Device}",
                        gate.Name, context.DeviceName);
                    continue;
            }
        }

        _log.LogInformation("All {GateCount} gates passed for device {Device}", _gates.Count, context.DeviceName);
        return ActionGateResult.Pass();
    }
}
