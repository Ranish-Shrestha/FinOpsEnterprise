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
                logger.LogInformation("Processed message with {HandlerCount} handler(s).", handlerNames.Count);
            else
                logger.LogWarning("Lock lost before completing; another worker owns the message. Effects are idempotent.");
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
                await DeadLetterAsync(queue, claimed, $"Failed after {message.DeliveryCount} deliveries. {error}", cancellationToken);
                return;
            }

            var retryAt = clock.GetUtcNow() + RetryPolicy.DelayAfter(message.DeliveryCount, Options);
            logger.LogWarning(ex, "Message failed; retrying at {RetryAtUtc}.", retryAt);
            await queue.AbandonAsync(claimed, retryAt, error, cancellationToken);
        }
    }

    private async Task RunHandlerAsync(string handlerName, IntegrationMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        if (await AlreadyProcessedAsync(db, message.Id, handlerName, cancellationToken)) return;

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
        await queue.DeadLetterAsync(claimed, reason, cancellationToken);
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
internal sealed class OutboxWorker(OutboxProcessor processor, IOptions<MessagingOptions> options, TimeProvider clock,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Outbox worker started; polling every {PollInterval}.", poll);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
}
