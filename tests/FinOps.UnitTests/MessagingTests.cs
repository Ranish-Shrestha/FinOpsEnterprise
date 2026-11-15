using System.Text.Json;
using FinOps.Application.Messaging;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Messaging;
using FinOps.Domain.Notifications;

namespace FinOps.UnitTests;

public sealed class MessagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Expense = Guid.Parse("50000000-0000-0000-0000-0000000000aa");
    private static readonly Guid Requester = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static IntegrationMessage Message(string type, decimal amount = 42.5m) => new(Guid.NewGuid(), type,
        new ExpenseStatusChanged(Expense, Requester, Guid.NewGuid(), amount, "USD", Guid.NewGuid(), Now).Serialize(),
        "corr-1", 1);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeEffects(bool reimbursementExists = false) : IMessageEffects
    {
        public List<object> Added { get; } = [];
        public void Add(Notification notification) => Added.Add(notification);
        public void Add(Reimbursement reimbursement) => Added.Add(reimbursement);
        public void Add(AuditEvent auditEvent) => Added.Add(auditEvent);
        public Task<bool> ReimbursementExistsAsync(Guid expenseRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(reimbursementExists);
    }

    [Fact]
    public void Retry_delay_doubles_from_the_base_and_is_capped()
    {
        var options = new MessagingOptions { RetryBaseSeconds = 5, RetryMaxSeconds = 60 };
        var delays = Enumerable.Range(1, 6).Select(n => RetryPolicy.DelayAfter(n, options).TotalSeconds);
        Assert.Equal([5, 10, 20, 40, 60, 60], delays);
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.DelayAfter(int.MaxValue, options));
    }

    [Fact]
    public void Outbox_message_is_due_immediately_and_validates_input()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "expense.posted.v1", "{}", new string('c', 80), Now);
        Assert.Equal(Now, message.NextAttemptAtUtc);
        Assert.Equal(0, message.DeliveryCount);
        Assert.Equal(OutboxMessage.MaximumCorrelationIdLength, message.CorrelationId!.Length);

        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(Guid.Empty, "t", "{}", null, Now));
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(Guid.NewGuid(), "t", new string('x', 4001), null, Now));
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(Guid.NewGuid(), "t", "{}", null, Now.ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Outbox_message_keeps_only_a_well_formed_trace_parent()
    {
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        Assert.Equal(traceParent, OutboxMessage.Create(Guid.NewGuid(), "t", "{}", null, Now, traceParent).TraceParent);
        Assert.Null(OutboxMessage.Create(Guid.NewGuid(), "t", "{}", null, Now, "|hierarchical.id.").TraceParent);
        Assert.Null(OutboxMessage.Create(Guid.NewGuid(), "t", "{}", null, Now).TraceParent);
    }

    [Fact]
    public void Only_dead_letters_can_be_replayed()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "t", "{}", null, Now);
        Assert.Throws<InvalidOperationException>(() => message.Replay(Now));
        Assert.Equal(1000, OutboxMessage.TrimError(new string('e', 5000)).Length);
    }

    [Fact]
    public void Payload_round_trips_and_carries_no_free_text()
    {
        var payload = new ExpenseStatusChanged(Expense, Requester, Guid.NewGuid(), 12.34m, "USD", Guid.NewGuid(), Now);
        var json = payload.Serialize();
        Assert.Equal(payload, ExpenseStatusChanged.Deserialize(json));
        var names = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["expenseId", "requesterId", "departmentId", "amount", "currency", "actorUserId", "occurredAtUtc"], names);
    }

    [Theory]
    [InlineData(IntegrationEventTypes.ExpenseApproved, NotificationKind.ExpenseApproved)]
    [InlineData(IntegrationEventTypes.ExpenseRejected, NotificationKind.ExpenseRejected)]
    [InlineData(IntegrationEventTypes.ExpensePosted, NotificationKind.ExpensePosted)]
    public async Task Notification_handler_notifies_the_requester(string type, NotificationKind kind)
    {
        var effects = new FakeEffects();
        var handler = new ExpenseNotificationHandler(effects, new FixedClock());
        var message = Message(type);
        Assert.True(handler.CanHandle(type));

        await handler.HandleAsync(message, CancellationToken.None);

        var notification = Assert.IsType<Notification>(Assert.Single(effects.Added));
        Assert.Equal((Requester, Expense, kind, message.Id), (notification.UserId, notification.ExpenseRequestId,
            notification.Kind, notification.SourceMessageId));
        Assert.Null(notification.ReadAtUtc);
    }

    [Fact]
    public async Task Reimbursement_handler_pays_posted_expenses_once_and_audits_as_the_system()
    {
        var handler = new ReimbursementHandler(new FakeEffects(), new FixedClock());
        Assert.True(handler.CanHandle(IntegrationEventTypes.ExpensePosted));
        Assert.False(handler.CanHandle(IntegrationEventTypes.ExpenseApproved));

        var effects = new FakeEffects();
        var message = Message(IntegrationEventTypes.ExpensePosted, 99.99m);
        await new ReimbursementHandler(effects, new FixedClock()).HandleAsync(message, CancellationToken.None);

        var reimbursement = Assert.IsType<Reimbursement>(effects.Added[0]);
        Assert.Equal((Expense, Requester, 99.99m, "USD", message.Id), (reimbursement.ExpenseRequestId,
            reimbursement.PayeeId, reimbursement.Amount, reimbursement.Currency, reimbursement.SourceMessageId));
        var audit = Assert.IsType<AuditEvent>(effects.Added[1]);
        Assert.Null(audit.ActorUserId);
        Assert.Equal(("reimbursement.scheduled", Expense, "corr-1"), (audit.Action, audit.ResourceId, audit.CorrelationId));

        var already = new FakeEffects(reimbursementExists: true);
        await new ReimbursementHandler(already, new FixedClock()).HandleAsync(message, CancellationToken.None);
        Assert.Empty(already.Added);
    }

    [Fact]
    public async Task Unreadable_payload_is_an_invalid_message()
    {
        var handler = new ExpenseNotificationHandler(new FakeEffects(), new FixedClock());
        await Assert.ThrowsAsync<InvalidMessageException>(() => handler.HandleAsync(
            new IntegrationMessage(Guid.NewGuid(), IntegrationEventTypes.ExpensePosted, "null", null, 1), CancellationToken.None));
    }

    [Fact]
    public void Notification_read_time_is_kept_on_repeat()
    {
        var notification = Notification.Create(Requester, Expense, NotificationKind.ExpensePosted, Guid.NewGuid(), Now);
        notification.MarkRead(Now);
        notification.MarkRead(Now.AddHours(1));
        Assert.Equal(Now, notification.ReadAtUtc);
        Assert.Throws<ArgumentException>(() => Notification.Create(Guid.Empty, Expense, NotificationKind.ExpensePosted, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Reimbursement_applies_the_money_rules()
    {
        Assert.Throws<ArgumentException>(() => Reimbursement.Schedule(Expense, Requester, 0m, "USD", Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => Reimbursement.Schedule(Expense, Requester, 10m, "EUR", Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => Reimbursement.Schedule(Expense, Requester, 10m, "USD", Guid.Empty, Now));
    }
}
