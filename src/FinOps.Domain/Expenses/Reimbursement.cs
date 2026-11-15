namespace FinOps.Domain.Expenses;

/// <summary>
/// The payment owed to an employee for a posted expense. Exactly one per expense: the background worker creates it
/// from the <c>expense.posted</c> event, and a unique index on <see cref="ExpenseRequestId"/> backs that up in the
/// database. Executing the payment (bank file, payroll) is out of scope; the record is the business effect that
/// must never be duplicated.
/// </summary>
public sealed class Reimbursement
{
    private Reimbursement() { }

    public Guid Id { get; private set; }
    public Guid ExpenseRequestId { get; private set; }
    public Guid PayeeId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTimeOffset ScheduledAtUtc { get; private set; }
    public Guid SourceMessageId { get; private set; }

    public static Reimbursement Schedule(Guid expenseRequestId, Guid payeeId, decimal amount, string currency,
        Guid sourceMessageId, DateTimeOffset nowUtc)
    {
        if (expenseRequestId == Guid.Empty) throw new ArgumentException("An expense is required.", nameof(expenseRequestId));
        if (payeeId == Guid.Empty) throw new ArgumentException("A payee is required.", nameof(payeeId));
        if (sourceMessageId == Guid.Empty) throw new ArgumentException("A source message is required.", nameof(sourceMessageId));
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.", nameof(nowUtc));
        Money.Validate(amount, currency);

        return new Reimbursement
        {
            Id = Guid.NewGuid(),
            ExpenseRequestId = expenseRequestId,
            PayeeId = payeeId,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            SourceMessageId = sourceMessageId,
            ScheduledAtUtc = nowUtc
        };
    }
}
