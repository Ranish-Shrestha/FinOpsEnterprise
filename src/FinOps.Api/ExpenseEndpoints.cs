using FinOps.Api.Security;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FinOps.Api;

internal sealed record ExpenseListParameters(string? Status, Guid? DepartmentId, bool? Mine, int? Page, int? PageSize, string? Sort);

internal sealed record AuditListParameters(int? Page, int? PageSize);

/// <summary>
/// Expense workflow endpoints. Endpoint policies are a coarse first gate (permission + scope); the workflow
/// then applies resource rules (visibility, ownership, department, separation of duties, state, version).
/// </summary>
internal static class ExpenseEndpoints
{
    /// <summary>Permission required to attempt each transition. Resource rules are checked afterwards.</summary>
    private static readonly IReadOnlyDictionary<string, string> TransitionPermissions = new Dictionary<string, string>
    {
        [ExpenseActions.Submit] = ApplicationPermissions.ExpensesCreate,
        [ExpenseActions.Withdraw] = ApplicationPermissions.ExpensesCreate,
        [ExpenseActions.Cancel] = ApplicationPermissions.ExpensesCreate,
        [ExpenseActions.Revise] = ApplicationPermissions.ExpensesCreate,
        [ExpenseActions.Approve] = ApplicationPermissions.ExpensesApprove,
        [ExpenseActions.Reject] = ApplicationPermissions.ExpensesApprove,
        [ExpenseActions.Post] = ApplicationPermissions.ExpensesPost,
    };

    internal static RouteGroupBuilder MapExpenseEndpoints(this RouteGroupBuilder api)
    {
        var expenses = api.MapGroup("/expenses").WithTags("Expenses");

        expenses.MapGet("/", async ([AsParameters] ExpenseListParameters p, HttpContext http, CurrentUserAccess access,
                ExpenseWorkflow workflow, CancellationToken ct) =>
            {
                if (await ActorAsync(http, access) is not { } actor) return Results.Forbid();
                var query = new ExpenseListQuery(p.Status, p.DepartmentId, p.Mine ?? false,
                    p.Page ?? 1, p.PageSize ?? Paging.DefaultPageSize, p.Sort);
                return ToHttp(http, await workflow.ListAsync(actor, query, ct));
            })
            .WithName("ListExpenses")
            .WithSummary("Lists expenses visible to the caller: their own, their departments' (managers), or all (finance/admin).")
            .Produces<PagedResult<ExpenseSummaryDto>>().ProducesValidationProblem();

        expenses.MapGet("/{id:guid}", async (Guid id, HttpContext http, CurrentUserAccess access,
                ExpenseWorkflow workflow, CancellationToken ct) =>
            {
                if (await ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ToHttp(http, await workflow.GetAsync(actor, id, ct));
            })
            .WithName("GetExpense")
            .WithSummary("Expense detail with approval history, the caller's allowed actions, and an ETag for updates.")
            .Produces<ExpenseDetailDto>().ProducesProblem(StatusCodes.Status404NotFound);

        expenses.MapPost("/", async (CreateExpenseInput input, HttpContext http, CurrentUserAccess access,
                ExpenseWorkflow workflow, CancellationToken ct) =>
            {
                if (await ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ToHttp(http, await workflow.CreateAsync(actor, input, ct));
            })
            .RequireAuthorization(ApplicationPermissions.ExpensesCreate)
            .WithName("CreateExpense")
            .WithSummary("Creates a draft expense in a department the caller is an active member of.")
            .Produces<ExpenseDetailDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        expenses.MapPut("/{id:guid}", async (Guid id, ExpenseInput input, HttpContext http, CurrentUserAccess access,
                ExpenseWorkflow workflow, CancellationToken ct) =>
            {
                if (MissingIfMatch(http) is { } missing) return missing;
                if (await ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return ToHttp(http, await workflow.UpdateAsync(actor, id, http.Request.Headers.IfMatch, input, ct));
            })
            .RequireAuthorization(ApplicationPermissions.ExpensesCreate)
            .WithName("UpdateExpense")
            .WithSummary("Edits a draft. Requires If-Match with the ETag from the last read.")
            .Produces<ExpenseDetailDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        foreach (var (action, permission) in TransitionPermissions)
        {
            expenses.MapPost($"/{{id:guid}}/{action}", async (Guid id, HttpContext http, CurrentUserAccess access,
                    ExpenseWorkflow workflow, CancellationToken ct,
                    [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DecisionInput? body) =>
                {
                    if (MissingIfMatch(http) is { } missing) return missing;
                    if (await ActorAsync(http, access) is not { } actor) return Results.Forbid();
                    var result = await workflow.TransitionAsync(actor, id, action, http.Request.Headers.IfMatch, body?.Comment, ct);
                    return ToHttp(http, result);
                })
                .RequireAuthorization(permission)
                .WithName($"{char.ToUpperInvariant(action[0])}{action[1..]}Expense")
                .WithSummary($"Workflow transition '{action}'. Requires If-Match.")
                .Produces<ExpenseDetailDto>()
                .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        }

        api.MapGet("/audit-events", async ([AsParameters] AuditListParameters p, IAuditReader reader, CancellationToken ct) =>
            {
                var page = p.Page ?? 1;
                var pageSize = p.PageSize ?? Paging.DefaultPageSize;
                var errors = new Dictionary<string, string[]>();
                Paging.Validate(page, pageSize, errors);
                return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await reader.ListAsync(page, pageSize, ct));
            })
            .RequireAuthorization(ApplicationPermissions.AuditRead)
            .WithName("ListAuditEvents").WithTags("Audit")
            .WithSummary("Newest-first audit trail of workflow actions and denied attempts.")
            .Produces<PagedResult<AuditEventDto>>().ProducesValidationProblem();

        return expenses;
    }

    private static async Task<Actor?> ActorAsync(HttpContext http, CurrentUserAccess access) =>
        await access.GetAsync() is { } user
            ? new Actor(user, http.Response.Headers[HttpPipeline.CorrelationHeader].ToString())
            : null;

    private static IResult? MissingIfMatch(HttpContext http) =>
        string.IsNullOrWhiteSpace(http.Request.Headers.IfMatch)
            ? Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match header required",
                detail: "Send the ETag from your last read of this expense in the If-Match header.")
            : null;

    private static IResult ToHttp<T>(HttpContext http, Result<T> result)
    {
        if (result.Value is ExpenseDetailDto detail) http.Response.Headers.ETag = detail.Version;

        return result.Kind switch
        {
            ResultKind.Ok => Results.Ok(result.Value),
            ResultKind.Created when result.Value is ExpenseDetailDto created =>
                Results.Created($"/api/expenses/{created.Id}", created),
            ResultKind.Created => Results.Ok(result.Value),
            ResultKind.Invalid => Results.ValidationProblem(result.Errors!),
            ResultKind.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Expense not found"),
            ResultKind.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: result.Detail),
            ResultKind.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Invalid state", detail: result.Detail),
            ResultKind.PreconditionFailed => Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Expense was modified", detail: result.Detail),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
