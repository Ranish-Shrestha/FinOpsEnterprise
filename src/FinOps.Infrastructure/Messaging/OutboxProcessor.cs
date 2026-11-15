using System.Diagnostics;
using FinOps.Application.Common;
using FinOps.Application.Messaging;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Notifications;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinOps.Infrastructure.Messaging;

/// <summary>
/// Claims a batch of messages and runs every matching handler for each.
/// <list type="bullet">
/// <item>Each handler runs in its own DI scope and transaction; its effects and its <c>ProcessedMessages</c> row
/// commit together. A handler that already committed for this message ID is skipped, so retries, lease-expiry
/// redeliveries and replays never repeat a business effect.</item>
/// <item>Handlers run in registration order, and a failure stops the remaining ones until the retry, so a later
/// handler can rely on an earlier one's effect (the notification announces a reimbursement that exists).</item>
/// <item>The message is completed only after all its handlers have committed.</item>
/// <item>A failure abandons the message with exponential backoff; after <see cref="MessagingOptions.MaxDeliveryCount"/>
/// deliveries, or immediately for a message that can never succeed, it is dead-lettered.</item>
/// </list>
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    private MessagingOptions Options => options.Value;

    /// <summary>Processes one batch. Returns the number of messages claimed (0 means the queue is idle).</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedMessage> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claimed = await scope.ServiceProvider.GetRequiredService<IMessageQueue>()
                .ClaimAsync(Options.BatchSize, TimeSpan.FromSeconds(Options.LeaseSeconds), cancellationToken);
        }

        foreach (var message in claimed) await ProcessAsync(message, cancellationToken);
        return claimed.Count;
    }

    private async Task ProcessAsync(ClaimedMessage claimed, CancellationToken cancellationToken)
    {
        var message = claimed.Message;
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = message.Id,
            ["MessageType"] = message.Type,
            ["CorrelationId"] = message.CorrelationId,
            ["DeliveryCount"] = message.DeliveryCount
        });

        // One consumer span per delivery, parented to the request that wrote the message, so one trace shows
        // the HTTP request, its SQL, and later the worker, its handlers and their SQL.
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = FinOpsDiagnostics.ActivitySource.StartActivity($"process {message.Type}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "finops.outbox");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", message.Type);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("finops.messaging.delivery_count", message.DeliveryCount);
        activity?.SetTag("finops.correlation_id", message.CorrelationId);

        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMessageQueue>();

        // A message whose worker crashed mid-processing comes back with a higher delivery count. Without this check
        // a message that crashes the process would be redelivered forever.
        if (message.DeliveryCount > Options.MaxDeliveryCount)
        {
            await DeadLetterAsync(queue, claimed, $"Exceeded {Options.MaxDeliveryCount} deliveries without completing.", cancellationToken);
            return;
        }

        try
        {
            var handlerNames = scope.ServiceProvider.GetServices<IIntegrationEventHandler>()
                .Where(h => h.CanHandle(message.Type)).Select(h => h.Name).ToList();
            if (handlerNames.Count == 0) throw new InvalidMessageException($"No handler for message type '{message.Type}'.");

            foreach (var handlerName in handlerNames) await RunHandlerAsync(handlerName, message, cancellationToken);

            if (await queue.CompleteAsync(claimed, cancellationToken))
            {
                logger.LogInformation("Processed message with {HandlerCount} handler(s).", handlerNames.Count);
                Settled(message, "completed");
                if (message.OccurredAtUtc is { } occurred)
                    FinOpsDiagnostics.DeliveryLatency.Record((clock.GetUtcNow() - occurred).TotalSeconds,
                        new KeyValuePair<string, object?>("messaging.destination.name", message.Type));
            }
            else
            {
                logger.LogWarning("Lock lost before completing; another worker owns the message. Effects are idempotent.");
                Settled(message, "lock_lost");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutting down: the lease expires and another delivery finishes the work.
        }
        catch (Exception ex) when (ex is InvalidMessageException or System.Text.Json.JsonException)
        {
            await DeadLetterAsync(queue, claimed, $"Invalid message: {ex.Message}", cancellationToken);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            if (message.DeliveryCount >= Options.MaxDeliveryCount)
            {
                MarkFailed(ex);
                await DeadLetterAsync(queue, claimed, $"Failed after {message.DeliveryCount} deliveries. {error}", cancellationToken);
                return;
            }

            var retryAt = clock.GetUtcNow() + RetryPolicy.DelayAfter(message.DeliveryCount, Options);
            logger.LogWarning(ex, "Message failed; retrying at {RetryAtUtc}.", retryAt);
            MarkFailed(ex);
            await queue.AbandonAsync(claimed, retryAt, error, cancellationToken);
            Settled(message, "retried");
        }
    }

    private async Task RunHandlerAsync(string handlerName, IntegrationMessage message, CancellationToken cancellationToken)
    {
        using var activity = FinOpsDiagnostics.ActivitySource.StartActivity($"handle {handlerName}");
        activity?.SetTag("finops.messaging.handler", handlerName);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        if (await AlreadyProcessedAsync(db, message.Id, handlerName, cancellationToken))
        {
            activity?.SetTag("finops.messaging.skipped", "already_processed");
            return;
        }

        var handler = scope.ServiceProvider.GetServices<IIntegrationEventHandler>().Single(h => h.Name == handlerName);
        await handler.HandleAsync(message, cancellationToken);
        db.ProcessedMessages.Add(new ProcessedMessage(message.Id, handlerName, clock.GetUtcNow()));

        try
        {
            // One transaction: the handler's effects and the idempotency record commit together or not at all.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another delivery of the same message committed first (lease expired while this one ran).
            // If so, the effect exists exactly once and there is nothing to do; any other duplicate is a real error.
            await using var check = scopes.CreateAsyncScope();
            if (!await AlreadyProcessedAsync(check.ServiceProvider.GetRequiredService<FinOpsDbContext>(), message.Id,
                    handlerName, cancellationToken)) throw;
            logger.LogInformation("Handler {Handler} was already committed by a concurrent delivery.", handlerName);
        }
    }

    private static Task<bool> AlreadyProcessedAsync(FinOpsDbContext db, Guid messageId, string handlerName,
        CancellationToken cancellationToken) =>
        db.ProcessedMessages.AnyAsync(p => p.MessageId == messageId && p.Handler == handlerName, cancellationToken);

    private async Task DeadLetterAsync(IMessageQueue queue, ClaimedMessage claimed, string reason,
        CancellationToken cancellationToken)
    {
        logger.LogError("Message dead-lettered: {Reason}", reason);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, "dead-lettered");
        await queue.DeadLetterAsync(claimed, reason, cancellationToken);
        Settled(claimed.Message, "dead_lettered");
    }

    private static void Settled(IntegrationMessage message, string outcome) =>
        FinOpsDiagnostics.MessagesSettled.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", message.Type),
            new KeyValuePair<string, object?>("outcome", outcome));

    private static void MarkFailed(Exception ex)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
        activity.AddException(ex);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };
}

/// <summary>Writes handler effects to the scope's DbContext; <see cref="OutboxProcessor"/> commits them.</summary>
internal sealed class MessageEffects(FinOpsDbContext db) : IMessageEffects
{
    public void Add(Notification notification) => db.Notifications.Add(notification);
    public void Add(Reimbursement reimbursement) => db.Reimbursements.Add(reimbursement);
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    public Task<bool> ReimbursementExistsAsync(Guid expenseRequestId, CancellationToken cancellationToken) =>
        db.Reimbursements.AnyAsync(r => r.ExpenseRequestId == expenseRequestId, cancellationToken);
}

/// <summary>The hosted loop: process batches back to back while there is work, then poll.</summary>
internal sealed class OutboxWorker(OutboxProcessor processor, IServiceScopeFactory scopes, IOptions<MessagingOptions> options,
    TimeProvider clock, ILogger<OutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    private DateTimeOffset nextSample = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Outbox worker started; polling every {PollInterval}.", poll);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleOutboxAsync(stoppingToken);
                if (await processor.ProcessBatchAsync(stoppingToken) > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // For example SQL Server unavailable. Keep the worker alive and try again after the poll interval.
                logger.LogError(ex, "Outbox batch failed; retrying after the poll interval.");
            }

            try { await Task.Delay(poll, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Feeds the backlog, dead-letter and oldest-pending gauges. Both counts use filtered indexes.</summary>
    private async Task SampleOutboxAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now < nextSample) return;
        nextSample = now + SampleInterval;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        var pending = db.OutboxMessages.Where(m => m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null);
        var pendingCount = await pending.CountAsync(cancellationToken);
        var oldest = await pending.OrderBy(m => m.NextAttemptAtUtc).Select(m => (DateTimeOffset?)m.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var deadLettered = await db.OutboxMessages.CountAsync(m => m.DeadLetteredAtUtc != null, cancellationToken);
        FinOpsDiagnostics.ReportOutbox(pendingCount, deadLettered, oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : 0);
    }
}
