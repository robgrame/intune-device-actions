using IntuneDeviceActions;
using IntuneDeviceActions.Capabilities.Rename;
using IntuneDeviceActions.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(b =>
    {
        b.UseMiddleware<AppConfigRefreshMiddleware>();
        b.UseMiddleware<ServiceBusTraceContextMiddleware>();
    })
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneDeviceActionsAppConfig(roleHint: "rename"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneDeviceActionsCore();
        services.AddIntuneDeviceActionsOpenTelemetry(role: "rename");
        // NOTE: no AddGraphClient() — rename uses a customer REST endpoint, not Graph.
        services.AddActionIdempotency();          // reserve / mark issued / mark failed
        services.AddActionStatusTracker();        // init state on action issued

        // Rename capability — Rename role hosts the executor:
        //   AddRenameExecutor: HttpCustomerRenameClient + RenameActionRunner.
        // The consumer function resolves RenameActionRunner directly (concrete type).
        services.AddRenameExecutor();
    })
    .Build();

host.Run();
