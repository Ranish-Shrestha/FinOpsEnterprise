# ADR 0005: Transactional outbox and a swappable local queue

Status: accepted (2026-11-14).

## Context

Phase 5 asks for business events (for example `ExpenseApproved`), a background worker, retries and idempotency. Its exit criterion is that "event processing survives retries without duplicate business effects". The spec names Azure Service Bus.

There are four constraints:

- **Cost and setup.** Real Service Bus needs an Azure subscription, a namespace and a secret in CI. Without the cloud, the tests could not run.
- **The emulator is heavy.** The Service Bus emulator adds two containers (the emulator plus its SQL Edge dependency) to every local run and CI job.
- **Dual writes lose events.** If the API commits the expense change and then publishes to a broker, a crash in between loses the event. If it publishes first, it can announce a change that later rolls back. Neither is acceptable for money.
- **Every real broker delivers at least once.** Service Bus redelivers when a lock expires or a consumer crashes. Exactly-once business effects therefore have to come from the consumer, whatever broker is chosen.

## Decision

1. **Transactional outbox.** `ExpenseWorkflow` adds an `OutboxMessage` to the same `SaveChanges` as the state change and its audit row. An event exists if and only if the change committed.
   - Events: `expense.approved.v1`, `expense.rejected.v1` and `expense.posted.v1`.
   - Payload: IDs and the amount only, never descriptions or comments.
   - The message carries the HTTP request's correlation ID.
2. **Local queue, behind a broker port.** `IMessageQueue` models Service Bus peek-lock: `Claim`, `Complete`, `Abandon(retryAt)` and `DeadLetter`.
   - The local implementation (`SqlOutboxQueue`) is the outbox table itself.
   - A claim is one `UPDATE … OUTPUT` with `UPDLOCK, READPAST`. It sets a lease (`LockedUntilUtc`), a fresh `LockToken` and increments `DeliveryCount`.
   - Settling requires the current lock token, so a worker whose lease expired changes nothing.
   - A future Service Bus implementation adds a relay (outbox → topic) and a receiver behind the same calls, and leaves the handlers unchanged.
3. **Idempotent handlers, one transaction each.** A message can have several handlers. For each one, the processor:
   - skips the handler if `messaging.ProcessedMessages` already has (message ID, handler name);
   - otherwise runs it, and commits its effects and that idempotency row together;
   - treats a primary-key collision from a concurrent delivery as "already done".

   A retry after a partial failure therefore re-runs only the handlers that did not commit.
4. **Database backstops.** One reimbursement per expense (`UX_Reimbursements_Expense`) and one notification per user per message (`UX_Notifications_Source_User`). The reimbursement handler also checks by business key, so even a *different* message about the same posted expense cannot pay twice.
5. **Retries and dead letters.**
   - Exponential backoff: 5 s, 10 s, 20 s, … capped at 300 s.
   - After `MaxDeliveryCount` (default 5) deliveries, the message is dead-lettered.
   - Messages that can never succeed (unknown type, unreadable payload) are dead-lettered on their first delivery.
   - A message that keeps crashing its worker is dead-lettered once its delivery count exceeds the limit.
   - Administrators (`messaging.manage`) can list dead letters and replay them. Replay keeps the message ID, so it cannot repeat effects, and it is audited.
6. **Business effects.**
   - The requester gets an in-app notification on approve, reject and post.
   - A posted expense gets exactly one `Reimbursement` record, audited as a system action (`reimbursement.scheduled`, no actor) that carries the posting request's correlation ID. Executing the payment is out of scope.
7. **Separate worker process.** `FinOps.Worker` hosts the processor. The API never runs it, so API tests and scaling stay independent. Several workers can run at once.

## Consequences

- **The guarantee is tested against real SQL Server**, including:
  - redelivery after a lost completion;
  - a handler failing after another handler committed;
  - a worker whose lease expires mid-handler while another worker finishes the message;
  - four concurrent workers over a duplicated publication;
  - backoff timing, dead-lettering and replay.

  Removing the idempotency record makes three of these tests fail.
- **Delivery is at least once and unordered.** Handlers must tolerate both. They do today: notifications are independent, and only `expense.posted` creates money records.
- **Polling latency.** The worker polls every 2 s when idle, and a busy worker drains batches back to back. This suits a portfolio workload. At higher volume, use a real broker, or wake the worker on insert.
- **Unbounded history.** Processed outbox rows and idempotency rows are kept. The pending index is filtered, so claims don't slow down as history grows, but a retention job is needed before production.
- **Moving to Service Bus later** means implementing `IMessageQueue` (or a relay plus processor) and choosing duplicate detection and sessions. Handlers and their idempotency stay as they are.
