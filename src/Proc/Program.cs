using IntuneDeviceActions;
using IntuneDeviceActions.Actions;
using IntuneDeviceActions.Actions.Runners;
using IntuneDeviceActions.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(b =>
    {
        b.UseMiddleware<AppConfigRefreshMiddleware>();
        b.UseMiddleware<ServiceBusTraceContextMiddleware>();
    })
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneDeviceActionsAppConfig(roleHint: "proc"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneDeviceActionsCore();
        services.AddIntuneDeviceActionsOpenTelemetry(role: "proc");
        services.AddGraphWipe();                  // poller uses GraphWipeService
        services.AddIdempotency();                // processor may inspect ledger entry on prep
        services.AddActionDispatchSender();       // processor → ActionDispatch queue
        services.AddWipeActionSender();      // WipeForwardingRunner → wipe-action queue
        services.AddActionStatusTracker();          // poller updates wipestatus table
        services.AddSingleton<ActionRunnerRegistry>();
        // Plug-in runners — dispatcher resolves by ActionType:
        //   "wipe"         → WipeForwardingRunner       → wipe-action queue → Wipe Function App
        //   "wipe-runbook" → WipeRunbookForwardingRunner → Automation webhook (PowerShell 7.2 runbook)
        services.AddSingleton<IActionRunner, WipeForwardingRunner>();
        services.AddSingleton<IActionRunner, WipeRunbookForwardingRunner>();
    })
    .Build();

host.Run();
