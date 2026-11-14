using FinOps.Application.Budgets;
using FinOps.Application.Departments;
using FinOps.Application.Expenses;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FinOps.Application.Identity;
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(
            "database", tags: ["ready"], timeout: TimeSpan.FromSeconds(15));
        return services;
    }
}
