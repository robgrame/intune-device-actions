using Azure.Identity;
using Azure.Storage.Queues;
using IntuneWipeApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(b => b.UseDefaultWorkerMiddleware())
    .ConfigureAppConfiguration(c => c.AddEnvironmentVariables())
    .ConfigureServices((ctx, services) =>
    {
        services.AddLogging();
        services.AddApplicationInsightsTelemetryWorkerService();

        var cfg = ctx.Configuration;

        services.AddSingleton<ClientCertValidator>();

        services.AddSingleton(_ =>
        {
            var queueName = cfg["Queue:WipeQueueName"] ?? "wipe-requests";
            var conn = cfg["AzureWebJobsStorage"];
            QueueClient client;
            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
            if (!string.IsNullOrWhiteSpace(conn) && conn != "UseDevelopmentStorage=true"
                && conn.Contains("AccountKey", StringComparison.OrdinalIgnoreCase))
            {
                client = new QueueClient(conn, queueName, options);
            }
            else
            {
                var account = cfg["AzureWebJobsStorage__accountName"]
                    ?? throw new InvalidOperationException("AzureWebJobsStorage__accountName must be set when using identity-based connection");
                client = new QueueClient(
                    new Uri($"https://{account}.queue.core.windows.net/{queueName}"),
                    new DefaultAzureCredential(),
                    options);
            }
            client.CreateIfNotExists();
            return client;
        });

        services.AddSingleton(_ =>
        {
            var cred = new DefaultAzureCredential();
            return new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });
        });
        services.AddSingleton<GraphWipeService>();
    })
    .Build();

host.Run();
