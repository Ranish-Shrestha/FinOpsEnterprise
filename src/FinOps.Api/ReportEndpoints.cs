using FinOps.Api.Security;
using FinOps.Application.Common;
using FinOps.Application.Reports;

namespace FinOps.Api;

internal sealed record ReportParameters(string? Format, DateOnly? From, DateOnly? To, int? Year, int? Month);

/// <summary>
/// Report catalog and runner. Authorization and department scope are decided by <see cref="ReportService"/>
/// on the server; the query string only carries dates and the output format.
/// </summary>
internal static class ReportEndpoints
{
    internal static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports").WithTags("Reports");

        reports.MapGet("/", async (HttpContext http, CurrentUserAccess access, ReportService service) =>
                await ExpenseEndpoints.ActorAsync(http, access) is { } actor
                    ? Results.Ok(service.Catalog(actor.User))
                    : Results.Forbid())
            .WithName("ListReports")
            .WithSummary("Reports you may run, with their parameters and formats.")
            .Produces<IReadOnlyList<ReportCatalogEntry>>();

        reports.MapGet("/{key}", async (string key, [AsParameters] ReportParameters p, HttpContext http,
                CurrentUserAccess access, ReportService service, CancellationToken ct) =>
            {
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                var result = await service.RunAsync(actor, new ReportRequest(key, p.Format, p.From, p.To, p.Year, p.Month), ct);

                if (result is { Kind: ResultKind.Ok, Value: ReportFile file })
                {
                    // Reports contain financial data: never cache them anywhere.
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.File(file.Content, file.ContentType, file.FileName);
                }

                return result.Kind == ResultKind.NotFound
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown report")
                    : ExpenseEndpoints.ToHttp(http, result);
            })
            .WithName("RunReport")
            .WithSummary("Runs a report. format=json (default, preview), pdf or xlsx. Dates are yyyy-MM-dd.")
            .Produces<ReportTable>()
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
