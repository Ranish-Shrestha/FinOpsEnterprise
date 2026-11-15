using FinOps.Application.Budgets;
using FinOps.Application.Departments;
using FinOps.Application.Expenses;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FinOps.Application.Identity;
using FinOps.Application.Messaging;
using FinOps.Infrastructure.Messaging;
using FinOps.Application.Reports;
using FinOps.Infrastructure.Reporting;
using FinOps.Infrastructure.Health;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString("FinOps") ?? string.Empty)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "Configure ConnectionStrings:FinOps with user secrets or ConnectionStrings__FinOps. See README.md.")
            .ValidateOnStart();

        services.AddDbContext<FinOpsDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString,
                sql => sql.CommandTimeout(10)));
        services.AddScoped<IDepartmentReader, DepartmentReader>();
        services.AddScoped<IUserAccessReader, UserAccessReader>();
        services.AddScoped<IExpenseStore, ExpenseStore>();
        services.AddScoped<IExpenseReader, ExpenseReader>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<ExpenseWorkflow>();
        services.AddScoped<IBudgetStore, BudgetStore>();
        services.AddScoped<IBudgetReader, BudgetReader>();
        services.AddScoped<BudgetService>();
        services.AddScoped<IReportReader, ReportReader>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddSingleton<IReportRenderer, PdfReportRenderer>();
        services.AddSingleton<IReportRenderer, ExcelReportRenderer>();
        services.AddScoped<ReportService>();

        // Messaging: the outbox is written by ExpenseWorkflow; the processor runs in FinOps.Worker (AddOutboxWorker).
        services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.SectionName)
            .Validate(o => o.MaxDeliveryCount is >= 1 and <= 100 && o.BatchSize is >= 1 and <= 500
                && o.LeaseSeconds >= 5 && o.PollIntervalSeconds > 0 && o.RetryBaseSeconds >= 1
                && o.RetryMaxSeconds >= o.RetryBaseSeconds, "Messaging options are out of range.")
            .ValidateOnStart();
        services.AddScoped<IMessageQueue, SqlOutboxQueue>();
        services.AddScoped<IMessageEffects, MessageEffects>();
        // Handlers run in registration order and a failure stops the rest until the retry. The reimbursement goes
        // first, so the "posted, reimbursement scheduled" notification can never appear before the money effect.
        services.AddScoped<IIntegrationEventHandler, ReimbursementHandler>();
        services.AddScoped<IIntegrationEventHandler, ExpenseNotificationHandler>();
        services.AddSingleton<OutboxProcessor>();
        services.AddScoped<INotificationInbox, NotificationInbox>();
        services.AddScoped<IOutboxOperations, OutboxOperations>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(
            "database", tags: ["ready"], timeout: TimeSpan.FromSeconds(15));
        return services;
    }

    /// <summary>Runs the outbox processor as a hosted service. Used by FinOps.Worker, never by the API.</summary>
    public static IServiceCollection AddOutboxWorker(this IServiceCollection services)
    {
        services.AddHostedService<OutboxWorker>();
        return services;
    }
}
