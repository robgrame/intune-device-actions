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
    })
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneDeviceActionsAppConfig(roleHint: "wipe"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneDeviceActionsCore();
        services.AddGraphWipe();                  // privileged Graph identity LIVES here
        services.AddIdempotency();                // reserve / mark issued / mark failed
        services.AddActionStatusTracker();          // init state on wipe issued
        // The consumer function resolves WipeActionRunner directly (concrete type).
        services.AddSingleton<WipeActionRunner>();
        services.AddSingleton<IActionRunner>(sp => sp.GetRequiredService<WipeActionRunner>());
    })
    .Build();

host.Run();
