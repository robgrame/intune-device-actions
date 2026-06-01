using IntuneWipeApi;
using IntuneWipeApi.Middleware;
using IntuneWipeApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(b =>
    {
        b.UseDefaultWorkerMiddleware();
        b.UseMiddleware<AppConfigRefreshMiddleware>();
    })
    .ConfigureAppConfiguration((ctx, c) => c.AddIntuneWipeApiAppConfig(roleHint: "web"))
    .ConfigureServices((ctx, services) =>
    {
        services.AddIntuneWipeApiCore();
        // Web-only: cert mTLS + replay nonce + directory resolver (Graph lookup for non-GUID claim).
        services.AddSingleton<ClientCertValidator>();
        services.AddSingleton<ReplayProtector>();
        services.AddGraphWipe();                  // GraphServiceClient (used by DeviceDirectoryResolver + WipeStatusTracker)
        services.AddSingleton<DeviceDirectoryResolver>();
        services.AddIdempotency();                // admin reset endpoint
        services.AddWipeRequestQueueSender();     // enqueue to proc
        services.AddWipeStatusTracker();          // GET /api/wipe/status reads it
    })
    .Build();

host.Run();
