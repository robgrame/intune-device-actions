using FluentAssertions;
using IntuneDeviceActions.Gates;
using Microsoft.Extensions.Configuration;
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

    private sealed class ThrowingGate : IActionGate
    {
        public string Name => "throwing";

        public Task<ActionGateResult> CheckAsync(ActionGateContext context, CancellationToken ct) =>
            throw new InvalidCastException("boom"); // not transient, not HttpRequestException/InvalidOperationException
    }

    private static IConfiguration Config(string? gateErrorPolicy = null)
    {
        var dict = new Dictionary<string, string?>();
        if (gateErrorPolicy is not null)
        {
            dict[GateErrorPolicyConfig.ConfigKey] = gateErrorPolicy;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public async Task RunAsync_returns_deferred_when_first_gate_defers()
    {
        var orchestrator = new ActionGateOrchestrator(new IActionGate[]
        {
            new StubGate("schedule", ActionGateResult.Deferred(DateTimeOffset.UtcNow.AddMinutes(5))),
            new StubGate("device-group", ActionGateResult.Pass()),
        }, Config(), NullLogger<ActionGateOrchestrator>.Instance);

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
        }, Config(), NullLogger<ActionGateOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(BaseContext(), CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Denied);
        result.DenialReason.Should().Be("denied:device-not-in-allowed-group");
    }

    [Fact]
    public async Task RunAsync_fails_closed_by_default_when_gate_throws_unexpectedly()
    {
        var orchestrator = new ActionGateOrchestrator(new IActionGate[]
        {
            new ThrowingGate(),
        }, Config(), NullLogger<ActionGateOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(BaseContext(), CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Denied);
        result.DenialReason.Should().Be("denied:gate-error");
    }

    [Fact]
    public async Task RunAsync_fails_open_when_policy_is_fail_open_and_gate_throws()
    {
        var orchestrator = new ActionGateOrchestrator(new IActionGate[]
        {
            new ThrowingGate(),
            new StubGate("device-group", ActionGateResult.Pass()),
        }, Config("fail-open"), NullLogger<ActionGateOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(BaseContext(), CancellationToken.None);

        result.Status.Should().Be(ActionGateStatus.Pass);
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
