using FluentAssertions;
using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntuneDeviceActions.Shared.Tests.Gates;

public sealed class ActionGateOrchestratorTests
{
    private sealed class StubGate : IActionGate
    {
        private readonly ActionGateResult _result;

        public StubGate(string name, ActionGateResult result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }

        public Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    [Fact]
    public async Task RunAsync_returns_deferred_when_first_gate_defers()
    {
        var orchestrator = new ActionGateOrchestrator(new IActionGate[]
        {
            new StubGate("schedule", ActionGateResult.Deferred(DateTimeOffset.UtcNow.AddMinutes(5))),
            new StubGate("device-group", ActionGateResult.Pass()),
        }, NullLogger<ActionGateOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(BaseContext(), CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Deferred);
    }

    [Fact]
    public async Task RunAsync_returns_denied_when_any_gate_denies()
    {
        var orchestrator = new ActionGateOrchestrator(new IActionGate[]
        {
            new StubGate("schedule", ActionGateResult.Pass()),
            new StubGate("device-group", ActionGateResult.Denied("denied:device-not-in-allowed-group")),
            new StubGate("user-group", ActionGateResult.Pass()),
        }, NullLogger<ActionGateOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(BaseContext(), CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Denied);
        result.DenialReason.Should().Be("denied:device-not-in-allowed-group");
    }

    private static ActionGateContext BaseContext() => new()
    {
        EntraDeviceId = Guid.NewGuid(),
        DeviceObjectId = "device-object",
        DeviceName = "PC1",
        ActionType = "wipe",
        CorrelationId = "c1",
    };
}
