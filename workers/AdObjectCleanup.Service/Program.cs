using Azure.Identity;
using Azure.Messaging.ServiceBus;
using IntuneDeviceActions.Workers.AdObjectCleanup;
using IntuneDeviceActions.Workers.AdObjectCleanup.Options;
using IntuneDeviceActions.Workers.AdObjectCleanup.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service when installed via sc.exe / New-Service; runs as a
// plain console app when started interactively (for testing).
builder.Services.AddWindowsService(o => o.ServiceName = "IntuneDeviceActions-AdObjectCleanup");

builder.Services.AddOptions<AdCleanupOptions>()
    .Bind(builder.Configuration.GetSection(AdCleanupOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString) || !string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace),
        "Provide AdCleanup:ConnectionString (SAS) or AdCleanup:FullyQualifiedNamespace (AAD).")
    .Validate(o => !string.IsNullOrWhiteSpace(o.QueueName), "AdCleanup:QueueName is required.")
    .Validate(o => o.MaxDeletePerMessage > 0, "AdCleanup:MaxDeletePerMessage must be > 0.")
    .ValidateOnStart();

// Single shared ServiceBusClient. Prefer a Listen SAS connection string
// (on-prem boxes rarely have a managed identity); fall back to AAD via
// service-principal secret or DefaultAzureCredential when only the namespace
// is configured.
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<AdCleanupOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(o.ConnectionString))
    {
        return new ServiceBusClient(o.ConnectionString);
    }

    if (!string.IsNullOrWhiteSpace(o.TenantId) &&
        !string.IsNullOrWhiteSpace(o.ClientId) &&
        !string.IsNullOrWhiteSpace(o.ClientSecret))
    {
        return new ServiceBusClient(o.FullyQualifiedNamespace,
            new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret));
    }

    return new ServiceBusClient(o.FullyQualifiedNamespace, new DefaultAzureCredential());
});

builder.Services.AddSingleton<IActiveDirectoryCleanupService, ActiveDirectoryCleanupService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
