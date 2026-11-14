using FinOps.Api.Security;
using FinOps.Application.Budgets;
using FinOps.Application.Identity;

namespace FinOps.Api;

/// <summary>
/// Budgets and the budget-vs-actual report. Reads are scoped on the server: managers see their active
/// departments, finance and administrators see all. Only finance (budgets.manage) changes budgets.
/// </summary>
internal static class BudgetEndpoints
{
    internal static void MapBudgetEndpoints(this RouteGroupBuilder api)
    {
        var budgets = api.MapGroup("/budgets").WithTags("Budgets");

        budgets.MapGet("/", async (int? year, HttpContext http, CurrentUserAccess access, BudgetService service,
                CancellationToken ct) =>
            {
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ExpenseEndpoints.ToHttp(http, await service.ListAsync(actor, year ?? DateTime.UtcNow.Year, ct));
            })
            .WithName("ListBudgets")
            .WithSummary("Monthly budgets for a year (default: current), limited to the departments you may see.")
            .Produces<IReadOnlyList<BudgetDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesValidationProblem();

        budgets.MapPost("/", async (CreateBudgetInput input, HttpContext http, CurrentUserAccess access,
                BudgetService service, CancellationToken ct) =>
            {
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ExpenseEndpoints.ToHttp(http, await service.CreateAsync(actor, input, ct));
            })
            .RequireAuthorization(ApplicationPermissions.BudgetsManage)
            .WithName("CreateBudget")
            .WithSummary("Sets a department's budget for a month (one per department and month; 409 if it exists).")
            .Produces<BudgetDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        budgets.MapPut("/{id:guid}", async (Guid id, UpdateBudgetInput input, HttpContext http, CurrentUserAccess access,
                BudgetService service, CancellationToken ct) =>
            {
                if (ExpenseEndpoints.MissingIfMatch(http) is { } missing) return missing;
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ExpenseEndpoints.ToHttp(http, await service.UpdateAsync(actor, id, http.Request.Headers.IfMatch, input, ct));
            })
            .RequireAuthorization(ApplicationPermissions.BudgetsManage)
            .WithName("UpdateBudget")
            .WithSummary("Changes a budget amount. Requires If-Match.")
            .Produces<BudgetDto>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status428PreconditionRequired);

        budgets.MapGet("/vs-actual", async (int? year, int? month, HttpContext http, CurrentUserAccess access,
                BudgetService service, CancellationToken ct) =>
            {
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ExpenseEndpoints.ToHttp(http,
                    await service.BudgetVsActualAsync(actor, year ?? DateTime.UtcNow.Year, month, ct));
            })
            .WithName("BudgetVsActual")
            .WithSummary("Budget vs. posted, committed (approved) and pending (submitted) spend per department and month. "
                + "Runs finance.usp_BudgetVsActual with a server-resolved department scope.")
            .Produces<IReadOnlyList<BudgetVsActualRow>>().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesValidationProblem();
    }
}
