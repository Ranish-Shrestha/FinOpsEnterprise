namespace FinOps.Application.Common;

/// <summary>Standard collection envelope for every paged endpoint.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    /// <summary>Out-of-range values are rejected (400), never silently clamped.</summary>
    public static void Validate(int page, int pageSize, IDictionary<string, string[]> errors)
    {
        if (page < 1) errors["page"] = ["Page must be 1 or greater."];
        if (pageSize is < 1 or > MaximumPageSize) errors["pageSize"] = [$"Page size must be between 1 and {MaximumPageSize}."];
    }
}

/// <summary>Outcome of an application command or query, mapped to HTTP status codes by the API.</summary>
public enum ResultKind { Ok, Created, Invalid, NotFound, Forbidden, Conflict, PreconditionFailed }

public sealed record Result<T>(ResultKind Kind, T? Value = default, string? Detail = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static Result<T> Ok(T value) => new(ResultKind.Ok, value);
    public static Result<T> Created(T value) => new(ResultKind.Created, value);
    public static Result<T> Invalid(IDictionary<string, string[]> errors) =>
        new(ResultKind.Invalid, Errors: new Dictionary<string, string[]>(errors));
    public static Result<T> Invalid(string field, string message) =>
        new(ResultKind.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });
    public static Result<T> NotFound() => new(ResultKind.NotFound);
    public static Result<T> Forbidden(string detail) => new(ResultKind.Forbidden, Detail: detail);
    public static Result<T> Conflict(string detail) => new(ResultKind.Conflict, Detail: detail);
    public static Result<T> PreconditionFailed(string detail) => new(ResultKind.PreconditionFailed, Detail: detail);
}
