using FinOps.Domain.Expenses;

namespace FinOps.Domain.Budgets;

/// <summary>
/// A department's spending budget for one calendar month. One budget per department and month
/// (unique in the database). Actual spend is never stored here; it is computed from expenses by
/// <c>finance.usp_BudgetVsActual</c>, so budget and actuals cannot drift apart.
/// </summary>
public sealed class Budget
{
    public const decimal MaximumAmount = 100_000_000m;
    public const int MinimumYear = 2000;
    public const int MaximumYear = 2100;

    private Budget() { }

    public Guid Id { get; private set; }
    public Guid DepartmentId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Budget Create(Guid id, Guid departmentId, int year, int month, decimal amount, string currency,
        DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("A budget ID is required.", nameof(id));
        if (departmentId == Guid.Empty) throw new ArgumentException("A department is required.", nameof(departmentId));
        if (year is < MinimumYear or > MaximumYear)
            throw new ArgumentException($"Year must be between {MinimumYear} and {MaximumYear}.", nameof(year));
        if (month is < 1 or > 12) throw new ArgumentException("Month must be between 1 and 12.", nameof(month));
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.", nameof(nowUtc));
        ValidateAmount(amount, currency);

        return new Budget
        {
            Id = id,
            DepartmentId = departmentId,
            Year = year,
            Month = month,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public void ChangeAmount(decimal amount, DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.", nameof(nowUtc));
        ValidateAmount(amount, Currency);
        Amount = amount;
        UpdatedAtUtc = nowUtc;
    }

    private static void ValidateAmount(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (!Money.SupportedCurrencies.Contains(currency.ToUpperInvariant()))
            throw new ArgumentException($"Currency must be one of: {string.Join(", ", Money.SupportedCurrencies)}.", nameof(currency));
        if (amount <= 0) throw new ArgumentException("The budget must be greater than zero.", nameof(amount));
        if (amount > MaximumAmount) throw new ArgumentException($"The budget cannot exceed {MaximumAmount:N0}.", nameof(amount));
        if (decimal.Round(amount, 2) != amount) throw new ArgumentException("The budget can have at most two decimal places.", nameof(amount));
    }
}
