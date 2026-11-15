using FinOps.Api.Security;
using FinOps.Application.Identity;
using FinOps.Application.Messaging;

namespace FinOps.Api;

/// <summary>Notification inbox (every provisioned user, own items only) and outbox operations (administrators).</summary>
internal static class MessagingEndpoints
{
    internal static RouteGroupBuilder MapMessagingEndpoints(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("/", async (CurrentUserAccess access, INotificationInbox inbox, CancellationToken ct) =>
                await access.GetAsync() is { } user ? Results.Ok(await inbox.GetAsync(user.Id, ct)) : Results.Forbid())
            .WithName("GetNotifications")
            .WithSummary($"The caller's newest {INotificationInbox.MaximumItems} notifications and their unread count.")
            .Produces<NotificationInboxDto>();

        notifications.MapPost("/{id:guid}/read", async (Guid id, CurrentUserAccess access, INotificationInbox inbox,
                TimeProvider clock, CancellationToken ct) =>
            {
                if (await access.GetAsync() is not { } user) return Results.Forbid();
                return await inbox.MarkReadAsync(user.Id, id, clock.GetUtcNow(), ct)
                    ? Results.NoContent()
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
            })
            .WithName("MarkNotificationRead")
            .WithSummary("Marks one of the caller's notifications as read. Idempotent; someone else's is a 404.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var outbox = api.MapGroup("/messaging").WithTags("Messaging")
            .RequireAuthorization(ApplicationPermissions.MessagingManage);

        outbox.MapGet("/outbox", async (IOutboxOperations operations, CancellationToken ct) =>
                Results.Ok(await operations.GetStatusAsync(ct)))
            .WithName("GetOutboxStatus")
            .WithSummary("Pending and dead-lettered message counts, and the newest dead letters (no payloads).")
            .Produces<OutboxStatusDto>();

        outbox.MapPost("/outbox/{id:guid}/replay", async (Guid id, HttpContext http, CurrentUserAccess access,
                IOutboxOperations operations, TimeProvider clock, CancellationToken ct) =>
            {
                if (await ExpenseEndpoints.ActorAsync(http, access) is not { } actor) return Results.Forbid();
                return await operations.ReplayAsync(id, actor.User.Id, actor.CorrelationId, clock.GetUtcNow(), ct)
                    ? Results.Accepted()
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such dead letter");
            })
            .WithName("ReplayDeadLetter")
            .WithSummary("Returns a dead-lettered message to the queue with the same ID, so handlers that already "
                + "committed are not repeated. Audited.")
            .Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound);

        return notifications;
    }
}
