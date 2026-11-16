using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinOps.Domain.Audit;
using FinOps.Domain.Budgets;
using FinOps.Domain.Expenses;
using FinOps.Domain.Identity;
using FinOps.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

/// <summary>What <see cref="DemoExpenseGenerator.GenerateAsync"/> inserted.</summary>
public sealed record DemoExpenseResult(int Expenses, int AlreadyPresent, IReadOnlyDictionary<ExpenseStatus, int> ByStatus,
    int Notifications, int Budgets);

/// <summary>
/// Development only: a few months of realistic expense history for every demo department, so the dashboard, approval
/// queues and every report have data. Each expense is driven through the domain methods (create, submit, approve or
/// reject, post), so it obeys the same rules as a real one (separation of duties, no future or year-old dates), and
/// the side effects the app would have produced are added too: approval decisions, audit events, notifications and
/// reimbursements. Deterministic (fixed random seed) and idempotent: expense IDs are derived from their slot, so a
/// rerun inserts only what is missing.
/// </summary>
public static class DemoExpenseGenerator
{
    /// <summary>A requester for Demo Operations, which otherwise has no active member.</summary>
    public static readonly DemoPersona OperationsLead = new(
        Guid.Parse("10000000-0000-0000-0000-000000000007"), Guid.Parse("20000000-0000-0000-0000-000000000007"),
        "Demo Operations Lead", "ops-lead@example.invalid", true, "Employee");

    private const string CorrelationId = "seed-demo-expenses";
    public const int DefaultExtraPerMonth = 3;
    public const int MaximumExtraPerMonth = 20;

    private sealed record Requester(Guid UserId, Guid DepartmentId, Guid Approver, int PerMonth);

    private static readonly (ExpenseCategory Category, decimal Min, decimal Max, string[] Descriptions)[] Catalog =
    [
        (ExpenseCategory.Travel, 60, 850, ["Flight to the regional office", "Train to the client workshop", "Taxi from the airport", "Car rental for the site visit", "Mileage for customer meetings"]),
        (ExpenseCategory.Lodging, 120, 640, ["Hotel for the partner summit", "Two nights near the data centre", "Hotel during the onboarding week"]),
        (ExpenseCategory.Meals, 14, 160, ["Team lunch after the release", "Client dinner", "Working lunch during the audit", "Coffee for the planning session"]),
        (ExpenseCategory.Supplies, 9, 240, ["Whiteboard markers and notebooks", "Replacement keyboard", "USB-C adapters for the meeting room", "Printer toner"]),
        (ExpenseCategory.Software, 9, 320, ["Diagramming tool subscription", "Password manager seat", "Design assets licence", "API testing tool subscription"]),
        (ExpenseCategory.Training, 45, 1150, ["Online secure coding course", "Certification exam fee", "Conference ticket", "Excel modelling workshop"]),
        (ExpenseCategory.Other, 10, 180, ["Courier for signed contracts", "Parking during the offsite", "Visa processing fee"]),
    ];

    private static readonly string[] RejectionReasons =
    [
        "Please attach the itemised receipt.", "This should go through the purchasing card instead.",
        "Over the meal limit; resubmit the policy amount.", "Duplicate of an expense already submitted.",
    ];

    /// <param name="months">How many months back, including the current one (1–11, so no date is a year old).</param>
    /// <param name="extraRequesterObjectId">Optional: also give this provisioned user some expenses of their own.</param>
    /// <param name="extraPerMonth">How many expenses per month for that user (1–20). Raising it on a rerun adds the difference.</param>
    public static async Task<DemoExpenseResult> GenerateAsync(FinOpsDbContext db, int months,
        Guid? extraRequesterObjectId = null, DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default,
        int extraPerMonth = DefaultExtraPerMonth)
    {
        if (months is < 1 or > 11) throw new ArgumentOutOfRangeException(nameof(months), "Use 1 to 11 months.");
        if (extraPerMonth is < 1 or > MaximumExtraPerMonth)
            throw new ArgumentOutOfRangeException(nameof(extraPerMonth), $"Use 1 to {MaximumExtraPerMonth} expenses per month.");
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        // Validate the optional real user before writing anything.
        var extra = extraRequesterObjectId is { } objectId ? await ExtraRequesterAsync(db, objectId, extraPerMonth, now, cancellationToken) : null;
        await DemoDataSeeder.SeedAsync(db, cancellationToken);
        await EnsureOperationsLeadAsync(db, now, cancellationToken);

        var finance = DemoDataSeeder.FinanceOfficer.UserId;
        var admin = DemoDataSeeder.Administrator.UserId;
        List<Requester> requesters =
        [
            new(DemoDataSeeder.Employee.UserId, DemoDataSeeder.EngineeringDepartment, DemoDataSeeder.EngineeringManager.UserId, 7),
            new(DemoDataSeeder.EngineeringManager.UserId, DemoDataSeeder.EngineeringDepartment, admin, 3),
            new(DemoDataSeeder.FinanceOfficer.UserId, DemoDataSeeder.FinanceDepartment, DemoDataSeeder.Manager.UserId, 4),
            new(DemoDataSeeder.Manager.UserId, DemoDataSeeder.FinanceDepartment, admin, 3),
            new(OperationsLead.UserId, DemoDataSeeder.OperationsDepartment, admin, 8),
        ];
        if (extra is not null) requesters.Add(extra);

        var budgets = await EnsureBudgetsAsync(db, now, months, cancellationToken);

        var random = new Random(42);
        var planned = new List<(Guid Id, Requester Who, DateOnly Date, int Slot)>();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        foreach (var who in requesters)
        {
            for (var m = 0; m < months; m++)
            {
                var monthStart = firstMonth.AddMonths(m);
                var lastDay = monthStart.AddMonths(1).AddDays(-1);
                if (lastDay > today) lastDay = today;
                for (var i = 0; i < who.PerMonth; i++)
                {
                    var date = monthStart.AddDays(random.Next(lastDay.DayNumber - monthStart.DayNumber + 1));
                    planned.Add((SlotId($"{who.UserId}|{monthStart:yyyy-MM}|{i}"), who, date, i));
                }
            }
        }

        var ids = planned.Select(p => p.Id).ToList();
        var existing = (await db.ExpenseRequests.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();

        var byStatus = Enum.GetValues<ExpenseStatus>().ToDictionary(s => s, _ => 0);
        var notifications = 0;
        foreach (var (id, who, date, slot) in planned)
        {
            // Draw every random value even for existing slots, so the remaining slots don't change between runs.
            var (category, min, max, descriptions) = Catalog[random.Next(Catalog.Length)];
            var amount = Math.Round(min + (decimal)random.NextDouble() * (max - min), 2);
            var description = descriptions[random.Next(descriptions.Length)];
            var roll = random.NextDouble();
            var delays = Enumerable.Range(0, 4).Select(_ => TimeSpan.FromHours(random.Next(2, 72))).ToArray();
            if (existing.Contains(id)) continue;

            var expense = Build(db, id, who, date, category, amount, description, roll, delays, now, finance, admin, ref notifications);
            byStatus[expense.Status]++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DemoExpenseResult(planned.Count - existing.Count, existing.Count, byStatus, notifications, budgets);
    }

    /// <summary>Creates one expense and walks it as far as its dates (and the roll of the dice) allow.</summary>
    private static ExpenseRequest Build(FinOpsDbContext db, Guid id, Requester who, DateOnly date, ExpenseCategory category,
        decimal amount, string description, double roll, TimeSpan[] delays, DateTimeOffset now, Guid finance, Guid admin,
        ref int notifications)
    {
        var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero) + delays[0];
        if (at > now) at = now;
        var expense = ExpenseRequest.CreateDraft(id, who.UserId, who.DepartmentId, amount, "USD", category, description, date, at);
        db.ExpenseRequests.Add(expense);
        Audit(db, at, who.UserId, "expense.created", id, ExpenseStatus.Draft);

        // ~4% stay drafts, ~3% are cancelled drafts.
        if (roll < 0.04) return expense;
        if (roll < 0.07) { Step(ref at, delays[1], now, out var ok); if (ok) { expense.Cancel(at); Audit(db, at, who.UserId, "expense.cancelled", id, expense.Status); } return expense; }

        if (!Step(ref at, delays[1], now)) return expense;
        expense.Submit(at);
        Audit(db, at, who.UserId, "expense.submitted", id, expense.Status);

        if (!Step(ref at, delays[2], now)) return expense;
        var rejected = roll < 0.15;
        var decision = rejected
            ? expense.Reject(who.Approver, RejectionReasons[(int)(roll * 1000) % RejectionReasons.Length], at)
            : expense.Approve(who.Approver, roll < 0.5 ? "Approved." : null, at);
        db.ApprovalDecisions.Add(decision);
        Audit(db, at, who.Approver, rejected ? "expense.rejected" : "expense.approved", id, expense.Status);
        notifications += Notify(db, who.UserId, id, rejected ? NotificationKind.ExpenseRejected : NotificationKind.ExpenseApproved, at, now);
        if (rejected) return expense;

        // ~5% of approved expenses wait for finance even when old enough to be posted.
        if (roll > 0.95 || !Step(ref at, delays[3], now)) return expense;
        var poster = who.UserId == finance ? admin : finance;
        expense.Post(poster, at);
        Audit(db, at, poster, "expense.posted", id, expense.Status);
        notifications += Notify(db, who.UserId, id, NotificationKind.ExpensePosted, at, now);
        db.Reimbursements.Add(Reimbursement.Schedule(id, who.UserId, amount, "USD", SlotId($"reimburse|{id}"), at.AddMinutes(1) > now ? now : at.AddMinutes(1)));
        return expense;
    }

    /// <summary>Moves the clock forward; false (and no move) when the step would be in the future.</summary>
    private static bool Step(ref DateTimeOffset at, TimeSpan delay, DateTimeOffset now)
    {
        Step(ref at, delay, now, out var ok);
        return ok;
    }

    private static void Step(ref DateTimeOffset at, TimeSpan delay, DateTimeOffset now, out bool ok)
    {
        ok = at + delay <= now;
        if (ok) at += delay;
    }

    private static int Notify(FinOpsDbContext db, Guid userId, Guid expenseId, NotificationKind kind, DateTimeOffset at,
        DateTimeOffset now)
    {
        var notification = Notification.Create(userId, expenseId, kind, SlotId($"notify|{expenseId}|{kind}"), at);
        if (now - at > TimeSpan.FromDays(5)) notification.MarkRead(at.AddDays(1)); // older updates have been seen
        db.Notifications.Add(notification);
        return 1;
    }

    private static void Audit(FinOpsDbContext db, DateTimeOffset at, Guid actor, string action, Guid expenseId, ExpenseStatus to) =>
        db.AuditEvents.Add(AuditEvent.Record(at, actor, action, "expense", expenseId, AuditOutcome.Succeeded, CorrelationId,
            JsonSerializer.Serialize(new { to = to.ToString(), seeded = true })));

    /// <summary>
    /// Budgets for every generated month, sized to the generated spending so most months are under budget and a few
    /// are over. A month that still has the small placeholder budget from seed-demo-data is resized; a budget anyone
    /// changed is left alone.
    /// </summary>
    private static async Task<int> EnsureBudgetsAsync(FinOpsDbContext db, DateTimeOffset now, int months,
        CancellationToken cancellationToken)
    {
        var monthly = new Dictionary<Guid, (decimal Target, decimal Placeholder)>
        {
            [DemoDataSeeder.EngineeringDepartment] = (4_000m, 1_000m),
            [DemoDataSeeder.FinanceDepartment] = (2_000m, 1_500m),
            [DemoDataSeeder.OperationsDepartment] = (3_000m, 2_000m),
        };
        var start = new DateOnly(now.Year, now.Month, 1).AddMonths(-(months - 1));
        var added = 0;
        for (var m = 0; m < months; m++)
        {
            var month = start.AddMonths(m);
            foreach (var (department, (target, placeholder)) in monthly)
            {
                var existing = await db.Budgets.SingleOrDefaultAsync(
                    b => b.DepartmentId == department && b.Year == month.Year && b.Month == month.Month, cancellationToken);
                if (existing is null)
                    db.Budgets.Add(Budget.Create(Guid.NewGuid(), department, month.Year, month.Month, target, "USD", now));
                else if (existing.Amount == placeholder)
                    existing.ChangeAmount(target, now);
                else
                    continue;
                added++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private static async Task EnsureOperationsLeadAsync(FinOpsDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = OperationsLead;
        if (!await db.UserProfiles.AnyAsync(u => u.Id == user.UserId, cancellationToken))
            db.UserProfiles.Add(UserProfile.Provision(user.UserId, user.EntraObjectId, user.DisplayName, user.Email));
        if (!await db.UserRoles.AnyAsync(r => r.UserId == user.UserId, cancellationToken))
            db.UserRoles.Add(UserRole.Assign(user.UserId, SyntheticIdentityIds.EmployeeRole));
        if (!await db.DepartmentMemberships.AnyAsync(m => m.UserId == user.UserId, cancellationToken))
            db.DepartmentMemberships.Add(DepartmentMembership.Create(user.UserId, DemoDataSeeder.OperationsDepartment,
                new DateTimeOffset(now.Year - 1, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A provisioned real user (e.g. a Google or Entra demo account) as an extra requester in their department.</summary>
    private static async Task<Requester> ExtraRequesterAsync(FinOpsDbContext db, Guid objectId, int perMonth, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var user = await db.UserProfiles.SingleOrDefaultAsync(u => u.EntraObjectId == objectId, cancellationToken)
            ?? throw new InvalidOperationException($"No user is provisioned with object ID {objectId}. Run provision-user first.");
        var departmentId = await db.DepartmentMemberships
            // Any membership that hasn't ended: a user provisioned today still gets back-dated demo history.
            .Where(m => m.UserId == user.Id && (m.EndsAtUtc == null || m.EndsAtUtc > now))
            .Select(m => (Guid?)m.DepartmentId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"{user.DisplayName} has no active department membership. Provision them with --department.");

        // Someone other than the requester approves: the department's demo manager, or the administrator.
        var approver = departmentId == DemoDataSeeder.EngineeringDepartment ? DemoDataSeeder.EngineeringManager.UserId
            : departmentId == DemoDataSeeder.FinanceDepartment ? DemoDataSeeder.Manager.UserId
            : DemoDataSeeder.Administrator.UserId;
        if (approver == user.Id) approver = DemoDataSeeder.Administrator.UserId;
        return new Requester(user.Id, departmentId, approver, perMonth);
    }

    /// <summary>A stable GUID for a named slot, so reruns find what they already inserted.</summary>
    private static Guid SlotId(string slot)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{CorrelationId}|{slot}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
