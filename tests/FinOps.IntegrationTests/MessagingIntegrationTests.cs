using System.Net;
using System.Net.Http.Json;
using FinOps.Application.Expenses;
using FinOps.Application.Messaging;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Messaging;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Messaging;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinOps.IntegrationTests;

/// <summary>
/// Transactional outbox, worker, retries, dead-lettering and idempotency against real SQL Server.
/// The worker is hosted exactly as FinOps.Worker hosts it (AddInfrastructure), with a controllable clock.
/// Every test first drains messages left by other tests, then creates its own, so assertions are exact.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class MessagingIntegrationTests(SqlServerFixture fixture)
{
    private const string NotificationHandler = "notifications.expense-status";
    private const string ReimbursementHandlerName = "reimbursements.schedule";

    // ---------- Outbox writes (API side) ----------

    [Fact]
    public async Task Approve_and_post_each_write_one_event_with_the_request_correlation_id()
    {
        var expense = await SubmittedByEmployeeAsync();
        Assert.Empty(await EventsForAsync(expense.Id)); // create and submit publish nothing

        var approved = await MustAsync(DemoDataSeeder.EngineeringManager, expense, ExpenseActions.Approve);
        await MustAsync(DemoDataSeeder.FinanceOfficer, approved, ExpenseActions.Post);

        var events = await EventsForAsync(expense.Id);
        Assert.Equal([IntegrationEventTypes.ExpenseApproved, IntegrationEventTypes.ExpensePosted], events.Select(e => e.Type));
        var auditCorrelations = await fixture.WithDbAsync(db => db.AuditEvents
            .Where(a => a.ResourceId == expense.Id && (a.Action == "expense.approved" || a.Action == "expense.posted"))
            .OrderBy(a => a.Id).Select(a => a.CorrelationId).ToListAsync());
        Assert.Equal(auditCorrelations, events.Select(e => e.CorrelationId));
        Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.CorrelationId)));

        var payload = ExpenseStatusChanged.Deserialize(events[1].Payload);
        Assert.Equal((expense.Id, DemoDataSeeder.Employee.UserId, 42.50m, DemoDataSeeder.FinanceOfficer.UserId),
            (payload.ExpenseId, payload.RequesterId, payload.Amount, payload.ActorUserId));
    }

    [Fact]
    public async Task Refused_and_racing_transitions_write_no_extra_events()
    {
        // Stale ETag: 412, nothing written.
        var expense = await SubmittedByEmployeeAsync();
        var stale = await ActAsync(DemoDataSeeder.EngineeringManager, expense.Id, ExpenseActions.Approve, "\"AAAAAAAAAAE=\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        // Self-approval: 403, nothing written.
        var own = await CreateAsync(DemoDataSeeder.EngineeringManager);
        own = await MustAsync(DemoDataSeeder.EngineeringManager, own, ExpenseActions.Submit);
        var self = await ActAsync(DemoDataSeeder.EngineeringManager, own.Id, ExpenseActions.Approve, own.Version);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Empty(await EventsForAsync(own.Id));

        // Two approvals with the same ETag at once: one commits, and only its event exists.
        var responses = await Task.WhenAll(
            ActAsync(DemoDataSeeder.EngineeringManager, expense.Id, ExpenseActions.Approve, expense.Version),
            ActAsync(DemoDataSeeder.EngineeringManager, expense.Id, ExpenseActions.Approve, expense.Version));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(await EventsForAsync(expense.Id));
    }

    // ---------- Worker: delivery and effects ----------

    [Fact]
    public async Task Worker_notifies_the_requester_and_schedules_one_reimbursement()
    {
        var clock = Clock();
        await using var worker = Worker(clock);
        await DrainAsync(worker);

        var submitted = await SubmittedByEmployeeAsync();
        var approved = await MustAsync(DemoDataSeeder.EngineeringManager, submitted, ExpenseActions.Approve);
        await MustAsync(DemoDataSeeder.FinanceOfficer, approved, ExpenseActions.Post);
        await DrainAsync(worker);

        Assert.All(await EventsForAsync(submitted.Id), e =>
        {
            Assert.NotNull(e.ProcessedAtUtc);
            Assert.Equal(1, e.DeliveryCount);
        });
        Assert.Equal(1, await ReimbursementsForAsync(submitted.Id));

        // The reimbursement is audited as a system action, linked to the posting request by correlation ID.
        var audits = await fixture.WithDbAsync(db => db.AuditEvents.Where(a => a.ResourceId == submitted.Id)
            .OrderBy(a => a.Id).ToListAsync());
        var scheduled = Assert.Single(audits, a => a.Action == "reimbursement.scheduled");
        Assert.Null(scheduled.ActorUserId);
        Assert.Equal(audits.Single(a => a.Action == "expense.posted").CorrelationId, scheduled.CorrelationId);

        // The requester sees the outcome; the detail shows the reimbursement.
        using var employee = As(DemoDataSeeder.Employee);
        var detail = await employee.GetFromJsonAsync<ExpenseDetailDto>($"/api/expenses/{submitted.Id}");
        Assert.NotNull(detail!.ReimbursementScheduledAtUtc);
        var inbox = await employee.GetFromJsonAsync<NotificationInboxDto>("/api/notifications");
        Assert.Equal(["ExpenseApproved", "ExpensePosted"],
            inbox!.Items.Where(n => n.ExpenseId == submitted.Id).Select(n => n.Kind).Order());
    }

    [Fact]
    public async Task Notifications_are_private_to_their_recipient_and_marking_read_is_idempotent()
    {
        var clock = Clock();
        await using var worker = Worker(clock);
        var (expenseId, _) = await PostedExpenseWithEventAsync();
        await DrainAsync(worker);

        using var employee = As(DemoDataSeeder.Employee);
        using var manager = As(DemoDataSeeder.EngineeringManager);
        var before = (await employee.GetFromJsonAsync<NotificationInboxDto>("/api/notifications"))!;
        var mine = Assert.Single(before.Items, n => n.ExpenseId == expenseId);
        Assert.Equal(("Messaging test", 20m), (mine.ExpenseDescription, mine.Amount));

        var managerInbox = (await manager.GetFromJsonAsync<NotificationInboxDto>("/api/notifications"))!;
        Assert.DoesNotContain(managerInbox.Items, n => n.Id == mine.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"/api/notifications/{mine.Id}/read", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await employee.PostAsync($"/api/notifications/{mine.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await employee.PostAsync($"/api/notifications/{mine.Id}/read", null)).StatusCode);
        var after = (await employee.GetFromJsonAsync<NotificationInboxDto>("/api/notifications"))!;
        Assert.NotNull(after.Items.Single(n => n.Id == mine.Id).ReadAtUtc);
        Assert.Equal(before.UnreadCount - 1, after.UnreadCount);
    }

    // ---------- Idempotency under redelivery, partial failure and concurrency ----------

    [Fact]
    public async Task Redelivery_after_a_lost_completion_repeats_no_effect()
    {
        var clock = Clock();
        await using var worker = Worker(clock);
        await DrainAsync(worker);
        var (expenseId, messageId) = await PostedExpenseWithEventAsync();
        await DrainAsync(worker);

        // Simulate a worker that committed the effects but crashed before completing the message.
        await fixture.WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAtUtc, (DateTimeOffset?)null)));
        await DrainAsync(worker);

        var message = await MessageAsync(messageId);
        Assert.NotNull(message.ProcessedAtUtc);
        Assert.Equal(2, message.DeliveryCount);
        Assert.Equal(1, await ReimbursementsForAsync(expenseId));
        Assert.Equal(1, await NotificationsForMessageAsync(messageId));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditEvents.CountAsync(
            a => a.ResourceId == expenseId && a.Action == "reimbursement.scheduled")));
    }

    [Fact]
    public async Task Partial_failure_retries_only_the_handler_that_failed()
    {
        var clock = Clock();
        await using (var plain = Worker(clock)) await DrainAsync(plain);
        var (expenseId, messageId) = await PostedExpenseWithEventAsync();

        // The reimbursement (first handler) commits; the notification (second) then times out once for this expense.
        var faults = new FaultSwitch();
        await using var worker = Worker(clock, services =>
        {
            services.Remove(services.Single(d => d.ImplementationType == typeof(ExpenseNotificationHandler)));
            services.AddSingleton(faults);
            services.AddScoped<IIntegrationEventHandler>(sp => new FailingOnceFor(expenseId,
                ActivatorUtilities.CreateInstance<ExpenseNotificationHandler>(sp), sp.GetRequiredService<FaultSwitch>()));
        });

        await DrainAsync(worker);
        var afterFailure = await MessageAsync(messageId);
        Assert.Null(afterFailure.ProcessedAtUtc);
        Assert.Equal(1, afterFailure.DeliveryCount);
        Assert.Null(afterFailure.LockToken);
        Assert.Equal("TimeoutException: Simulated notification service timeout.", afterFailure.LastError);
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), afterFailure.NextAttemptAtUtc); // first backoff step
        Assert.Equal(1, await ReimbursementsForAsync(expenseId));                    // committed on its own
        Assert.Equal(0, await NotificationsForMessageAsync(messageId));

        await DrainAsync(worker); // not due yet
        Assert.Equal(1, (await MessageAsync(messageId)).DeliveryCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync(worker);
        var done = await MessageAsync(messageId);
        Assert.NotNull(done.ProcessedAtUtc);
        Assert.Equal(2, done.DeliveryCount);
        Assert.Equal(1, await ReimbursementsForAsync(expenseId));      // the retry did not pay twice
        Assert.Equal(1, await NotificationsForMessageAsync(messageId));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditEvents.CountAsync(
            a => a.ResourceId == expenseId && a.Action == "reimbursement.scheduled")));
        Assert.Equal([NotificationHandler, ReimbursementHandlerName], await fixture.WithDbAsync(db => db.ProcessedMessages
            .Where(p => p.MessageId == messageId).OrderBy(p => p.Handler).Select(p => p.Handler).ToListAsync()));
    }

    [Fact]
    public async Task Concurrent_workers_deliver_each_message_once_and_pay_each_expense_once()
    {
        var clock = Clock();
        await using var worker = Worker(clock, batchSize: 1);
        await DrainAsync(worker);

        var posted = new List<(Guid ExpenseId, Guid MessageId)>();
        for (var i = 0; i < 8; i++) posted.Add(await PostedExpenseWithEventAsync(10m + i));
        // A duplicate publication for the first expense: a different message ID about the same business fact.
        var duplicate = await EventForExistingExpenseAsync(posted[0].ExpenseId, 10m);

        var processor = worker.GetRequiredService<OutboxProcessor>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (await processor.ProcessBatchAsync(CancellationToken.None) > 0) { }
        })));

        foreach (var messageId in posted.Select(p => p.MessageId).Append(duplicate))
        {
            var message = await MessageAsync(messageId);
            Assert.NotNull(message.ProcessedAtUtc);
            Assert.Equal(1, message.DeliveryCount); // claims never overlapped
        }

        foreach (var (expenseId, _) in posted) Assert.Equal(1, await ReimbursementsForAsync(expenseId));
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_cannot_settle_or_duplicate()
    {
        var clock = Clock();
        var gate = new Gate();
        await using var worker = Worker(clock, services =>
        {
            services.AddSingleton(gate);
            services.AddScoped<IIntegrationEventHandler, SlowHandler>();
        });
        await DrainAsync(worker);
        var messageId = await TestMessageAsync(SlowHandler.MessageType);
        var processor = worker.GetRequiredService<OutboxProcessor>();

        // Worker A claims the message and stalls inside the handler.
        var first = Task.Run(() => processor.ProcessBatchAsync(CancellationToken.None));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Its lease (30 s) runs out; worker B claims the message again and finishes it.
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        var completed = await MessageAsync(messageId);
        Assert.NotNull(completed.ProcessedAtUtc);
        Assert.Equal(2, completed.DeliveryCount);

        // A resumes: its commit hits the idempotency key, its completion finds a different lock. Nothing changes.
        gate.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditEvents.CountAsync(a => a.ResourceId == messageId && a.Action == "test.effect")));
        Assert.Equal(completed.ProcessedAtUtc, (await MessageAsync(messageId)).ProcessedAtUtc);

        // Settling with a stale lock token is refused outright.
        await using var scope = worker.CreateAsyncScope();
        var stale = new ClaimedMessage(new IntegrationMessage(messageId, SlowHandler.MessageType, "{}", null, 1),
            Guid.NewGuid(), clock.GetUtcNow());
        Assert.False(await scope.ServiceProvider.GetRequiredService<IMessageQueue>().DeadLetterAsync(stale, "late", CancellationToken.None));
    }

    // ---------- Retries, dead letters and replay ----------

    [Fact]
    public async Task Failing_message_backs_off_then_dead_letters_and_an_administrator_can_replay_it()
    {
        var clock = Clock();
        var faults = new FaultSwitch { Failing = true };
        await using var worker = Worker(clock, services =>
        {
            services.AddSingleton(faults);
            services.AddScoped<IIntegrationEventHandler, SwitchableHandler>();
        });
        await DrainAsync(worker);
        var messageId = await TestMessageAsync(SwitchableHandler.MessageType);

        // MaxDeliveryCount = 3: retry after 5 s, then 10 s, then dead-letter.
        foreach (var (delivery, backoff) in new[] { (1, 5), (2, 10) })
        {
            await DrainAsync(worker);
            var retrying = await MessageAsync(messageId);
            Assert.Equal(delivery, retrying.DeliveryCount);
            Assert.Equal(clock.GetUtcNow().AddSeconds(backoff), retrying.NextAttemptAtUtc);
            clock.Advance(TimeSpan.FromSeconds(backoff));
        }

        await DrainAsync(worker);
        var dead = await MessageAsync(messageId);
        Assert.NotNull(dead.DeadLetteredAtUtc);
        Assert.Null(dead.ProcessedAtUtc);
        Assert.Equal(3, dead.DeliveryCount);
        Assert.Equal("Failed after 3 deliveries. InvalidOperationException: Simulated downstream outage.", dead.LastError);
        await DrainAsync(worker);
        Assert.Equal(3, faults.Calls); // dead letters are not retried

        // Operators see it; only administrators (messaging.manage).
        using var admin = As(DemoDataSeeder.Administrator);
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.GetAsync("/api/messaging/outbox")).StatusCode);
        var status = (await admin.GetFromJsonAsync<OutboxStatusDto>("/api/messaging/outbox"))!;
        var listed = Assert.Single(status.DeadLetters, d => d.Id == messageId);
        Assert.Equal((3, dead.LastError), (listed.DeliveryCount, listed.LastError));
        Assert.True(status.DeadLettered >= 1);

        // Replay: audited, back in the queue with the same ID and a fresh delivery count.
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/messaging/outbox/{messageId}/replay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/messaging/outbox/{messageId}/replay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsync($"/api/messaging/outbox/{messageId}/replay", null)).StatusCode);
        var replayAudit = await fixture.WithDbAsync(db => db.AuditEvents.SingleAsync(a => a.ResourceId == messageId && a.Action == "message.replayed"));
        Assert.Equal(DemoDataSeeder.Administrator.UserId, replayAudit.ActorUserId);
        var replayed = await MessageAsync(messageId);
        Assert.Equal((0, (DateTimeOffset?)null), (replayed.DeliveryCount, replayed.DeadLetteredAtUtc));

        // Once the downstream problem is fixed, the replay succeeds exactly once.
        faults.Failing = false;
        await DrainAsync(worker);
        Assert.NotNull((await MessageAsync(messageId)).ProcessedAtUtc);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditEvents.CountAsync(a => a.ResourceId == messageId && a.Action == "test.effect")));
    }

    [Fact]
    public async Task Messages_that_can_never_succeed_are_dead_lettered_without_retrying()
    {
        var clock = Clock();
        var faults = new FaultSwitch();
        await using var worker = Worker(clock, services =>
        {
            services.AddSingleton(faults);
            services.AddScoped<IIntegrationEventHandler, SwitchableHandler>();
        });
        await DrainAsync(worker);

        var unknown = await TestMessageAsync("test.unknown.v1");
        var unreadable = await TestMessageAsync(IntegrationEventTypes.ExpensePosted, payload: "not json");
        // A message that kept crashing its worker arrives with its delivery budget already spent.
        var exhausted = await TestMessageAsync(SwitchableHandler.MessageType);
        await fixture.WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == exhausted)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeliveryCount, 3)));

        await DrainAsync(worker);

        Assert.StartsWith("Invalid message: No handler for message type 'test.unknown.v1'", (await MessageAsync(unknown)).LastError);
        Assert.StartsWith("Invalid message:", (await MessageAsync(unreadable)).LastError);
        Assert.Equal("Exceeded 3 deliveries without completing.", (await MessageAsync(exhausted)).LastError);
        foreach (var id in new[] { unknown, unreadable })
        {
            var message = await MessageAsync(id);
            Assert.NotNull(message.DeadLetteredAtUtc);
            Assert.Equal(1, message.DeliveryCount);
        }

        Assert.Equal(0, faults.Calls);
    }

    [Fact]
    public async Task The_database_refuses_a_second_reimbursement_for_one_expense()
    {
        var (expenseId, _) = await PostedExpenseWithEventAsync();
        await fixture.WithDbAsync(async db =>
        {
            db.Reimbursements.Add(Reimbursement.Schedule(expenseId, DemoDataSeeder.Employee.UserId, 20m, "USD", Guid.NewGuid(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            db.Reimbursements.Add(Reimbursement.Schedule(expenseId, DemoDataSeeder.Employee.UserId, 20m, "USD", Guid.NewGuid(), DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return 0;
        });
    }

    // ---------- Hosting and data helpers ----------

    /// <summary>Starts ahead of real time so messages the API wrote moments ago are due.</summary>
    private static MutableClock Clock() => new(DateTimeOffset.UtcNow.AddMinutes(1));

    /// <summary>The worker's composition (AddInfrastructure), with a test clock and optional extra handlers.</summary>
    private ServiceProvider Worker(MutableClock clock, Action<IServiceCollection>? configure = null, int batchSize = 20)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinOps"] = fixture.ConnectionString,
            ["Messaging:MaxDeliveryCount"] = "3",
            ["Messaging:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Messaging:LeaseSeconds"] = "30",
            ["Messaging:RetryBaseSeconds"] = "5",
            ["Messaging:RetryMaxSeconds"] = "60",
        }).Build());
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddInfrastructure();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task DrainAsync(ServiceProvider worker)
    {
        var processor = worker.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 100 && await processor.ProcessBatchAsync(CancellationToken.None) > 0; i++) { }
    }

    private Task<List<OutboxMessage>> EventsForAsync(Guid expenseId) => fixture.WithDbAsync(db => db.OutboxMessages
        .AsNoTracking().Where(m => m.Payload.Contains(expenseId.ToString())).OrderBy(m => m.OccurredAtUtc).ToListAsync());

    private Task<OutboxMessage> MessageAsync(Guid id) =>
        fixture.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id));

    private Task<int> ReimbursementsForAsync(Guid expenseId) =>
        fixture.WithDbAsync(db => db.Reimbursements.CountAsync(r => r.ExpenseRequestId == expenseId));

    private Task<int> NotificationsForMessageAsync(Guid messageId) =>
        fixture.WithDbAsync(db => db.Notifications.CountAsync(n => n.SourceMessageId == messageId));

    /// <summary>A posted Engineering expense of Demo Employee and its expense.posted event, as the API would write them.</summary>
    private Task<(Guid ExpenseId, Guid MessageId)> PostedExpenseWithEventAsync(decimal amount = 20m) =>
        fixture.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var expense = ExpenseRequest.CreateDraft(Guid.NewGuid(), DemoDataSeeder.Employee.UserId,
                DemoDataSeeder.EngineeringDepartment, amount, "USD", ExpenseCategory.Supplies, "Messaging test",
                DateOnly.FromDateTime(now.UtcDateTime), now);
            db.ExpenseRequests.Add(expense);
            expense.Submit(now);
            db.ApprovalDecisions.Add(expense.Approve(DemoDataSeeder.EngineeringManager.UserId, null, now));
            expense.Post(DemoDataSeeder.FinanceOfficer.UserId, now);
            var message = PostedEvent(expense.Id, amount, now);
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
            return (expense.Id, message.Id);
        });

    private Task<Guid> EventForExistingExpenseAsync(Guid expenseId, decimal amount) => fixture.WithDbAsync(async db =>
    {
        var message = PostedEvent(expenseId, amount, DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    });

    private static OutboxMessage PostedEvent(Guid expenseId, decimal amount, DateTimeOffset now) =>
        OutboxMessage.Create(Guid.NewGuid(), IntegrationEventTypes.ExpensePosted,
            new ExpenseStatusChanged(expenseId, DemoDataSeeder.Employee.UserId, DemoDataSeeder.EngineeringDepartment,
                amount, "USD", DemoDataSeeder.FinanceOfficer.UserId, now).Serialize(), "messaging-test", now);

    private Task<Guid> TestMessageAsync(string type, string payload = "{}") => fixture.WithDbAsync(async db =>
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), type, payload, "messaging-test", DateTimeOffset.UtcNow);
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    });

    private HttpClient As(DemoPersona persona) =>
        ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    private async Task<ExpenseDetailDto> CreateAsync(DemoPersona persona)
    {
        using var client = As(persona);
        var response = await client.PostAsJsonAsync("/api/expenses", new
        {
            departmentId = DemoDataSeeder.EngineeringDepartment, amount = 42.50m, currency = "USD", category = "Travel",
            description = "Messaging HTTP test", expenseDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    private async Task<HttpResponseMessage> ActAsync(DemoPersona persona, Guid id, string action, string etag)
    {
        using var client = As(persona);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{id}/{action}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private async Task<ExpenseDetailDto> MustAsync(DemoPersona persona, ExpenseDetailDto expense, string action)
    {
        var response = await ActAsync(persona, expense.Id, action, expense.Version);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    private async Task<ExpenseDetailDto> SubmittedByEmployeeAsync() =>
        await MustAsync(DemoDataSeeder.Employee, await CreateAsync(DemoDataSeeder.Employee), ExpenseActions.Submit);

    // ---------- Test doubles ----------

    internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class FaultSwitch
    {
        private int calls;
        public volatile bool Failing;
        public int Calls => calls;
        public void Count() => Interlocked.Increment(ref calls);
    }

    /// <summary>Wraps a real handler and throws a transient error the first time it sees one expense.</summary>
    private sealed class FailingOnceFor(Guid expenseId, IIntegrationEventHandler inner, FaultSwitch faults) : IIntegrationEventHandler
    {
        public string Name => inner.Name;
        public bool CanHandle(string messageType) => inner.CanHandle(messageType);

        public Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken)
        {
            if (ExpenseStatusChanged.Deserialize(message.Payload).ExpenseId == expenseId && faults.Calls == 0)
            {
                faults.Count();
                throw new TimeoutException("Simulated notification service timeout.");
            }

            return inner.HandleAsync(message, cancellationToken);
        }
    }

    /// <summary>Fails while the switch is on; otherwise records one audit row as its business effect.</summary>
    private sealed class SwitchableHandler(FaultSwitch faults, IMessageEffects effects, TimeProvider clock) : IIntegrationEventHandler
    {
        public const string MessageType = "test.switchable.v1";
        public string Name => "test.switchable";
        public bool CanHandle(string messageType) => messageType == MessageType;

        public Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken)
        {
            faults.Count();
            if (faults.Failing) throw new InvalidOperationException("Simulated downstream outage.");
            effects.Add(AuditEvent.Record(clock.GetUtcNow(), null, "test.effect", "outbox-message", message.Id,
                AuditOutcome.Succeeded, message.CorrelationId));
            return Task.CompletedTask;
        }
    }

    private sealed class Gate
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
    }

    /// <summary>Records its effect, then stalls on its first call until released.</summary>
    private sealed class SlowHandler(Gate gate, IMessageEffects effects, TimeProvider clock) : IIntegrationEventHandler
    {
        public const string MessageType = "test.slow.v1";
        public string Name => "test.slow";
        public bool CanHandle(string messageType) => messageType == MessageType;

        public async Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken)
        {
            effects.Add(AuditEvent.Record(clock.GetUtcNow(), null, "test.effect", "outbox-message", message.Id,
                AuditOutcome.Succeeded, message.CorrelationId));
            if (Interlocked.Increment(ref gate.Calls) == 1)
            {
                gate.Entered.SetResult();
                await gate.Release.Task;
            }
        }
    }
}
