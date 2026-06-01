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
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneWipeApiAppConfig(roleHint: "wipe"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneWipeApiCore();
        services.AddGraphWipe();                  // privileged Graph identity LIVES here
        services.AddIdempotency();                // reserve / mark issued / mark failed
        services.AddWipeStatusTracker();          // init state on wipe issued
        // The consumer function resolves WipeActionRunner directly (concrete type).
        services.AddSingleton<WipeActionRunner>();
        services.AddSingleton<IActionRunner>(sp => sp.GetRequiredService<WipeActionRunner>());
    })
    .Build();

host.Run();
