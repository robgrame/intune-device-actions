using System.Text.Json;
using Azure.Messaging.ServiceBus;
using IntuneDeviceActions.Workers.AdObjectCleanup.Models;
using IntuneDeviceActions.Workers.AdObjectCleanup.Options;
using IntuneDeviceActions.Workers.AdObjectCleanup.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntuneDeviceActions.Workers.AdObjectCleanup;

/// <summary>
/// Event-driven consumer of the <c>ad-object-cleanup</c> Service Bus queue.
/// A <see cref="ServiceBusProcessor"/> invokes the handler as messages arrive
/// (no polling). For each message the target AD computer object(s) are deleted
/// via <see cref="IActiveDirectoryCleanupService"/>:
/// <list type="bullet">
///   <item>success            → Complete;</item>
///   <item>transient AD fault → Abandon (redelivered);</item>
///   <item>poison message     → Dead-letter (bad schema/type, blank name, cap
///         exceeded) so it never poison-loops.</item>
/// </list>
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusClient _client;
    private readonly IActiveDirectoryCleanupService _ad;
    private readonly AdCleanupOptions _opts;
    private readonly ILogger<Worker> _log;
    private ServiceBusProcessor? _processor;

    public Worker(ServiceBusClient client, IActiveDirectoryCleanupService ad,
        IOptions<AdCleanupOptions> opts, ILogger<Worker> log)
    {
        _client = client;
        _ad = ad;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _client.CreateProcessor(_opts.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = Math.Max(1, _opts.MaxConcurrentCalls),
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        _log.LogInformation(
            "AD object cleanup worker starting. queue={Queue} searchBase={SearchBase} maxDelete={MaxDelete} dryRun={DryRun}",
            _opts.QueueName, string.IsNullOrWhiteSpace(_opts.SearchBase) ? "(domain)" : _opts.SearchBase,
            _opts.MaxDeletePerMessage, _opts.DryRun);

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { /* shutting down */ }

        await _processor.StopProcessingAsync(CancellationToken.None);
        _log.LogInformation("AD object cleanup worker stopped.");
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var msg = args.Message;
        var correlationId = msg.CorrelationId;

        AdCleanupMessage? payload;
        try
        {
            payload = msg.Body.ToObjectFromJson<AdCleanupMessage>(Json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Dead-lettering unparseable message. correlationId={CorrelationId}", correlationId);
            await args.DeadLetterMessageAsync(msg, "invalid-json", ex.Message, args.CancellationToken);
            return;
        }

        // ── Contract validation (poison → dead-letter, never loop) ───────────
        var messageType = msg.ApplicationProperties.TryGetValue("messageType", out var mt) ? mt?.ToString() : msg.Subject;
        if (!string.IsNullOrEmpty(messageType) &&
            !string.Equals(messageType, AdCleanupMessage.MessageType, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Dead-lettering unexpected messageType={MessageType}. correlationId={CorrelationId}", messageType, correlationId);
            await args.DeadLetterMessageAsync(msg, "unexpected-message-type", messageType, args.CancellationToken);
            return;
        }

        var target = payload?.TargetName?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            _log.LogWarning("Dead-lettering message with blank targetName. correlationId={CorrelationId}", correlationId);
            await args.DeadLetterMessageAsync(msg, "blank-target-name", "targetName is required", args.CancellationToken);
            return;
        }

        _log.LogInformation("Processing cleanup. correlationId={CorrelationId} targetName={Target} source={Source}",
            correlationId, target, payload!.SourceDeviceName);

        AdCleanupResult result;
        try
        {
            result = _ad.DeleteByName(target, args.CancellationToken);
        }
        catch (TransientAdException ex)
        {
            _log.LogWarning(ex, "Transient AD failure; abandoning for retry. correlationId={CorrelationId} targetName={Target}", correlationId, target);
            await args.AbandonMessageAsync(msg, cancellationToken: args.CancellationToken);
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected failure; abandoning. correlationId={CorrelationId} targetName={Target}", correlationId, target);
            await args.AbandonMessageAsync(msg, cancellationToken: args.CancellationToken);
            return;
        }

        if (result.CapExceeded)
        {
            _log.LogError(
                "Delete cap exceeded ({Found} > {Cap}); dead-lettering WITHOUT deleting. correlationId={CorrelationId} targetName={Target}",
                result.Found, _opts.MaxDeletePerMessage, correlationId, target);
            await args.DeadLetterMessageAsync(msg, "delete-cap-exceeded",
                $"{result.Found} objects > cap {_opts.MaxDeletePerMessage}", args.CancellationToken);
            return;
        }

        _log.LogInformation(
            "Cleanup complete. correlationId={CorrelationId} targetName={Target} found={Found} deleted={Deleted} excluded={Excluded} skipped={Skipped} dryRun={DryRun} objects={Objects}",
            correlationId, target, result.Found, result.Deleted, result.Excluded, result.Skipped, result.DryRun,
            string.Join(";", result.Objects));

        await args.CompleteMessageAsync(msg, args.CancellationToken);
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _log.LogError(args.Exception, "Service Bus processor error. source={Source} entity={Entity}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _processor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
