using IntuneWipeApi;
using IntuneWipeApi.Actions;
using IntuneWipeApi.Actions.Runners;
using IntuneWipeApi.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(b =>
    {
        b.UseMiddleware<AppConfigRefreshMiddleware>();
    })
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneWipeApiAppConfig(roleHint: "proc"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneWipeApiCore();
        services.AddGraphWipe();                  // poller uses GraphWipeService
        services.AddIdempotency();                // processor may inspect ledger entry on prep
        services.AddActionDispatchSender();       // processor → ActionDispatch queue
        services.AddWipeActionQueueSender();      // WipeForwardingRunner → wipe-action queue
        services.AddWipeStatusTracker();          // poller updates wipestatus table
        services.AddSingleton<ActionRunnerRegistry>();
        // Plug-in runners — dispatcher resolves by ActionType:
        //   "wipe"         → WipeForwardingRunner       → wipe-action queue → Wipe Function App
        //   "wipe-runbook" → WipeRunbookForwardingRunner → Automation webhook (PowerShell 7.2 runbook)
        services.AddSingleton<IActionRunner, WipeForwardingRunner>();
        services.AddSingleton<IActionRunner, WipeRunbookForwardingRunner>();
    })
    .Build();

host.Run();
